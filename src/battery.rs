//! Everything the battery class driver will tell us about the pack, read
//! with `IOCTL_BATTERY_QUERY_STATUS` (instantaneous state) and
//! `IOCTL_BATTERY_QUERY_INFORMATION` (the pack's own identity, its
//! capacities, its temperature and its runtime estimate).
//!
//! This replaces `Win32_Battery.DischargeRate`, which is blank on a great many
//! laptops. A rate of exactly 0 is a legitimate reading, not a failure — a pack
//! that is full and on AC passes no current. Genuine unavailability is
//! signalled by the driver as BATTERY_UNKNOWN_RATE, which is reported
//! separately.
//!
//! Being a direct device IOCTL this needs no elevation and no WMI/COM, so it is
//! cheap enough to poll every second.
//!
//! Not every firmware implements every information level. Levels that fail
//! repeatedly are given up on (see [`MAX_LEVEL_FAILURES`]) so an unsupported
//! one costs nothing after the first few ticks, and the caller is told the
//! value is absent rather than handed a substitute.

use std::ffi::c_void;
use std::mem::size_of;
use std::time::{Duration, Instant};

use chrono::NaiveDate;
use windows::core::{GUID, PCWSTR};
use windows::Win32::Devices::DeviceAndDriverInstallation::{
    SetupDiDestroyDeviceInfoList, SetupDiEnumDeviceInterfaces, SetupDiGetClassDevsW, SetupDiGetDeviceInterfaceDetailW,
    DIGCF_DEVICEINTERFACE, DIGCF_PRESENT, SP_DEVICE_INTERFACE_DATA, SP_DEVICE_INTERFACE_DETAIL_DATA_W,
};
use windows::Win32::Foundation::{CloseHandle, GENERIC_READ, GENERIC_WRITE, HANDLE};
use windows::Win32::Storage::FileSystem::{
    CreateFileW, FILE_FLAGS_AND_ATTRIBUTES, FILE_SHARE_READ, FILE_SHARE_WRITE, OPEN_EXISTING,
};
use windows::Win32::System::IO::DeviceIoControl;

use crate::models::{age_years, round1, round2};

/// One instantaneous reading of the pack.
#[derive(Debug, Clone, Copy, Default)]
pub struct Reading {
    pub is_charging: bool,
    /// False when the driver reported BATTERY_UNKNOWN_RATE.
    pub is_rate_known: bool,
    /// Watts flowing out of the pack; 0 when not discharging.
    pub discharge_w: f64,
    /// Watts flowing into the pack; 0 when not charging.
    pub charge_w: f64,
    /// Pack voltage, or 0 when the driver does not report it.
    pub voltage_v: f64,
    /// Energy still in the pack, in mWh. Unlike the integer percentage from
    /// GetSystemPowerStatus this has real resolution.
    pub remaining_capacity_mwh: i32,
    pub is_capacity_known: bool,
    /// The pack's own temperature in °C — the battery's, not the CPU thermal
    /// zone. Hardware-dependent: plenty of ACPI batteries do not implement the
    /// level at all, hence the companion flag.
    pub temperature_c: f64,
    pub is_temperature_known: bool,
    /// The driver's own estimate of remaining runtime at the present rate, in
    /// seconds. Unknown while charging or on AC.
    pub estimated_runtime_seconds: i32,
    pub is_estimated_runtime_known: bool,
}

/// What the driver knows about the pack rather than its momentary state.
/// Capacities and cycle count do drift, so this is re-read on a slow cadence
/// rather than cached forever.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct PackInfo {
    /// False on a desktop, or if the driver refused the query.
    pub is_valid: bool,
    /// True when the driver reports capacities in its own arbitrary units
    /// instead of mWh (BATTERY_CAPACITY_RELATIVE). Health as a ratio is still
    /// meaningful; energy in Wh is not, and must not be shown as though it were.
    pub is_capacity_relative: bool,
    /// Four ASCII characters from the pack, e.g. "LION".
    pub chemistry: String,
    pub designed_capacity_mwh: i32,
    pub full_charged_capacity_mwh: i32,
    /// None when the pack does not count cycles (0 is reported as absent).
    pub cycle_count: Option<i32>,
    pub device_name: String,
    pub manufacture_name: String,
    /// Date the cells were made. powercfg's report does not carry this at all,
    /// and it is the only way to tell a pack that has aged badly in two years
    /// from one that has aged well in six.
    pub manufacture_date: Option<NaiveDate>,
}

impl PackInfo {
    /// Health as full-charge over design capacity, capped at 100%.
    pub fn health_percent(&self) -> Option<f64> {
        (self.designed_capacity_mwh > 0 && self.full_charged_capacity_mwh > 0).then(|| {
            round1(self.full_charged_capacity_mwh as f64 / self.designed_capacity_mwh as f64 * 100.0).min(100.0)
        })
    }

    #[allow(dead_code)]
    pub fn age_years(&self) -> Option<f64> {
        self.manufacture_date.map(age_years)
    }
}

const BATTERY_DISCHARGING: u32 = 0x0000_0002;
const BATTERY_CHARGING: u32 = 0x0000_0004;
const BATTERY_UNKNOWN_RATE: i32 = i32::MIN;
const BATTERY_UNKNOWN_VOLTAGE: u32 = 0xFFFF_FFFF;
const BATTERY_UNKNOWN_CAPACITY: u32 = 0xFFFF_FFFF;
const BATTERY_UNKNOWN_TIME: u32 = 0xFFFF_FFFF;

const BATTERY_CAPACITY_RELATIVE: u32 = 0x4000_0000;

const IOCTL_BATTERY_QUERY_TAG: u32 = 0x0029_4040;
const IOCTL_BATTERY_QUERY_INFORMATION: u32 = 0x0029_4044;
const IOCTL_BATTERY_QUERY_STATUS: u32 = 0x0029_404C;

// BATTERY_QUERY_INFORMATION_LEVEL
const LEVEL_BATTERY_INFORMATION: u32 = 0;
const LEVEL_TEMPERATURE: u32 = 2;
const LEVEL_ESTIMATED_TIME: u32 = 3;
const LEVEL_DEVICE_NAME: u32 = 4;
const LEVEL_MANUFACTURE_DATE: u32 = 5;
const LEVEL_MANUFACTURE_NAME: u32 = 6;
// BatteryUniqueID (level 7) is deliberately not queried: on the pack this was
// developed against the driver answers with its manufacturer and device name
// concatenated rather than a serial number, and nothing in the UI has a use
// for it.

/// How many consecutive IOCTL failures mark an information level as
/// unimplemented by this firmware. A handful of retries rides out a transient
/// failure during start-up; after that we stop asking.
const MAX_LEVEL_FAILURES: u32 = 3;

/// Capacities and cycle count change over hours, not seconds, so the pack
/// information is re-read once a minute rather than every tick.
const PACK_INFO_REFRESH: Duration = Duration::from_secs(60);

const GUID_DEVCLASS_BATTERY: GUID = GUID::from_u128(0x72631e54_78a4_11d0_bcf7_00aa00b7b32a);

#[repr(C)]
#[derive(Default)]
struct BatteryWaitStatus {
    battery_tag: u32,
    timeout: u32,
    power_state: u32,
    low_capacity: u32,
    high_capacity: u32,
}

#[repr(C)]
#[derive(Default)]
struct BatteryStatus {
    power_state: u32,
    capacity: u32,
    voltage: u32,
    /// Signed milliwatts.
    rate: i32,
}

#[repr(C)]
struct BatteryQueryInformation {
    battery_tag: u32,
    /// BATTERY_QUERY_INFORMATION_LEVEL
    information_level: u32,
    /// mW to assume; 0 means the present rate.
    at_rate: i32,
}

#[repr(C)]
#[derive(Default)]
struct BatteryInformation {
    capabilities: u32,
    technology: u8,
    reserved: [u8; 3],
    chemistry: [u8; 4],
    designed_capacity: u32,
    full_charged_capacity: u32,
    default_alert1: u32,
    default_alert2: u32,
    critical_bias: u32,
    cycle_count: u32,
}

#[repr(C)]
#[derive(Default)]
struct BatteryManufactureDate {
    day: u8,
    month: u8,
    year: u16,
}

/// An open battery device plus everything learned about the pack behind it.
/// Owned by the monitoring thread; nothing here is shared.
pub struct BatteryTelemetry {
    handle: Option<HANDLE>,
    tag: u32,
    pack_info: PackInfo,
    last_pack_info: Option<Instant>,
    temperature_failures: u32,
    estimated_time_failures: u32,
}

// The handle is an ordinary kernel object handle with no thread affinity.
unsafe impl Send for BatteryTelemetry {}

impl BatteryTelemetry {
    /// Opens the first battery device, if the machine has one.
    pub fn open() -> Self {
        let mut this = Self {
            handle: None,
            tag: 0,
            pack_info: PackInfo::default(),
            last_pack_info: None,
            temperature_failures: 0,
            estimated_time_failures: 0,
        };
        this.open_first_battery();
        this.refresh_pack_info();
        this
    }

    /// The pack's identity, capacities and cycle count, re-read at most once a
    /// minute. Returns an invalid [`PackInfo`] on a machine with no battery.
    pub fn pack_info(&mut self) -> &PackInfo {
        let stale = self.last_pack_info.is_none_or(|at| at.elapsed() >= PACK_INFO_REFRESH);
        if !self.pack_info.is_valid || stale {
            self.refresh_pack_info();
        }
        &self.pack_info
    }

    /// Reads the battery. Returns None on desktops, or if the device vanished
    /// (an external pack being removed, say).
    pub fn read(&mut self) -> Option<Reading> {
        self.handle?;

        let status = match self.query_status() {
            Some(status) => status,
            None => {
                // The tag is invalidated whenever the pack changes; re-open
                // once and retry before giving up.
                self.close_current();
                self.open_first_battery();
                self.handle?;
                self.query_status()?
            }
        };

        let charging = status.power_state & BATTERY_CHARGING != 0;
        let _discharging = status.power_state & BATTERY_DISCHARGING != 0;
        let rate_known = status.rate != BATTERY_UNKNOWN_RATE;

        // Sign convention is negative-for-discharge, but not every driver
        // honours it — trust the state flags for direction and take the
        // magnitude.
        let watts = if rate_known { status.rate.unsigned_abs() as f64 / 1000.0 } else { 0.0 };
        let capacity_known = status.capacity != BATTERY_UNKNOWN_CAPACITY;

        let temperature = self.query_temperature();
        let runtime = self.query_estimated_runtime();

        Some(Reading {
            is_charging: charging,
            is_rate_known: rate_known,
            discharge_w: if rate_known && !charging { round1(watts) } else { 0.0 },
            charge_w: if rate_known && charging { round1(watts) } else { 0.0 },
            voltage_v: if status.voltage == BATTERY_UNKNOWN_VOLTAGE || status.voltage == 0 {
                0.0
            } else {
                round2(status.voltage as f64 / 1000.0)
            },
            remaining_capacity_mwh: if capacity_known { status.capacity as i32 } else { 0 },
            is_capacity_known: capacity_known,
            temperature_c: temperature.unwrap_or(0.0),
            is_temperature_known: temperature.is_some(),
            estimated_runtime_seconds: runtime.unwrap_or(0),
            is_estimated_runtime_known: runtime.is_some(),
        })
    }

    fn close_current(&mut self) {
        if let Some(handle) = self.handle.take() {
            unsafe {
                let _ = CloseHandle(handle);
            }
        }
        self.tag = 0;

        // Everything below was learned about the device behind that handle: a
        // different pack may implement a different set of levels, so both the
        // cached information and the give-up counters start over.
        self.pack_info = PackInfo::default();
        self.last_pack_info = None;
        self.temperature_failures = 0;
        self.estimated_time_failures = 0;
    }

    /// One DeviceIoControl round trip between two plain-old-data values.
    fn ioctl<I, O>(&self, code: u32, input: &I, output: &mut O) -> Option<u32> {
        let handle = self.handle?;
        ioctl_raw(handle, code, input, (output as *mut O).cast(), size_of::<O>())
    }

    fn query_status(&self) -> Option<BatteryStatus> {
        let wait = BatteryWaitStatus { battery_tag: self.tag, ..Default::default() };
        let mut status = BatteryStatus::default();
        self.ioctl(IOCTL_BATTERY_QUERY_STATUS, &wait, &mut status)?;
        Some(status)
    }

    fn new_query(&self, level: u32, at_rate: i32) -> BatteryQueryInformation {
        BatteryQueryInformation { battery_tag: self.tag, information_level: level, at_rate }
    }

    /// The temperature and estimated-runtime levels, both of which answer a
    /// single 32-bit value.
    fn query_info_u32(&self, level: u32) -> Option<u32> {
        let mut value = 0u32;
        // AtRate 0 means "at the rate the pack is draining right now".
        self.ioctl(IOCTL_BATTERY_QUERY_INFORMATION, &self.new_query(level, 0), &mut value)?;
        Some(value)
    }

    /// Pack temperature, converted from the driver's tenths of a degree
    /// Kelvin. Values outside -40..80 °C are rejected: firmware that does not
    /// really implement the level tends to answer 0, which would otherwise
    /// render as a plausible-looking -273 °C.
    fn query_temperature(&mut self) -> Option<f64> {
        if self.temperature_failures >= MAX_LEVEL_FAILURES {
            return None;
        }

        let Some(tenths_kelvin) = self.query_info_u32(LEVEL_TEMPERATURE) else {
            self.temperature_failures += 1;
            return None;
        };
        self.temperature_failures = 0;

        let celsius = tenths_kelvin as f64 / 10.0 - 273.15;
        (-40.0..=80.0).contains(&celsius).then(|| round1(celsius))
    }

    /// The driver's own runtime estimate at the present rate. Reported as
    /// BATTERY_UNKNOWN_TIME whenever the pack is not discharging, which is an
    /// ordinary answer rather than a failed level — so it does not count
    /// towards giving up on the query.
    fn query_estimated_runtime(&mut self) -> Option<i32> {
        if self.estimated_time_failures >= MAX_LEVEL_FAILURES {
            return None;
        }

        let Some(value) = self.query_info_u32(LEVEL_ESTIMATED_TIME) else {
            self.estimated_time_failures += 1;
            return None;
        };
        self.estimated_time_failures = 0;

        // A day or more is the firmware saying "no idea", not a forecast.
        (value != BATTERY_UNKNOWN_TIME && value != 0 && value < 86_400).then_some(value as i32)
    }

    fn refresh_pack_info(&mut self) {
        self.last_pack_info = Some(Instant::now());

        let mut info = BatteryInformation::default();
        let query = self.new_query(LEVEL_BATTERY_INFORMATION, 0);
        if self.ioctl(IOCTL_BATTERY_QUERY_INFORMATION, &query, &mut info).is_none() {
            self.pack_info = PackInfo::default();
            return;
        }

        let capacity = |raw: u32| if raw == BATTERY_UNKNOWN_CAPACITY { 0 } else { raw as i32 };

        // Strings and the manufacture date never change for a given pack, so
        // they are carried over instead of re-read every refresh.
        let (device_name, manufacture_name, manufacture_date) = if self.pack_info.is_valid {
            (
                std::mem::take(&mut self.pack_info.device_name),
                std::mem::take(&mut self.pack_info.manufacture_name),
                self.pack_info.manufacture_date,
            )
        } else {
            (
                self.query_string(LEVEL_DEVICE_NAME),
                self.query_string(LEVEL_MANUFACTURE_NAME),
                self.query_manufacture_date(),
            )
        };

        self.pack_info = PackInfo {
            is_valid: true,
            is_capacity_relative: info.capabilities & BATTERY_CAPACITY_RELATIVE != 0,
            chemistry: read_chemistry(&info.chemistry),
            designed_capacity_mwh: capacity(info.designed_capacity),
            full_charged_capacity_mwh: capacity(info.full_charged_capacity),
            cycle_count: (info.cycle_count > 0).then_some(info.cycle_count as i32),
            device_name,
            manufacture_name,
            manufacture_date,
        };
    }

    fn query_manufacture_date(&self) -> Option<NaiveDate> {
        let mut date = BatteryManufactureDate::default();
        let query = self.new_query(LEVEL_MANUFACTURE_DATE, 0);
        self.ioctl(IOCTL_BATTERY_QUERY_INFORMATION, &query, &mut date)?;

        if !(1990..=2100).contains(&date.year) {
            return None;
        }
        NaiveDate::from_ymd_opt(date.year as i32, date.month as u32, date.day as u32)
    }

    /// The variable-length string levels. 256 wide characters is far more than
    /// any of these fields uses, and one buffer is cheaper than probing for
    /// the exact size.
    fn query_string(&self, level: u32) -> String {
        let Some(handle) = self.handle else {
            return String::new();
        };

        let mut buffer = [0u16; 256];
        let query = self.new_query(level, 0);
        let returned = ioctl_raw(
            handle,
            IOCTL_BATTERY_QUERY_INFORMATION,
            &query,
            buffer.as_mut_ptr().cast(),
            size_of::<[u16; 256]>(),
        );

        match returned {
            Some(bytes) if bytes > 0 => {
                let len = buffer.iter().position(|&ch| ch == 0).unwrap_or(buffer.len());
                String::from_utf16_lossy(&buffer[..len]).trim().to_string()
            }
            _ => String::new(),
        }
    }

    fn open_first_battery(&mut self) {
        for path in enumerate_battery_paths() {
            let opened = unsafe {
                CreateFileW(
                    PCWSTR(path.as_ptr()),
                    (GENERIC_READ | GENERIC_WRITE).0,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    None,
                    OPEN_EXISTING,
                    FILE_FLAGS_AND_ATTRIBUTES(0),
                    None,
                )
            };
            let Ok(handle) = opened else { continue };

            // Zero timeout: do not wait for a tag.
            let timeout = 0u32;
            let mut tag = 0u32;
            let answered =
                ioctl_raw(handle, IOCTL_BATTERY_QUERY_TAG, &timeout, (&raw mut tag).cast(), size_of::<u32>());

            if answered.is_some() && tag != 0 {
                self.handle = Some(handle);
                self.tag = tag;
                return;
            }

            unsafe {
                let _ = CloseHandle(handle);
            }
        }
    }
}

impl Drop for BatteryTelemetry {
    fn drop(&mut self) {
        self.close_current();
    }
}

/// Returns the number of bytes the driver wrote, or None if the call failed.
fn ioctl_raw<I>(handle: HANDLE, code: u32, input: &I, output: *mut c_void, output_size: usize) -> Option<u32> {
    let mut returned = 0u32;
    unsafe {
        DeviceIoControl(
            handle,
            code,
            Some((input as *const I).cast()),
            size_of::<I>() as u32,
            Some(output),
            output_size as u32,
            Some(&mut returned),
            None,
        )
    }
    .ok()
    .map(|()| returned)
}

/// Four ASCII bytes, not null-terminated: "LION", "LiP", "NiMH"…
fn read_chemistry(bytes: &[u8; 4]) -> String {
    bytes
        .iter()
        .take_while(|&&b| b != 0)
        .filter(|&&b| (0x20..=0x7E).contains(&b))
        .map(|&b| b as char)
        .collect::<String>()
        .trim()
        .to_string()
}

/// Null-terminated device interface paths of every present battery.
fn enumerate_battery_paths() -> Vec<Vec<u16>> {
    let mut paths = Vec::new();

    let Ok(set) = (unsafe {
        SetupDiGetClassDevsW(Some(&GUID_DEVCLASS_BATTERY), PCWSTR::null(), None, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE)
    }) else {
        return paths;
    };

    for index in 0.. {
        let mut interface =
            SP_DEVICE_INTERFACE_DATA { cbSize: size_of::<SP_DEVICE_INTERFACE_DATA>() as u32, ..Default::default() };

        let found = unsafe { SetupDiEnumDeviceInterfaces(set, None, &GUID_DEVCLASS_BATTERY, index, &mut interface) };
        if found.is_err() {
            break;
        }

        // First call only reports how large the detail structure needs to be.
        let mut needed = 0u32;
        unsafe {
            let _ = SetupDiGetDeviceInterfaceDetailW(set, &interface, None, 0, Some(&mut needed), None);
        }
        if needed == 0 {
            continue;
        }

        // u64 storage keeps the buffer aligned for the detail structure.
        let mut buffer = vec![0u64; (needed as usize).div_ceil(8)];
        let detail = buffer.as_mut_ptr().cast::<SP_DEVICE_INTERFACE_DETAIL_DATA_W>();

        unsafe {
            // cbSize covers only the fixed part of the struct.
            (*detail).cbSize = size_of::<SP_DEVICE_INTERFACE_DETAIL_DATA_W>() as u32;

            if SetupDiGetDeviceInterfaceDetailW(set, &interface, Some(detail), needed, None, None).is_ok() {
                let start = (&raw const (*detail).DevicePath).cast::<u16>();
                let max_chars =
                    (needed as usize - std::mem::offset_of!(SP_DEVICE_INTERFACE_DETAIL_DATA_W, DevicePath)) / 2;
                let chars = std::slice::from_raw_parts(start, max_chars);
                let len = chars.iter().position(|&ch| ch == 0).unwrap_or(max_chars);
                if len > 0 {
                    let mut path = chars[..len].to_vec();
                    path.push(0);
                    paths.push(path);
                }
            }
        }
    }

    unsafe {
        let _ = SetupDiDestroyDeviceInfoList(set);
    }

    paths
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_chemistry_bytes() {
        assert_eq!(read_chemistry(b"LION"), "LION");
        assert_eq!(read_chemistry(b"LiP\0"), "LiP");
        assert_eq!(read_chemistry(&[0, 0, 0, 0]), "");
        assert_eq!(read_chemistry(&[b'L', 0x01, b'i', 0]), "Li");
    }

    #[test]
    fn pack_health_is_capped_and_optional() {
        let pack = PackInfo { designed_capacity_mwh: 76_000, full_charged_capacity_mwh: 56_000, ..Default::default() };
        assert_eq!(pack.health_percent(), Some(73.7));

        let over = PackInfo { designed_capacity_mwh: 50_000, full_charged_capacity_mwh: 52_000, ..Default::default() };
        assert_eq!(over.health_percent(), Some(100.0));

        assert_eq!(PackInfo::default().health_percent(), None);
    }

    #[test]
    fn driver_structs_match_the_ioctl_layouts() {
        assert_eq!(size_of::<BatteryWaitStatus>(), 20);
        assert_eq!(size_of::<BatteryStatus>(), 16);
        assert_eq!(size_of::<BatteryQueryInformation>(), 12);
        assert_eq!(size_of::<BatteryInformation>(), 36);
        assert_eq!(size_of::<BatteryManufactureDate>(), 4);
    }
}
