//! 1Hz 全系統即時電源與硬體功耗遙測整合。
//!
//! 整合 CPU 使用率、iGPU/dGPU 負載與功耗 (DXGI / PDH / NVML)、電池端原生充放電
//! 功率 (IOCTL) 與螢幕亮度等數據。所有瓦數都是實測值：讀不到就回報「讀不到」，
//! 絕不以使用率推估一個看似合理的數字。

use std::collections::HashMap;
use std::time::{Duration, Instant};

use windows::core::{GUID, PWSTR};
use windows::System::Power::{PowerManager, PowerSupplyStatus};
use windows::Win32::Foundation::{LocalFree, FILETIME, HLOCAL};
use windows::Win32::System::Power::{
    GetSystemPowerStatus, PowerGetActiveScheme, PowerReadFriendlyName, SYSTEM_POWER_STATUS,
};
use windows::Win32::System::Threading::GetSystemTimes;
use wmi::{Variant, WMIConnection};

use crate::battery::{BatteryTelemetry, PackInfo, Reading};
use crate::gpu::{self, GpuNames, GpuUsageSampler, NvmlPowerSensor};
use crate::models::{round1, round2, PowerSupplyCapability, RealTimePowerState};

/// WMI queries are comparatively heavy, and brightness, voltage and the power
/// plan change slowly, so those values are cached and refreshed every few
/// seconds rather than on every tick.
const SLOW_REFRESH: Duration = Duration::from_millis(4000);

/// Nominal pack voltage used only to keep the I = P / V maths sane when the
/// embedded controller exposes no live voltage. Never shown as a reading.
const NOMINAL_VOLTAGE_V: f64 = 15.4;

/// What `GetSystemPowerStatus` says about the machine.
#[derive(Debug, Clone, Copy)]
pub struct SystemPower {
    pub ac_online: bool,
    /// False when Windows reports no system battery (a desktop).
    pub has_battery: bool,
    pub battery_percent: i32,
    /// Windows' own runtime forecast in seconds; non-positive when unknown.
    pub battery_life_time: i32,
}

/// Every raw reading one tick is built from. Kept separate from the sampling
/// so the state logic can be exercised without hardware.
#[derive(Debug, Clone)]
pub struct Inputs {
    pub gpu_names: GpuNames,
    pub cpu_usage_percent: f64,
    pub igpu_usage_percent: f64,
    pub dgpu_usage_percent: f64,
    pub dgpu_power_w: Option<f64>,
    pub brightness_percent: Option<i32>,
    pub battery: Option<Reading>,
    pub pack: PackInfo,
    pub supply: PowerSupplyCapability,
    pub system: Option<SystemPower>,
    /// Measured pack voltage, from the battery IOCTL or the WMI fallback.
    pub measured_voltage_v: Option<f64>,
    pub power_plan_name: String,
}

/// Whole hours and leftover minutes of a duration given in hours.
fn hours_minutes(hours: f64) -> (i64, i64) {
    let total_seconds = (hours * 3600.0) as i64;
    (total_seconds / 3600, (total_seconds % 3600) / 60)
}

/// Builds the snapshot the UI renders from one tick's raw readings.
pub fn compose_state(input: &Inputs) -> RealTimePowerState {
    let mut state = RealTimePowerState {
        has_discrete_gpu: input.gpu_names.has_discrete,
        dgpu_name: input.gpu_names.dgpu_name.clone(),
        igpu_name: input.gpu_names.igpu_name.clone(),
        // CPU package power is deliberately not reported. It is unreadable
        // without a kernel driver, and no estimate stands in for it.
        cpu_usage_percent: input.cpu_usage_percent,
        igpu_usage_percent: input.igpu_usage_percent,
        supply_capability: input.supply,
        ..Default::default()
    };

    // GPU usage. Only dGPU package power is real.
    if state.has_discrete_gpu {
        let usage = input.dgpu_usage_percent;
        state.dgpu_usage_percent = usage;

        // A real GPU draws real watts even at 0% utilisation (~19 W idle
        // measured on an RTX 3060 Laptop), so the sensor value is used
        // whatever the load reads.
        state.is_dgpu_power_measured = input.dgpu_power_w.is_some();
        state.dgpu_power_w = input.dgpu_power_w.unwrap_or(0.0);

        state.dgpu_status_text =
            if usage > 0.0 { format!("{usage:.1}% (高效能運算中)") } else { format!("{usage:.1}% (待機)") };
    } else {
        state.dgpu_usage_percent = 0.0;
        state.dgpu_power_w = 0.0;
        state.is_dgpu_power_measured = false;
        state.dgpu_status_text = "無獨立顯示卡".into();
    }

    // Desktop monitors and many external panels do not implement
    // WmiMonitorBrightness; the flag keeps the fallback from being presented
    // as a real reading.
    state.is_brightness_measured = input.brightness_percent.is_some();
    state.screen_brightness_percent = input.brightness_percent.unwrap_or(75);

    // There is no system-total wattage. Screen, disk, Wi-Fi, RAM and chipset
    // power would all be guesses over utilisation, and summing them would
    // produce an equally invented total. On battery the pack reports the real
    // whole-system figure, which is strictly better than any sum of estimates.

    let batt = input.battery;
    let pack = &input.pack;

    // Energy in the pack and the health that follows from it, both read from
    // the battery driver. A relative-capacity pack reports capacities in its
    // own arbitrary units, so it is excluded here rather than having those
    // numbers presented as watt-hours.
    let energy_usable = batt.is_some_and(|b| b.is_capacity_known)
        && pack.is_valid
        && !pack.is_capacity_relative
        && pack.full_charged_capacity_mwh > 0;

    if let (true, Some(b)) = (energy_usable, batt) {
        let remaining_wh = b.remaining_capacity_mwh as f64 / 1000.0;
        let full_wh = pack.full_charged_capacity_mwh as f64 / 1000.0;

        state.is_energy_measured = true;
        state.remaining_capacity_mwh = b.remaining_capacity_mwh;
        state.full_charged_capacity_mwh = pack.full_charged_capacity_mwh;
        state.designed_capacity_mwh = pack.designed_capacity_mwh;
        state.true_soc_percent = round1((remaining_wh / full_wh * 100.0).min(100.0));
        state.driver_health_percent = pack.health_percent().unwrap_or(0.0);

        state.battery_energy_text = format!("{remaining_wh:.1} / {full_wh:.1} Wh");
        state.battery_capacity_health_text = if pack.designed_capacity_mwh > 0 {
            format!("{full_wh:.1} / {:.1} Wh", pack.designed_capacity_mwh as f64 / 1000.0)
        } else {
            format!("{full_wh:.1} Wh")
        };
    }

    // Real pack temperature, when the firmware implements the level.
    if let Some(b) = batt.filter(|b| b.is_temperature_known) {
        state.is_battery_temperature_measured = true;
        state.battery_temperature_c = b.temperature_c;
    }

    let measured_discharge = batt.filter(|b| b.is_rate_known && b.discharge_w > 0.0).map(|b| b.discharge_w);

    if let Some(system) = input.system {
        state.is_ac_online = system.ac_online;
        state.battery_percent = system.battery_percent;
        state.has_battery = system.has_battery || batt.is_some();

        // The driver's own flag decides whether current is flowing into the
        // pack. Inferring it from a percentage threshold is wrong on any
        // laptop with a charge limit — one that stops at ~95% by design would
        // be reported as still charging.
        state.is_charging = match batt {
            Some(b) => b.is_charging,
            None => state.has_battery && state.is_ac_online && state.battery_percent < 98,
        };

        if !state.has_battery {
            // A desktop, or a laptop with its pack removed. There is no
            // battery figure of any kind to show, and saying "charging" about
            // a battery that does not exist would be an invention.
            state.power_status_text = "市電供電中 (無電池)".into();
            state.charging_status_text = "市電直供 | 未偵測到電池".into();
            state.estimated_time_remaining_text = "未偵測到電池".into();
            state.discharge_rate_text = "-- W".into();
        } else if state.is_ac_online {
            if state.is_charging {
                // No invented constant here: an unknown charge rate is
                // reported as unknown, not as a plausible number.
                if let Some(b) = batt.filter(|b| b.is_rate_known && b.charge_w > 0.0) {
                    state.charging_rate_w = b.charge_w;
                    state.is_charge_rate_measured = true;
                }

                state.power_status_text = "AC 充電中".into();

                // Only the charge rate is real. AC adapter input cannot be
                // measured at all on Windows, so no total is shown.
                if state.is_charge_rate_measured {
                    let w = state.charging_rate_w;
                    state.discharge_rate_text = format!("+{w:.1} W (電池充電實測)");
                    state.charging_status_text = format!("電池充電中 +{w:.1}W (電池實測)");
                } else {
                    state.discharge_rate_text = "-- W".into();
                    state.charging_status_text = "電池充電中 (充電功率無法讀取)".into();
                }

                // Time to full, from the energy still missing and the measured
                // charge rate. The pack's own capacity is used; no pack size
                // is assumed.
                state.estimated_time_remaining_text = "充電中...".into();
                if state.is_charge_rate_measured
                    && state.is_energy_measured
                    && state.full_charged_capacity_mwh > state.remaining_capacity_mwh
                {
                    let missing_wh = (state.full_charged_capacity_mwh - state.remaining_capacity_mwh) as f64 / 1000.0;
                    let hours = missing_wh / state.charging_rate_w;
                    if hours < 24.0 {
                        let (h, m) = hours_minutes(hours);
                        state.estimated_time_remaining_text = format!("預估 {h}小時{m}分 充飽");
                    }
                }
            } else if let Some(discharge_w) = measured_discharge {
                // External power is connected and the pack is *still*
                // draining. The charger is not covering the load, and the
                // battery is silently making up the difference.
                //
                // This is the everyday failure mode of charging a laptop over
                // USB-C: a 65 W PD brick cannot hold up a machine that wants
                // more than 65 W, so the pack drains even though the cable is
                // plugged in.
                //
                // The shortfall is measured at the pack, so unlike any adapter
                // figure it is a reading rather than a guess.
                state.is_charger_deficit = true;
                state.discharge_rate_w = discharge_w;
                state.is_discharge_rate_measured = true;

                state.power_status_text = "外接電源供電不足".into();
                state.charging_status_text = format!("外接電源供電不足 | 電池補上 -{discharge_w:.1}W (電池實測)");
                state.discharge_rate_text = format!("-{discharge_w:.1} W (外接電源不足，由電池補足)");

                // How long the pack can keep covering the shortfall. Windows'
                // own forecast is not offered while on AC, so this comes from
                // the pack's remaining energy over the measured deficit — both
                // real readings.
                state.estimated_time_remaining_text = "外接電源不足，電池仍在耗電".into();
                if energy_usable {
                    let hours = state.remaining_capacity_mwh as f64 / 1000.0 / discharge_w;
                    if hours < 24.0 {
                        let (h, m) = hours_minutes(hours);
                        state.estimated_time_remaining_text =
                            format!("仍在耗電，約 {h} 小時 {m} 分鐘 (以 -{discharge_w:.1}W 計算)");
                    }
                }
            } else {
                // On AC with no current flowing there is nothing real to
                // measure: the pack reports 0 and adapter input is not exposed
                // by Windows. Report that honestly.
                state.power_status_text = "市電供電中 (未充電)".into();
                state.charging_status_text = "市電直供 | 電池未充放電 (無可量測功率)".into();

                // The pack's own state of charge, where the Windows percentage
                // is a rounded integer. A charge limit that stops at 95% reads
                // as 95.0%, not as "full".
                state.estimated_time_remaining_text = if state.is_energy_measured {
                    let soc = state.true_soc_percent;
                    if soc >= 99.0 {
                        format!("電量已充滿 ({soc:.1}%)")
                    } else {
                        format!("電量 {soc:.1}% (電池保養未充電)")
                    }
                } else if state.battery_percent >= 98 {
                    "電量已充滿 100%".into()
                } else {
                    format!("電量 {}% (電池保養未充電)", state.battery_percent)
                };

                state.discharge_rate_text = "-- W".into();
            }
        } else {
            state.is_charging = false;
            state.power_status_text = "電池放電中".into();
            state.charging_status_text = "使用電池供電".into();

            // Three sources, best first. Windows' own forecast is preferred
            // because it smooths the rate over time; the two fallbacks exist
            // because it reports nothing at all for the first minute or so
            // after unplugging.
            state.estimated_time_remaining_text = "估算中...".into();
            if system.battery_life_time > 0 && system.battery_life_time < 86_400 {
                let (h, m) = hours_minutes(system.battery_life_time as f64 / 3600.0);
                state.estimated_time_remaining_text = format!("{h} 小時 {m} 分鐘");
            } else if let Some(b) = batt.filter(|b| b.is_estimated_runtime_known) {
                let (h, m) = hours_minutes(b.estimated_runtime_seconds as f64 / 3600.0);
                state.estimated_time_remaining_text = format!("{h} 小時 {m} 分鐘 (電池驅動估算)");
            } else if let (true, Some(discharge_w)) = (energy_usable, measured_discharge) {
                // Remaining watt-hours over the present draw. Both terms are
                // measured at the pack, so this is arithmetic on real readings
                // rather than a utilisation curve.
                let hours = state.remaining_capacity_mwh as f64 / 1000.0 / discharge_w;
                if hours < 24.0 {
                    let (h, m) = hours_minutes(hours);
                    state.estimated_time_remaining_text = format!("{h} 小時 {m} 分鐘 (以目前 {discharge_w:.1}W 計算)");
                }
            }
        }
    }

    // Discharge rate. On battery this is the single most valuable number the
    // app has: it is the whole machine's real power draw, measured at the
    // pack, with no per-component estimation involved.
    if !state.is_ac_online && state.has_battery {
        match measured_discharge {
            Some(discharge_w) => {
                state.discharge_rate_w = discharge_w;
                state.is_discharge_rate_measured = true;
                state.discharge_rate_text = format!("-{discharge_w:.1} W (電池實測)");
            }
            None => {
                // No substitute figure: if the pack will not report a rate
                // there is no honest number to show.
                state.discharge_rate_w = 0.0;
                state.is_discharge_rate_measured = false;
                state.discharge_rate_text = "-- W".into();
            }
        }
    }

    // Battery physical telemetry (voltage, current).
    state.is_voltage_measured = input.measured_voltage_v.is_some();
    state.battery_voltage_v = input.measured_voltage_v.unwrap_or(NOMINAL_VOLTAGE_V);

    // Current follows whichever way power is actually flowing. On AC that is
    // normally the charge rate, but not when the charger is being out-run and
    // the pack is discharging into the machine.
    let active_power_w =
        if !state.is_ac_online || state.is_charger_deficit { state.discharge_rate_w } else { state.charging_rate_w };
    state.battery_current_a = if state.battery_voltage_v > 0.0 && active_power_w > 0.0 {
        round2(active_power_w / state.battery_voltage_v)
    } else {
        0.0
    };

    let volt_text = if state.is_voltage_measured { format!("{:.2} V", state.battery_voltage_v) } else { "-- V".into() };
    let current_text =
        if state.battery_current_a > 0.0 { format!("{:.2} A", state.battery_current_a) } else { "-- A".into() };
    state.battery_telemetry_text = format!("{volt_text} | {current_text}");

    state.power_plan_name = input.power_plan_name.clone();
    state
}

/// CPU utilisation from successive `GetSystemTimes` samples.
struct CpuSampler {
    previous: Option<(u64, u64)>,
}

impl CpuSampler {
    fn new() -> Self {
        let mut sampler = Self { previous: None };
        sampler.sample();
        sampler
    }

    /// Returns (idle, total) in 100 ns units. Kernel time includes idle time.
    fn read() -> Option<(u64, u64)> {
        let to_u64 = |ft: FILETIME| ((ft.dwHighDateTime as u64) << 32) | ft.dwLowDateTime as u64;
        let (mut idle, mut kernel, mut user) = (FILETIME::default(), FILETIME::default(), FILETIME::default());
        unsafe { GetSystemTimes(Some(&mut idle), Some(&mut kernel), Some(&mut user)) }.ok()?;
        Some((to_u64(idle), to_u64(kernel) + to_u64(user)))
    }

    /// When the counters are unavailable this reports 0 rather than a
    /// fabricated value.
    fn sample(&mut self) -> f64 {
        let Some(current) = Self::read() else { return 0.0 };
        let usage = match self.previous {
            Some((idle, total)) if current.1 > total => {
                let busy = 1.0 - (current.0 - idle) as f64 / (current.1 - total) as f64;
                round1((busy * 100.0).clamp(0.0, 100.0))
            }
            _ => 0.0,
        };
        self.previous = Some(current);
        usage
    }
}

/// A value refreshed at most once per [`SLOW_REFRESH`].
struct Cached<T> {
    value: T,
    refreshed: Option<Instant>,
}

impl<T: Clone> Cached<T> {
    fn new(initial: T) -> Self {
        Self { value: initial, refreshed: None }
    }

    fn get(&mut self, refresh: impl FnOnce() -> Option<T>) -> T {
        if self.refreshed.is_none_or(|at| at.elapsed() >= SLOW_REFRESH) {
            self.refreshed = Some(Instant::now());
            if let Some(value) = refresh() {
                self.value = value;
            }
        }
        self.value.clone()
    }
}

/// Owns every sensor handle and produces one [`RealTimePowerState`] per call.
/// Lives on the monitoring thread: the WMI connection it holds is bound to the
/// thread that created it.
pub struct Sampler {
    battery: BatteryTelemetry,
    cpu: CpuSampler,
    gpu_usage: GpuUsageSampler,
    gpu_names: GpuNames,
    nvml: Option<NvmlPowerSensor>,
    wmi: Option<WMIConnection>,
    brightness: Cached<Option<i32>>,
    voltage: Cached<Option<f64>>,
    power_plan: Cached<String>,
}

impl Sampler {
    /// Opens the sensor stack. Loading NVML and connecting to WMI can take a
    /// noticeable moment, which is why this runs on the monitoring thread and
    /// never on the UI thread.
    pub fn new() -> Self {
        let adapters = gpu::enumerate_adapters();
        let gpu_names = gpu::resolve_gpu_names(&adapters);
        let mut gpu_usage = GpuUsageSampler::new();
        gpu_usage.set_adapters(&adapters);

        Self {
            battery: BatteryTelemetry::open(),
            cpu: CpuSampler::new(),
            gpu_usage,
            // NVML is only worth loading when there is a discrete card to ask.
            nvml: gpu_names.has_discrete.then(NvmlPowerSensor::open).flatten(),
            gpu_names,
            wmi: WMIConnection::with_namespace_path("root\\WMI").ok(),
            brightness: Cached::new(None),
            voltage: Cached::new(None),
            power_plan: Cached::new("平衡 (Balanced)".to_string()),
        }
    }

    /// The pack's identity and capacities, for merging into a parsed report.
    pub fn pack_info(&mut self) -> PackInfo {
        self.battery.pack_info().clone()
    }

    /// 採樣並傳回當前全系統即時功耗與硬體狀態。
    pub fn sample(&mut self) -> RealTimePowerState {
        // A failed DXGI enumeration is retried rather than cached, so the
        // discrete GPU is not stranded at 0% for the rest of the session.
        if !self.gpu_usage.has_adapter_map() {
            let adapters = gpu::enumerate_adapters();
            if !adapters.is_empty() {
                self.gpu_names = gpu::resolve_gpu_names(&adapters);
                self.gpu_usage.set_adapters(&adapters);
            }
        }

        let (igpu_usage, dgpu_usage) = self.gpu_usage.sample();
        let battery = self.battery.read();
        let pack = self.battery.pack_info().clone();

        let wmi = self.wmi.as_ref();
        let brightness = self.brightness.get(|| Some(wmi.and_then(query_brightness)));

        // The battery IOCTL returns voltage in the same call the rate comes
        // from, so prefer it; WMI is the fallback.
        let pack_voltage = battery.map(|b| b.voltage_v).filter(|v| *v > 0.0);
        let measured_voltage_v = self.voltage.get(|| Some(pack_voltage.or_else(|| wmi.and_then(query_voltage))));

        compose_state(&Inputs {
            gpu_names: self.gpu_names.clone(),
            cpu_usage_percent: self.cpu.sample(),
            igpu_usage_percent: igpu_usage,
            dgpu_usage_percent: dgpu_usage,
            dgpu_power_w: self.nvml.as_ref().and_then(NvmlPowerSensor::read_watts),
            brightness_percent: brightness,
            battery,
            pack,
            supply: power_supply_status(),
            system: system_power(),
            measured_voltage_v,
            power_plan_name: self.power_plan.get(active_power_plan_name),
        })
    }
}

fn variant_to_f64(value: &Variant) -> Option<f64> {
    Some(match value {
        Variant::I1(v) => *v as f64,
        Variant::I2(v) => *v as f64,
        Variant::I4(v) => *v as f64,
        Variant::I8(v) => *v as f64,
        Variant::UI1(v) => *v as f64,
        Variant::UI2(v) => *v as f64,
        Variant::UI4(v) => *v as f64,
        Variant::UI8(v) => *v as f64,
        Variant::R4(v) => *v as f64,
        Variant::R8(v) => *v,
        Variant::String(v) => v.trim().parse().ok()?,
        _ => return None,
    })
}

/// The first numeric value of `property` across the rows a query returns.
fn query_number(wmi: &WMIConnection, query: &str, property: &str) -> Option<f64> {
    let rows: Vec<HashMap<String, Variant>> = wmi.raw_query(query).ok()?;
    rows.iter().find_map(|row| row.get(property).and_then(variant_to_f64))
}

fn query_brightness(wmi: &WMIConnection) -> Option<i32> {
    query_number(wmi, "SELECT CurrentBrightness FROM WmiMonitorBrightness", "CurrentBrightness")
        .map(|value| value as i32)
}

/// Pack voltage in volts from the WMI BatteryStatus class (millivolts).
fn query_voltage(wmi: &WMIConnection) -> Option<f64> {
    query_number(wmi, "SELECT Voltage FROM BatteryStatus", "Voltage")
        .filter(|millivolts| *millivolts > 0.0)
        .map(|millivolts| round2(millivolts / 1000.0))
}

fn system_power() -> Option<SystemPower> {
    let mut status = SYSTEM_POWER_STATUS::default();
    unsafe { GetSystemPowerStatus(&mut status) }.ok()?;
    Some(SystemPower {
        ac_online: status.ACLineStatus == 1,
        // BatteryFlag: 128 = no system battery, 255 = unknown status.
        has_battery: status.BatteryFlag != 255 && status.BatteryFlag & 128 == 0,
        battery_percent: if status.BatteryLifePercent <= 100 { status.BatteryLifePercent as i32 } else { 0 },
        battery_life_time: status.BatteryLifeTime as i32,
    })
}

/// Windows' verdict on the charger itself. This is the only thing the platform
/// will say about the external supply rather than about the pack — the USB-C
/// PD contract, and with it a real adapter wattage, is not reachable
/// unelevated.
fn power_supply_status() -> PowerSupplyCapability {
    match PowerManager::PowerSupplyStatus() {
        Ok(PowerSupplyStatus::NotPresent) => PowerSupplyCapability::NotPresent,
        Ok(PowerSupplyStatus::Inadequate) => PowerSupplyCapability::Inadequate,
        Ok(PowerSupplyStatus::Adequate) => PowerSupplyCapability::Adequate,
        _ => PowerSupplyCapability::Unknown,
    }
}

/// The friendly name of the active Windows power scheme (PowrProf.dll).
fn active_power_plan_name() -> Option<String> {
    unsafe {
        let mut scheme: *mut GUID = std::ptr::null_mut();
        if PowerGetActiveScheme(None, &mut scheme).is_err() || scheme.is_null() {
            return None;
        }

        let mut buffer = [0u16; 256];
        let mut size = std::mem::size_of_val(&buffer) as u32;
        let read = PowerReadFriendlyName(None, Some(scheme), None, None, Some(buffer.as_mut_ptr().cast()), &mut size);
        let _ = LocalFree(Some(HLOCAL(scheme.cast())));

        if read.is_err() {
            return None;
        }

        let name = PWSTR(buffer.as_mut_ptr()).to_string().ok()?;
        let name = name.trim();
        (!name.is_empty()).then(|| name.to_string())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn inputs() -> Inputs {
        Inputs {
            gpu_names: GpuNames { has_discrete: true, dgpu_name: "RTX".into(), igpu_name: "Radeon".into() },
            cpu_usage_percent: 12.5,
            igpu_usage_percent: 3.0,
            dgpu_usage_percent: 0.0,
            dgpu_power_w: Some(19.2),
            brightness_percent: Some(60),
            battery: None,
            pack: PackInfo {
                is_valid: true,
                designed_capacity_mwh: 76_000,
                full_charged_capacity_mwh: 56_000,
                ..Default::default()
            },
            supply: PowerSupplyCapability::Adequate,
            system: Some(SystemPower {
                ac_online: true,
                has_battery: true,
                battery_percent: 50,
                battery_life_time: -1,
            }),
            measured_voltage_v: Some(16.0),
            power_plan_name: "Balanced".into(),
        }
    }

    fn reading() -> Reading {
        Reading {
            is_rate_known: true,
            voltage_v: 16.0,
            remaining_capacity_mwh: 28_000,
            is_capacity_known: true,
            ..Default::default()
        }
    }

    #[test]
    fn charging_reports_measured_rate_and_time_to_full() {
        let mut input = inputs();
        input.battery = Some(Reading { is_charging: true, charge_w: 56.0, ..reading() });

        let state = compose_state(&input);

        assert!(state.is_ac_online && state.is_charging && state.is_charge_rate_measured);
        assert!(!state.is_charger_deficit);
        assert_eq!(state.charging_rate_w, 56.0);
        assert_eq!(state.discharge_rate_text, "+56.0 W (電池充電實測)");
        // 28 Wh missing at 56 W is exactly half an hour.
        assert_eq!(state.estimated_time_remaining_text, "預估 0小時30分 充飽");
        assert_eq!(state.battery_current_a, 3.5);
        assert_eq!(state.battery_telemetry_text, "16.00 V | 3.50 A");
        assert_eq!(state.true_soc_percent, 50.0);
        assert_eq!(state.battery_energy_text, "28.0 / 56.0 Wh");
        assert_eq!(state.battery_capacity_health_text, "56.0 / 76.0 Wh");
        assert_eq!(state.driver_health_percent, 73.7);
    }

    #[test]
    fn charging_without_a_rate_never_invents_one() {
        let mut input = inputs();
        input.battery = Some(Reading { is_charging: true, is_rate_known: false, ..reading() });

        let state = compose_state(&input);

        assert!(state.is_charging && !state.is_charge_rate_measured);
        assert_eq!(state.charging_rate_w, 0.0);
        assert_eq!(state.discharge_rate_text, "-- W");
        assert_eq!(state.estimated_time_remaining_text, "充電中...");
    }

    #[test]
    fn plugged_in_but_draining_is_a_charger_deficit() {
        let mut input = inputs();
        input.battery = Some(Reading { discharge_w: 14.0, ..reading() });

        let state = compose_state(&input);

        assert!(state.is_ac_online && state.is_charger_deficit && state.is_discharge_rate_measured);
        assert_eq!(state.discharge_rate_w, 14.0);
        assert_eq!(state.power_status_text, "外接電源供電不足");
        assert_eq!(state.estimated_time_remaining_text, "仍在耗電，約 2 小時 0 分鐘 (以 -14.0W 計算)");
        // Current follows the discharge, not the (zero) charge rate.
        assert_eq!(state.battery_current_a, 0.88);
    }

    #[test]
    fn idle_on_ac_reports_true_state_of_charge() {
        let mut input = inputs();
        input.battery = Some(Reading { remaining_capacity_mwh: 53_200, ..reading() });

        let state = compose_state(&input);

        assert!(!state.is_charging && !state.is_charger_deficit);
        assert_eq!(state.discharge_rate_text, "-- W");
        assert_eq!(state.estimated_time_remaining_text, "電量 95.0% (電池保養未充電)");
        assert_eq!(state.battery_telemetry_text, "16.00 V | -- A");
    }

    #[test]
    fn on_battery_prefers_the_windows_forecast_then_falls_back() {
        let mut input = inputs();
        input.battery = Some(Reading { discharge_w: 14.0, ..reading() });
        input.system =
            Some(SystemPower { ac_online: false, has_battery: true, battery_percent: 50, battery_life_time: 5400 });

        let state = compose_state(&input);
        assert!(!state.is_ac_online && !state.is_charging && state.is_discharge_rate_measured);
        assert_eq!(state.discharge_rate_text, "-14.0 W (電池實測)");
        assert_eq!(state.estimated_time_remaining_text, "1 小時 30 分鐘");

        input.system =
            Some(SystemPower { ac_online: false, has_battery: true, battery_percent: 50, battery_life_time: -1 });
        let state = compose_state(&input);
        assert_eq!(state.estimated_time_remaining_text, "2 小時 0 分鐘 (以目前 14.0W 計算)");

        input.battery = Some(Reading {
            discharge_w: 14.0,
            estimated_runtime_seconds: 3660,
            is_estimated_runtime_known: true,
            ..reading()
        });
        let state = compose_state(&input);
        assert_eq!(state.estimated_time_remaining_text, "1 小時 1 分鐘 (電池驅動估算)");
    }

    #[test]
    fn missing_sensors_are_reported_as_missing() {
        let mut input = inputs();
        input.battery = None;
        input.pack = PackInfo::default();
        input.measured_voltage_v = None;
        input.brightness_percent = None;
        input.dgpu_power_w = None;
        input.system =
            Some(SystemPower { ac_online: false, has_battery: true, battery_percent: 0, battery_life_time: -1 });

        let state = compose_state(&input);

        assert!(!state.is_discharge_rate_measured && !state.is_voltage_measured && !state.is_energy_measured);
        assert!(!state.is_brightness_measured && !state.is_dgpu_power_measured);
        assert_eq!(state.discharge_rate_text, "-- W");
        assert_eq!(state.battery_telemetry_text, "-- V | -- A");
        assert_eq!(state.estimated_time_remaining_text, "估算中...");
    }

    #[test]
    fn desktops_are_not_reported_as_charging() {
        let mut input = inputs();
        input.battery = None;
        input.pack = PackInfo::default();
        input.measured_voltage_v = None;
        input.system =
            Some(SystemPower { ac_online: true, has_battery: false, battery_percent: 0, battery_life_time: -1 });

        let state = compose_state(&input);

        assert!(!state.has_battery && !state.is_charging && !state.is_charger_deficit);
        assert_eq!(state.power_status_text, "市電供電中 (無電池)");
        assert_eq!(state.estimated_time_remaining_text, "未偵測到電池");
        assert_eq!(state.discharge_rate_text, "-- W");
        assert_eq!(state.battery_telemetry_text, "-- V | -- A");
    }

    #[test]
    fn relative_capacity_packs_are_never_shown_as_watt_hours() {
        let mut input = inputs();
        input.pack.is_capacity_relative = true;
        input.battery = Some(reading());

        assert!(!compose_state(&input).is_energy_measured);
    }

    #[test]
    fn machines_without_a_discrete_gpu_report_none() {
        let mut input = inputs();
        input.gpu_names =
            GpuNames { has_discrete: false, dgpu_name: "無獨立顯示卡".into(), igpu_name: "Iris".into() };
        input.dgpu_usage_percent = 40.0;

        let state = compose_state(&input);

        assert!(!state.has_discrete_gpu && !state.is_dgpu_power_measured);
        assert_eq!(state.dgpu_usage_percent, 0.0);
        assert_eq!(state.dgpu_status_text, "無獨立顯示卡");
    }
}

/// Debug builds only: synthetic states for exercising the dashboard on a
/// machine without the hardware (a desktop has no battery to charge, drain or
/// out-run). Selected with `WINBAT_LENS_DEMO=charging|battery|deficit|idle`;
/// release builds never contain this and only ever show real readings.
#[cfg(any(test, debug_assertions))]
pub mod demo {
    use super::*;

    pub fn pack() -> PackInfo {
        PackInfo {
            is_valid: true,
            chemistry: "LION".into(),
            designed_capacity_mwh: 76_000,
            full_charged_capacity_mwh: 56_032,
            cycle_count: Some(212),
            device_name: "DEMO Battery".into(),
            manufacture_name: "DEMO".into(),
            manufacture_date: chrono::NaiveDate::from_ymd_opt(2023, 1, 2),
            ..Default::default()
        }
    }

    /// Replaces the battery-side readings of a real sample with a scenario.
    pub fn state(scenario: &str, tick: u64, real: &RealTimePowerState) -> RealTimePowerState {
        let wobble = ((tick as f64) * 0.7).sin();
        let percent = (62 + (tick / 4) as i32 % 30).min(100);
        let base = Reading {
            is_rate_known: true,
            voltage_v: 15.6 + wobble * 0.2,
            remaining_capacity_mwh: 56_032 * percent / 100,
            is_capacity_known: true,
            temperature_c: 34.5,
            is_temperature_known: true,
            ..Default::default()
        };
        let (battery, ac_online, supply) = match scenario {
            "charging" => (
                Reading { is_charging: true, charge_w: 48.0 + wobble * 6.0, ..base },
                true,
                PowerSupplyCapability::Adequate,
            ),
            "deficit" => {
                (Reading { discharge_w: 14.0 + wobble * 4.0, ..base }, true, PowerSupplyCapability::Inadequate)
            }
            "idle" => (base, true, PowerSupplyCapability::Adequate),
            _ => (Reading { discharge_w: 22.0 + wobble * 9.0, ..base }, false, PowerSupplyCapability::NotPresent),
        };

        compose_state(&Inputs {
            gpu_names: GpuNames {
                has_discrete: real.has_discrete_gpu,
                dgpu_name: real.dgpu_name.clone(),
                igpu_name: real.igpu_name.clone(),
            },
            cpu_usage_percent: real.cpu_usage_percent,
            igpu_usage_percent: real.igpu_usage_percent,
            dgpu_usage_percent: real.dgpu_usage_percent,
            dgpu_power_w: real.is_dgpu_power_measured.then_some(real.dgpu_power_w),
            brightness_percent: Some(60),
            battery: Some(battery),
            pack: pack(),
            supply,
            system: Some(SystemPower { ac_online, has_battery: true, battery_percent: percent, battery_life_time: -1 }),
            measured_voltage_v: Some(battery.voltage_v),
            power_plan_name: real.power_plan_name.clone(),
        })
    }
}
