//! 顯示卡相關遙測：DXGI 轉接卡枚舉、GPU Engine 效能計數器使用率，以及
//! NVIDIA 獨顯的 NVML 實測功耗。

use std::collections::HashMap;
use std::ffi::c_void;

use windows::core::{s, w, PCWSTR};
use windows::Win32::Graphics::Dxgi::{CreateDXGIFactory1, IDXGIFactory1};
use windows::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryExW, LOAD_LIBRARY_SEARCH_SYSTEM32};
use windows::Win32::System::Performance::{
    PdhAddEnglishCounterW, PdhCloseQuery, PdhCollectQueryData, PdhGetFormattedCounterArrayW, PdhOpenQueryW,
    PDH_FMT_COUNTERVALUE_ITEM_W, PDH_FMT_DOUBLE, PDH_HCOUNTER, PDH_HQUERY,
};

use crate::models::round1;

const VENDOR_NVIDIA: u32 = 0x10DE;
const DXGI_ADAPTER_FLAG_SOFTWARE: u32 = 2;
const ONE_GIGABYTE: u64 = 1 << 30;
const PDH_MORE_DATA: u32 = 0x8000_07D2;

/// 單一 DXGI 顯示轉接卡的規格與 LUID 識別資訊。
#[derive(Debug, Clone)]
pub struct DxgiAdapter {
    /// 小寫格式之 LUID 鍵值（例如："luid_0x00000000_0x00010666"），用於對映效能計數器。
    pub luid_key: String,
    /// 顯示卡名稱描述。
    pub description: String,
    /// 是否為軟體模擬顯示轉接卡（如 WARP）。
    pub is_software: bool,
    /// 是否判斷為獨立顯示卡 (dGPU)：NVIDIA，或專用 VRAM >= 1GB。
    pub is_discrete: bool,
}

/// 枚舉系統中所有由 DXGI 識別之顯示轉接卡。即使失敗亦傳回空清單。
pub fn enumerate_adapters() -> Vec<DxgiAdapter> {
    let mut result = Vec::new();

    let Ok(factory) = (unsafe { CreateDXGIFactory1::<IDXGIFactory1>() }) else {
        return result;
    };

    for index in 0.. {
        let Ok(adapter) = (unsafe { factory.EnumAdapters1(index) }) else { break };
        let Ok(desc) = (unsafe { adapter.GetDesc1() }) else { continue };

        let name_len = desc.Description.iter().position(|&ch| ch == 0).unwrap_or(desc.Description.len());
        let description = String::from_utf16_lossy(&desc.Description[..name_len]).trim().to_string();

        let is_software = desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE != 0;
        let dedicated_vram = desc.DedicatedVideoMemory as u64;

        // NVIDIA 視為獨顯；其餘廠商若專用 VRAM >= 1GB 亦判定為獨顯
        let is_discrete = !is_software && (desc.VendorId == VENDOR_NVIDIA || dedicated_vram >= ONE_GIGABYTE);

        result.push(DxgiAdapter {
            luid_key: format!("luid_0x{:08x}_0x{:08x}", desc.AdapterLuid.HighPart, desc.AdapterLuid.LowPart),
            description,
            is_software,
            is_discrete,
        });
    }

    result
}

/// UI 需要的三個顯示卡名稱欄位。轉接卡清單在行程存活期間不會改變，因此只解析一次。
#[derive(Debug, Clone)]
pub struct GpuNames {
    pub has_discrete: bool,
    pub dgpu_name: String,
    pub igpu_name: String,
}

pub fn resolve_gpu_names(adapters: &[DxgiAdapter]) -> GpuNames {
    let hardware: Vec<&DxgiAdapter> = adapters.iter().filter(|a| !a.is_software).collect();
    let discrete = hardware.iter().find(|a| a.is_discrete);
    let integrated = hardware.iter().find(|a| !a.is_discrete);

    match discrete {
        Some(dgpu) => GpuNames {
            has_discrete: true,
            dgpu_name: dgpu.description.clone(),
            igpu_name: integrated.map_or_else(|| "內建顯示晶片 (iGPU)".to_string(), |a| a.description.clone()),
        },
        None => GpuNames {
            has_discrete: false,
            dgpu_name: "無獨立顯示卡".into(),
            igpu_name: integrated
                .or(hardware.first())
                .map_or_else(|| "顯示晶片 (GPU)".to_string(), |a| a.description.clone()),
        },
    }
}

/// Splits a GPU Engine instance name (e.g.
/// "pid_1234_luid_0x00000000_0x00010666_phys_0_eng_0_engtype_3D") into the
/// adapter's lower-cased LUID token and its engine type.
pub fn parse_gpu_engine_instance(instance: &str) -> Option<(String, String)> {
    let lower = instance.to_ascii_lowercase();
    let luid = extract_token(&lower, "luid_", Some("_phys_"))?;
    let engine_type = extract_token(&lower, "engtype_", None)?;
    if luid.is_empty() || engine_type.is_empty() {
        return None;
    }
    Some((format!("luid_{luid}"), engine_type.to_string()))
}

/// Extracts the text between `start` and `end` markers. If `end` is None,
/// reads to the end of the string.
fn extract_token<'a>(source: &'a str, start: &str, end: Option<&str>) -> Option<&'a str> {
    let begin = source.find(start)? + start.len();
    let rest = &source[begin..];
    match end {
        None => Some(rest),
        Some(end) => rest.find(end).map(|stop| &rest[..stop]),
    }
}

/// Reduces per-instance utilisation to one figure per adapter class.
///
/// Task Manager reports a GPU's headline utilisation as the busiest single
/// engine, so instances are summed within an engine type and the maximum
/// engine type is taken per adapter. Unknown LUIDs (not in the DXGI map, e.g.
/// software adapters) count toward the integrated bucket.
pub fn aggregate_gpu_usage<'a>(
    samples: impl IntoIterator<Item = (&'a str, f64)>,
    luid_is_discrete: &HashMap<String, bool>,
) -> (f64, f64) {
    let mut per_adapter: HashMap<String, HashMap<String, f64>> = HashMap::new();

    for (instance, value) in samples {
        // Idle instances are the overwhelming majority every tick, and they
        // cannot move any adapter's figure — so the name is only parsed once
        // something is actually running on it.
        if value <= 0.0 {
            continue;
        }
        let Some((luid, engine_type)) = parse_gpu_engine_instance(instance) else { continue };
        *per_adapter.entry(luid).or_default().entry(engine_type).or_default() += value;
    }

    let mut igpu_max = 0.0f64;
    let mut dgpu_max = 0.0f64;

    for (luid, engines) in &per_adapter {
        let adapter_util = engines.values().copied().fold(0.0, f64::max).min(100.0);
        if luid_is_discrete.get(luid).copied().unwrap_or(false) {
            dgpu_max = dgpu_max.max(adapter_util);
        } else {
            igpu_max = igpu_max.max(adapter_util);
        }
    }

    (round1(igpu_max), round1(dgpu_max))
}

/// One PDH query over `\GPU Engine(*)\Utilization Percentage`.
///
/// The whole category is collected in a single pass per tick; PDH keeps last
/// tick's raw sample and computes the per-instance rate itself, so the first
/// collection reports nothing and every later one is accurate.
pub struct GpuUsageSampler {
    query: Option<(PDH_HQUERY, PDH_HCOUNTER)>,
    /// Maps a GPU Engine LUID token to whether that adapter is the discrete
    /// GPU. Built from DXGI; left None until enumeration succeeds so a failed
    /// first attempt is retried rather than cached as "no discrete GPU".
    luid_is_discrete: Option<HashMap<String, bool>>,
    buffer: Vec<u64>,
}

// PDH handles are plain identifiers with no thread affinity.
unsafe impl Send for GpuUsageSampler {}

impl GpuUsageSampler {
    pub fn new() -> Self {
        let mut sampler = Self { query: None, luid_is_discrete: None, buffer: Vec::new() };
        sampler.open_query();
        sampler
    }

    fn open_query(&mut self) {
        unsafe {
            let mut query = PDH_HQUERY::default();
            if PdhOpenQueryW(PCWSTR::null(), 0, &mut query) != 0 {
                return;
            }

            // The English counter path works regardless of the display
            // language, where the localised name would not.
            let mut counter = PDH_HCOUNTER::default();
            let path = w!("\\GPU Engine(*)\\Utilization Percentage");
            if PdhAddEnglishCounterW(query, path, 0, &mut counter) != 0 {
                PdhCloseQuery(query);
                return;
            }

            // Baseline sample; rates need two collections.
            PdhCollectQueryData(query);
            self.query = Some((query, counter));
        }
    }

    pub fn set_adapters(&mut self, adapters: &[DxgiAdapter]) {
        // DXGI returned nothing — enumeration failed (rare, but possible under
        // session-0 / remote contexts). Do not cache the empty result: that
        // would strand the discrete GPU at 0% for the rest of the session.
        if adapters.is_empty() {
            return;
        }
        self.luid_is_discrete = Some(
            adapters
                .iter()
                .filter(|a| !a.is_software && !a.luid_key.is_empty())
                .map(|a| (a.luid_key.clone(), a.is_discrete))
                .collect(),
        );
    }

    pub fn has_adapter_map(&self) -> bool {
        self.luid_is_discrete.is_some()
    }

    /// Returns (iGPU %, dGPU %). Reports 0 rather than guessing when the
    /// counter category is unavailable.
    pub fn sample(&mut self) -> (f64, f64) {
        let Some((query, counter)) = self.query else {
            return (0.0, 0.0);
        };

        unsafe {
            if PdhCollectQueryData(query) != 0 {
                return (0.0, 0.0);
            }

            let mut size = 0u32;
            let mut count = 0u32;
            let status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, &mut size, &mut count, None);
            if status != PDH_MORE_DATA || size == 0 {
                return (0.0, 0.0);
            }

            // u64 storage keeps the item array aligned. The instance list can
            // grow between the two calls, so leave some headroom.
            size += 4096;
            let words = (size as usize).div_ceil(8);
            if self.buffer.len() < words {
                self.buffer.resize(words, 0);
            }
            let items = self.buffer.as_mut_ptr().cast::<PDH_FMT_COUNTERVALUE_ITEM_W>();

            if PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, &mut size, &mut count, Some(items)) != 0 {
                return (0.0, 0.0);
            }

            let empty = HashMap::new();
            let map = self.luid_is_discrete.as_ref().unwrap_or(&empty);

            let mut names: Vec<(String, f64)> = Vec::new();
            for item in std::slice::from_raw_parts(items, count as usize) {
                if item.FmtValue.CStatus != 0 {
                    continue;
                }
                let value = item.FmtValue.Anonymous.doubleValue;
                if value <= 0.0 || item.szName.is_null() {
                    continue;
                }
                names.push((String::from_utf16_lossy(item.szName.as_wide()), value));
            }

            aggregate_gpu_usage(names.iter().map(|(name, value)| (name.as_str(), *value)), map)
        }
    }
}

impl Drop for GpuUsageSampler {
    fn drop(&mut self) {
        if let Some((query, _)) = self.query.take() {
            unsafe {
                PdhCloseQuery(query);
            }
        }
    }
}

type NvmlInit = unsafe extern "C" fn() -> i32;
type NvmlShutdown = unsafe extern "C" fn() -> i32;
type NvmlDeviceGetCount = unsafe extern "C" fn(*mut u32) -> i32;
type NvmlDeviceGetHandleByIndex = unsafe extern "C" fn(u32, *mut *mut c_void) -> i32;
type NvmlDeviceGetPowerUsage = unsafe extern "C" fn(*mut c_void, *mut u32) -> i32;

/// NVIDIA 獨顯的實測 Package 功耗，直接讀自驅動隨附的 NVML (nvml.dll)。
///
/// 這是整個儀表板上唯一有真實功耗感測器的元件。AMD / Intel 獨顯沒有等效的
/// 免權限介面，因此在那些機器上功耗會如實顯示為「-- W」而不是推估值。
pub struct NvmlPowerSensor {
    device: *mut c_void,
    get_power_usage: NvmlDeviceGetPowerUsage,
    shutdown: NvmlShutdown,
}

// NVML is documented as thread-safe; the device handle is an opaque token.
unsafe impl Send for NvmlPowerSensor {}

impl NvmlPowerSensor {
    /// Loads NVML from System32 only (never from the application directory or
    /// PATH) and opens the first device. None when there is no NVIDIA driver.
    pub fn open() -> Option<Self> {
        unsafe {
            let module = LoadLibraryExW(w!("nvml.dll"), None, LOAD_LIBRARY_SEARCH_SYSTEM32).ok()?;

            let init: NvmlInit = std::mem::transmute(GetProcAddress(module, s!("nvmlInit_v2"))?);
            let shutdown: NvmlShutdown = std::mem::transmute(GetProcAddress(module, s!("nvmlShutdown"))?);
            let get_count: NvmlDeviceGetCount =
                std::mem::transmute(GetProcAddress(module, s!("nvmlDeviceGetCount_v2"))?);
            let get_handle: NvmlDeviceGetHandleByIndex =
                std::mem::transmute(GetProcAddress(module, s!("nvmlDeviceGetHandleByIndex_v2"))?);
            let get_power_usage: NvmlDeviceGetPowerUsage =
                std::mem::transmute(GetProcAddress(module, s!("nvmlDeviceGetPowerUsage"))?);

            if init() != 0 {
                return None;
            }

            let mut count = 0u32;
            let mut device = std::ptr::null_mut();
            if get_count(&mut count) != 0 || count == 0 || get_handle(0, &mut device) != 0 || device.is_null() {
                shutdown();
                return None;
            }

            Some(Self { device, get_power_usage, shutdown })
        }
    }

    /// 獨顯目前功耗（瓦特）。感測器不支援或讀值無意義時為 None。
    pub fn read_watts(&self) -> Option<f64> {
        let mut milliwatts = 0u32;
        let status = unsafe { (self.get_power_usage)(self.device, &mut milliwatts) };
        let watts = milliwatts as f64 / 1000.0;
        (status == 0 && watts > 0.01).then(|| round1(watts))
    }
}

impl Drop for NvmlPowerSensor {
    fn drop(&mut self) {
        unsafe {
            (self.shutdown)();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn adapter(luid: &str, name: &str, software: bool, discrete: bool) -> DxgiAdapter {
        DxgiAdapter { luid_key: luid.into(), description: name.into(), is_software: software, is_discrete: discrete }
    }

    #[test]
    fn parses_gpu_engine_instance_names() {
        let parsed = parse_gpu_engine_instance("pid_1234_luid_0x00000000_0x00010666_phys_0_eng_0_engtype_3D");
        assert_eq!(parsed, Some(("luid_0x00000000_0x00010666".into(), "3d".into())));

        let upper = parse_gpu_engine_instance("pid_9_luid_0x00000000_0x0001ABCD_phys_0_eng_3_engtype_VideoDecode");
        assert_eq!(upper, Some(("luid_0x00000000_0x0001abcd".into(), "videodecode".into())));

        assert_eq!(parse_gpu_engine_instance("_Total"), None);
        assert_eq!(parse_gpu_engine_instance("pid_1_luid_0x0_0x1_eng_0_engtype_3D"), None);
    }

    #[test]
    fn aggregates_busiest_engine_per_adapter_class() {
        let map = HashMap::from([
            ("luid_0x00000000_0x0000aaaa".to_string(), false),
            ("luid_0x00000000_0x0000bbbb".to_string(), true),
        ]);

        let samples = [
            // iGPU: two processes on 3D (summed), one on video decode.
            ("pid_1_luid_0x00000000_0x0000AAAA_phys_0_eng_0_engtype_3D", 10.0),
            ("pid_2_luid_0x00000000_0x0000AAAA_phys_0_eng_0_engtype_3D", 5.5),
            ("pid_2_luid_0x00000000_0x0000AAAA_phys_0_eng_1_engtype_VideoDecode", 12.0),
            // dGPU: saturated, must cap at 100.
            ("pid_3_luid_0x00000000_0x0000BBBB_phys_0_eng_0_engtype_3D", 80.0),
            ("pid_4_luid_0x00000000_0x0000BBBB_phys_0_eng_0_engtype_3D", 45.0),
            // Idle and unparseable instances contribute nothing.
            ("pid_5_luid_0x00000000_0x0000BBBB_phys_0_eng_2_engtype_Copy", 0.0),
            ("garbage", 50.0),
        ];

        assert_eq!(aggregate_gpu_usage(samples, &map), (15.5, 100.0));
    }

    #[test]
    fn unknown_luids_count_toward_the_integrated_bucket() {
        let samples = [("pid_1_luid_0x00000000_0x0000CCCC_phys_0_eng_0_engtype_3D", 33.33)];
        assert_eq!(aggregate_gpu_usage(samples, &HashMap::new()), (33.3, 0.0));
    }

    #[test]
    fn resolves_gpu_names() {
        let both = [
            adapter("a", "AMD Radeon(TM) Graphics", false, false),
            adapter("b", "NVIDIA GeForce RTX 3060 Laptop GPU", false, true),
            adapter("c", "Microsoft Basic Render Driver", true, false),
        ];
        let names = resolve_gpu_names(&both);
        assert!(names.has_discrete);
        assert_eq!(names.dgpu_name, "NVIDIA GeForce RTX 3060 Laptop GPU");
        assert_eq!(names.igpu_name, "AMD Radeon(TM) Graphics");

        let integrated_only = [adapter("a", "Intel(R) Iris(R) Xe Graphics", false, false)];
        let names = resolve_gpu_names(&integrated_only);
        assert!(!names.has_discrete);
        assert_eq!(names.dgpu_name, "無獨立顯示卡");
        assert_eq!(names.igpu_name, "Intel(R) Iris(R) Xe Graphics");

        let none = resolve_gpu_names(&[]);
        assert!(!none.has_discrete);
        assert_eq!(none.igpu_name, "顯示晶片 (GPU)");
    }
}
