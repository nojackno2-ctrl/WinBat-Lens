//! 即時功耗事件紀錄（含 CSV 匯出）與「電量百分比 → 電池端電壓」的持久化實測紀錄。

use std::collections::VecDeque;
use std::fs;
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};

use chrono::{DateTime, Local, NaiveDateTime, TimeZone};
use serde::{Deserialize, Deserializer, Serialize, Serializer};

use crate::models::RealTimePowerState;

const TIMESTAMP_FORMAT: &str = "%Y-%m-%d %H:%M:%S";

/// 事件 UI 標籤樣式類別。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BadgeClass {
    Info,
    Warning,
    Success,
    Danger,
}

/// 單一時間點之即時功耗與系統遙測歷史紀錄。
#[derive(Debug, Clone)]
pub struct PowerHistoryRecord {
    /// 紀錄產生之時間戳記文字（格式 yyyy-MM-dd HH:mm:ss）。
    pub timestamp_text: String,
    pub event_type: String,
    pub badge: BadgeClass,
    /// 電池端實測放電功率（瓦特 W）。0 表示當前無放電電流。
    pub discharge_rate_w: f64,
    pub battery_percent: i32,
    pub cpu_usage_percent: f64,
    pub dgpu_usage_percent: f64,
    /// 獨立顯示卡實測功耗（瓦特 W，來自 NVML 讀取）。
    pub dgpu_power_w: f64,
    pub battery_voltage_v: f64,
    pub battery_current_a: f64,
    pub summary_text: String,
}

impl PowerHistoryRecord {
    pub fn discharge_rate_text(&self) -> String {
        if self.discharge_rate_w > 0.0 {
            format!("{:.1} W", self.discharge_rate_w)
        } else {
            "0.0 W (未放電)".into()
        }
    }

    pub fn battery_percent_text(&self) -> String {
        format!("{}%", self.battery_percent)
    }

    pub fn cpu_usage_text(&self) -> String {
        format!("{:.1}%", self.cpu_usage_percent)
    }

    pub fn dgpu_usage_text(&self) -> String {
        if self.dgpu_usage_percent > 0.0 {
            format!("{:.1}%", self.dgpu_usage_percent)
        } else {
            "0.0% (待機)".into()
        }
    }

    pub fn dgpu_power_text(&self) -> String {
        if self.dgpu_power_w > 0.0 {
            format!("{:.1} W", self.dgpu_power_w)
        } else {
            "-- W".into()
        }
    }

    /// 物理遙測（電壓與電流）格式化綜合文字。
    pub fn telemetry_text(&self) -> String {
        let voltage =
            if self.battery_voltage_v > 0.0 { format!("{:.2} V", self.battery_voltage_v) } else { "-- V".into() };
        let current =
            if self.battery_current_a > 0.0 { format!("{:.2} A", self.battery_current_a) } else { "-- A".into() };
        format!("{voltage} | {current}")
    }
}

/// 最多保留之歷史事件紀錄筆數上限。
const MAX_RECORDS: usize = 500;

/// 定時週期採樣間隔。
const SAMPLE_INTERVAL: Duration = Duration::from_secs(5);

/// 即時功耗與插拔電事件歷史紀錄；最新的紀錄排在最前面。
#[derive(Default)]
pub struct PowerHistory {
    records: VecDeque<PowerHistoryRecord>,
    last_ac_status: Option<bool>,
    last_sample: Option<Instant>,
}

impl PowerHistory {
    pub fn records(&self) -> &VecDeque<PowerHistoryRecord> {
        &self.records
    }

    pub fn clear(&mut self) {
        self.records.clear();
    }

    /// 根據即時電源狀態，判定插拔電事件或定時（5 秒）記錄歷史採樣。
    pub fn add_from_power_state(&mut self, state: &RealTimePowerState) {
        self.add_at(state, Instant::now(), Local::now().format(TIMESTAMP_FORMAT).to_string());
    }

    fn add_at(&mut self, state: &RealTimePowerState, now: Instant, timestamp_text: String) {
        let is_ac = state.is_ac_online;
        let record = |event_type: &str, badge: BadgeClass, summary_text: String| PowerHistoryRecord {
            timestamp_text: timestamp_text.clone(),
            event_type: event_type.to_string(),
            badge,
            discharge_rate_w: state.discharge_rate_w,
            battery_percent: state.battery_percent,
            cpu_usage_percent: state.cpu_usage_percent,
            dgpu_usage_percent: state.dgpu_usage_percent,
            dgpu_power_w: if state.is_dgpu_power_measured { state.dgpu_power_w } else { 0.0 },
            // An unmeasured voltage is logged as absent (shown as "-- V"),
            // never as the nominal figure used internally for the current maths.
            battery_voltage_v: if state.is_voltage_measured { state.battery_voltage_v } else { 0.0 },
            battery_current_a: state.battery_current_a,
            summary_text,
        };

        // 1. 偵測 AC 市電插拔狀態切換事件
        if self.last_ac_status.is_some_and(|last| last != is_ac) {
            let entry = if is_ac {
                record("AC市電", BadgeClass::Success, format!("連接 AC 電源 | 目前電量: {}%", state.battery_percent))
            } else {
                record(
                    "電池放電",
                    BadgeClass::Warning,
                    format!(
                        "切換至電池供電 | 初始放電功率: {:.1} W | 目前電量: {}%",
                        state.discharge_rate_w, state.battery_percent
                    ),
                )
            };
            self.insert(entry);
        }
        self.last_ac_status = Some(is_ac);

        // 2. 定時週期採樣（每 5 秒一次）
        if self.last_sample.is_some_and(|last| now.duration_since(last) < SAMPLE_INTERVAL) {
            return;
        }
        self.last_sample = Some(now);

        let (event_type, badge) = if !is_ac && state.discharge_rate_w > 25.0 {
            ("⚠ 高耗電警示", BadgeClass::Danger)
        } else if state.is_charger_deficit {
            ("⚠ 外接電源不足", BadgeClass::Danger)
        } else if state.dgpu_usage_percent > 30.0 {
            ("獨顯運算", BadgeClass::Warning)
        } else if is_ac {
            ("AC 供電", BadgeClass::Success)
        } else {
            ("電池放電", BadgeClass::Info)
        };

        let summary = if state.is_charger_deficit {
            format!(
                "外接電源供電不足，電池補上 {:.1}W | {} | CPU: {:.0}% | 獨顯: {}",
                state.discharge_rate_w, state.battery_telemetry_text, state.cpu_usage_percent, state.dgpu_status_text
            )
        } else if is_ac {
            let brightness = if state.is_brightness_measured {
                format!(" | 螢幕亮度: {}%", state.screen_brightness_percent)
            } else {
                String::new()
            };
            format!(
                "市電正常 | {} | CPU: {:.0}% | 獨顯: {}{brightness}",
                state.battery_telemetry_text, state.cpu_usage_percent, state.dgpu_status_text
            )
        } else {
            format!(
                "放電中 ({:.1}W) | {} | CPU: {:.0}% | 獨顯: {}",
                state.discharge_rate_w, state.battery_telemetry_text, state.cpu_usage_percent, state.dgpu_status_text
            )
        };

        self.insert(record(event_type, badge, summary));
    }

    fn insert(&mut self, record: PowerHistoryRecord) {
        self.records.push_front(record);
        self.records.truncate(MAX_RECORDS);
    }

    /// 目前歷史紀錄的 CSV 內容（UTF-8 含 BOM，Excel 才能正確辨識中文）。
    pub fn to_csv(&self) -> String {
        let mut csv = String::from("\u{feff}");
        csv.push_str("時間戳記,事件類型,電池放電功率(W),目前電量(%),電壓(V),電流(A),CPU負載(%),獨顯負載(%),獨顯功耗(W),詳細狀態\r\n");

        for r in &self.records {
            csv.push_str(&format!(
                "{},{},{},{},{},{},{},{},{},{}\r\n",
                csv_field(&r.timestamp_text),
                csv_field(&r.event_type),
                r.discharge_rate_w,
                r.battery_percent,
                r.battery_voltage_v,
                r.battery_current_a,
                r.cpu_usage_percent,
                r.dgpu_usage_percent,
                r.dgpu_power_w,
                csv_field(&r.summary_text)
            ));
        }

        csv
    }

    /// 將目前歷史紀錄匯出為 UTF-8 CSV 檔案。
    pub fn export_to_csv(&self, path: &Path) -> std::io::Result<()> {
        fs::write(path, self.to_csv())
    }
}

/// 符合 RFC 4180 標準之 CSV 欄位跳脫轉義。
fn csv_field(value: &str) -> String {
    format!("\"{}\"", value.replace('"', "\"\""))
}

/// 以含時區位移的 ISO 8601 格式序列化本地時間，並寬鬆地讀回；與先前版本寫出的
/// 紀錄檔相容。
mod local_timestamp {
    use super::*;

    pub fn serialize<S: Serializer>(value: &NaiveDateTime, serializer: S) -> Result<S::Ok, S::Error> {
        let text = match Local.from_local_datetime(value).earliest() {
            Some(local) => local.to_rfc3339(),
            None => value.format("%Y-%m-%dT%H:%M:%S").to_string(),
        };
        serializer.serialize_str(&text)
    }

    pub fn deserialize<'de, D: Deserializer<'de>>(deserializer: D) -> Result<NaiveDateTime, D::Error> {
        let text = String::deserialize(deserializer)?;
        Ok(parse(&text).unwrap_or_default())
    }

    pub fn parse(text: &str) -> Option<NaiveDateTime> {
        if let Ok(with_offset) = DateTime::parse_from_rfc3339(text) {
            return Some(with_offset.with_timezone(&Local).naive_local());
        }
        // No offset: the timestamp was written as local wall-clock time.
        let seconds = text.get(..19)?;
        NaiveDateTime::parse_from_str(seconds, "%Y-%m-%dT%H:%M:%S").ok()
    }
}

/// 一個電量百分比的電池端實測電壓彙總。
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct BatteryVoltageHistoryPoint {
    /// 電池電量百分比，範圍為 0–100。
    pub battery_percent: i32,
    #[serde(rename = "AverageVoltageV")]
    pub average_voltage_v: f64,
    #[serde(rename = "MinimumVoltageV")]
    pub minimum_voltage_v: f64,
    #[serde(rename = "MaximumVoltageV")]
    pub maximum_voltage_v: f64,
    /// 此電量百分比累積的實測樣本數。
    pub sample_count: i32,
    #[serde(with = "local_timestamp")]
    pub last_recorded_at: NaiveDateTime,
}

impl BatteryVoltageHistoryPoint {
    pub fn new(battery_percent: i32) -> Self {
        Self {
            battery_percent,
            average_voltage_v: 0.0,
            minimum_voltage_v: 0.0,
            maximum_voltage_v: 0.0,
            sample_count: 0,
            last_recorded_at: NaiveDateTime::default(),
        }
    }

    pub fn battery_percent_text(&self) -> String {
        format!("{}%", self.battery_percent)
    }

    pub fn average_voltage_text(&self) -> String {
        format!("{:.2} V", self.average_voltage_v)
    }

    pub fn voltage_range_text(&self) -> String {
        if self.sample_count > 1 {
            format!("{:.2}–{:.2} V", self.minimum_voltage_v, self.maximum_voltage_v)
        } else {
            self.average_voltage_text()
        }
    }

    pub fn last_recorded_at_text(&self) -> String {
        self.last_recorded_at.format(TIMESTAMP_FORMAT).to_string()
    }

    /// 將一筆新的實測電壓加入此電量區間，並以累進平均避免保留大量原始樣本。
    /// 電壓不是有限正數時不記錄並傳回 false。
    pub fn add_sample(&mut self, voltage_v: f64, recorded_at: NaiveDateTime) -> bool {
        if !voltage_v.is_finite() || voltage_v <= 0.0 {
            return false;
        }

        if self.sample_count <= 0 {
            self.average_voltage_v = voltage_v;
            self.minimum_voltage_v = voltage_v;
            self.maximum_voltage_v = voltage_v;
            self.sample_count = 1;
        } else {
            self.average_voltage_v += (voltage_v - self.average_voltage_v) / (self.sample_count + 1) as f64;
            self.minimum_voltage_v = self.minimum_voltage_v.min(voltage_v);
            self.maximum_voltage_v = self.maximum_voltage_v.max(voltage_v);
            self.sample_count += 1;
        }

        self.last_recorded_at = recorded_at;
        true
    }

    fn is_valid(&self) -> bool {
        let positive = |v: f64| v.is_finite() && v > 0.0;
        (0..=100).contains(&self.battery_percent)
            && self.sample_count > 0
            && positive(self.average_voltage_v)
            && positive(self.minimum_voltage_v)
            && positive(self.maximum_voltage_v)
            && self.minimum_voltage_v <= self.maximum_voltage_v
    }
}

const CURRENT_FILE_VERSION: i32 = 1;

#[derive(Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
struct HistoryFile {
    version: i32,
    #[serde(with = "local_timestamp")]
    saved_at: NaiveDateTime,
    #[serde(default)]
    points: Vec<BatteryVoltageHistoryPoint>,
}

/// 累積並保存「電量百分比 → 電池端電壓」的實測紀錄。
pub struct VoltageHistory {
    points: Vec<BatteryVoltageHistoryPoint>,
    last_observed_percent: Option<i32>,
    path: Option<PathBuf>,
}

impl VoltageHistory {
    /// `%LOCALAPPDATA%\WinBatLens\battery-voltage-history.json`
    pub fn default_path() -> Option<PathBuf> {
        let base = std::env::var_os("LOCALAPPDATA")?;
        Some(PathBuf::from(base).join("WinBatLens").join("battery-voltage-history.json"))
    }

    /// 載入持久化電壓紀錄；損毀或版本不符的檔案會被忽略。
    pub fn load(path: Option<PathBuf>) -> Self {
        let mut points = path
            .as_deref()
            .and_then(|path| fs::read_to_string(path).ok())
            .and_then(|json| serde_json::from_str::<HistoryFile>(json.trim_start_matches('\u{feff}')).ok())
            .filter(|file| file.version == CURRENT_FILE_VERSION)
            .map(|file| file.points)
            .unwrap_or_default();

        points.retain(BatteryVoltageHistoryPoint::is_valid);
        points.sort_by_key(|point| point.battery_percent);
        points.dedup_by_key(|point| point.battery_percent);

        Self { points, last_observed_percent: None, path }
    }

    /// 依電量百分比遞增排序的歷史紀錄。
    pub fn points(&self) -> &[BatteryVoltageHistoryPoint] {
        &self.points
    }

    /// 目前累積的硬體實測樣本總數。
    pub fn total_sample_count(&self) -> i32 {
        self.points.iter().map(|point| point.sample_count.max(0)).sum()
    }

    /// 判斷目前百分比是否與上一筆觀察不同。
    pub fn has_percent_changed(last_observed: Option<i32>, current: i32) -> bool {
        (0..=100).contains(&current) && last_observed != Some(current)
    }

    /// 只在電量百分比變化時記錄一筆已確認為硬體實測的電壓。相同百分比的每秒
    /// 輪詢會被忽略；不同充放電循環再次經過同一百分比時，才會將該次變化加入
    /// 同一個百分比的彙總資料。若這次變化已加入紀錄則回傳 true。
    pub fn record_on_percent_change(
        &mut self,
        battery_percent: i32,
        voltage_v: f64,
        recorded_at: NaiveDateTime,
    ) -> bool {
        if !(0..=100).contains(&battery_percent) || !voltage_v.is_finite() || voltage_v <= 0.0 {
            return false;
        }

        let is_first_observation = self.last_observed_percent.is_none();
        if !Self::has_percent_changed(self.last_observed_percent, battery_percent) {
            return false;
        }
        self.last_observed_percent = Some(battery_percent);

        let index = match self.points.binary_search_by_key(&battery_percent, |point| point.battery_percent) {
            // An existing point already represents this percentage from an
            // earlier run. The first observation after a restart is not a
            // percentage change, so do not create a duplicate event. Once the
            // percentage moves and comes back, the event is recorded.
            Ok(_) if is_first_observation => return false,
            Ok(index) => index,
            Err(index) => {
                self.points.insert(index, BatteryVoltageHistoryPoint::new(battery_percent));
                index
            }
        };

        self.points[index].add_sample(voltage_v, recorded_at);

        // Percentage changes are infrequent compared with the one-second
        // monitor loop, so each accepted event is durable immediately.
        self.persist();
        true
    }

    /// 清除畫面與磁碟上的所有電壓紀錄。
    pub fn clear(&mut self) {
        self.points.clear();
        self.last_observed_percent = None;
        self.persist();
    }

    /// 把目前的彙總寫入磁碟。先寫暫存檔再取代，中途失敗不會留下半個檔案。
    pub fn persist(&self) {
        let Some(path) = &self.path else { return };

        let file = HistoryFile {
            version: CURRENT_FILE_VERSION,
            saved_at: Local::now().naive_local(),
            points: self.points.clone(),
        };
        let Ok(json) = serde_json::to_string_pretty(&file) else { return };

        if let Some(directory) = path.parent() {
            let _ = fs::create_dir_all(directory);
        }
        let temp_path = path.with_extension("json.tmp");
        if fs::write(&temp_path, json).is_ok() && fs::rename(&temp_path, path).is_err() {
            let _ = fs::remove_file(&temp_path);
        }
    }
}

#[cfg(test)]
mod tests {
    use chrono::NaiveDate;

    use super::*;

    fn at(hour: u32, minute: u32, second: u32) -> NaiveDateTime {
        NaiveDate::from_ymd_opt(2026, 8, 5).unwrap().and_hms_opt(hour, minute, second).unwrap()
    }

    #[test]
    fn aggregates_average_range_and_sample_count() {
        let mut point = BatteryVoltageHistoryPoint::new(42);
        assert!(point.add_sample(12.10, at(12, 0, 0)));
        assert!(point.add_sample(12.30, at(12, 0, 1)));

        assert_eq!(point.battery_percent, 42);
        assert!((point.average_voltage_v - 12.20).abs() < 0.005);
        assert_eq!(point.minimum_voltage_v, 12.10);
        assert_eq!(point.maximum_voltage_v, 12.30);
        assert_eq!(point.sample_count, 2);
        assert_eq!(point.last_recorded_at, at(12, 0, 1));
        assert_eq!(point.voltage_range_text(), "12.10–12.30 V");
        assert_eq!(point.last_recorded_at_text(), "2026-08-05 12:00:01");
    }

    #[test]
    fn rejects_invalid_voltage() {
        let mut point = BatteryVoltageHistoryPoint::new(42);
        for voltage in [0.0, -1.0, f64::NAN, f64::INFINITY] {
            assert!(!point.add_sample(voltage, at(12, 0, 0)));
        }
        assert_eq!(point.sample_count, 0);
    }

    #[test]
    fn records_only_when_battery_percent_changes() {
        assert!(!VoltageHistory::has_percent_changed(Some(50), 50));
        assert!(VoltageHistory::has_percent_changed(Some(50), 49));
        assert!(VoltageHistory::has_percent_changed(Some(50), 51));
        assert!(VoltageHistory::has_percent_changed(None, 50));
        assert!(!VoltageHistory::has_percent_changed(None, 101));
    }

    #[test]
    fn voltage_history_keeps_points_sorted_and_ignores_repeat_polls() {
        let mut history = VoltageHistory::load(None);

        assert!(history.record_on_percent_change(50, 15.8, at(12, 0, 0)));
        assert!(!history.record_on_percent_change(50, 15.9, at(12, 0, 1)));
        assert!(history.record_on_percent_change(49, 15.7, at(12, 5, 0)));
        assert!(history.record_on_percent_change(51, 16.0, at(12, 9, 0)));
        assert!(history.record_on_percent_change(50, 15.6, at(12, 10, 0)));
        assert!(!history.record_on_percent_change(101, 15.6, at(12, 11, 0)));
        assert!(!history.record_on_percent_change(48, 0.0, at(12, 11, 0)));

        let percents: Vec<i32> = history.points().iter().map(|p| p.battery_percent).collect();
        assert_eq!(percents, [49, 50, 51]);
        assert_eq!(history.points()[1].sample_count, 2);
        assert_eq!(history.total_sample_count(), 4);
    }

    #[test]
    fn voltage_history_round_trips_and_skips_the_first_observation_after_restart() {
        let directory = std::env::temp_dir().join(format!("winbat-lens-test-{}", std::process::id()));
        let path = directory.join("battery-voltage-history.json");
        let _ = fs::remove_dir_all(&directory);

        let mut history = VoltageHistory::load(Some(path.clone()));
        assert!(history.record_on_percent_change(80, 16.4, at(9, 0, 0)));
        assert!(history.record_on_percent_change(79, 16.3, at(9, 10, 0)));

        let mut reloaded = VoltageHistory::load(Some(path.clone()));
        assert_eq!(reloaded.points(), history.points());

        // Same percentage as before the restart: not a change event.
        assert!(!reloaded.record_on_percent_change(79, 16.2, at(9, 20, 0)));
        assert!(reloaded.record_on_percent_change(78, 16.1, at(9, 30, 0)));

        reloaded.clear();
        assert!(VoltageHistory::load(Some(path)).points().is_empty());
        let _ = fs::remove_dir_all(&directory);
    }

    #[test]
    fn reads_history_files_written_by_the_dotnet_build() {
        let json = r#"{
          "Version": 1,
          "SavedAt": "2026-08-05T12:00:01.1234567+08:00",
          "Points": [
            { "BatteryPercent": 60, "AverageVoltageV": 15.9, "MinimumVoltageV": 15.8, "MaximumVoltageV": 16.0,
              "SampleCount": 3, "LastRecordedAt": "2026-08-05T12:00:00.5+08:00" },
            { "BatteryPercent": 40, "AverageVoltageV": 15.2, "MinimumVoltageV": 15.2, "MaximumVoltageV": 15.2,
              "SampleCount": 1, "LastRecordedAt": "2026-08-04T08:30:00" },
            { "BatteryPercent": 140, "AverageVoltageV": 15.2, "MinimumVoltageV": 15.2, "MaximumVoltageV": 15.2,
              "SampleCount": 1, "LastRecordedAt": "2026-08-04T08:30:00" }
          ]
        }"#;
        let directory = std::env::temp_dir().join(format!("winbat-lens-compat-{}", std::process::id()));
        let path = directory.join("history.json");
        fs::create_dir_all(&directory).unwrap();
        fs::write(&path, json).unwrap();

        let history = VoltageHistory::load(Some(path));
        let _ = fs::remove_dir_all(&directory);

        let percents: Vec<i32> = history.points().iter().map(|p| p.battery_percent).collect();
        assert_eq!(percents, [40, 60]);
        assert_eq!(history.points()[0].last_recorded_at_text(), "2026-08-04 08:30:00");
        assert_eq!(history.points()[1].sample_count, 3);
        assert!(local_timestamp::parse("not a date").is_none());
    }

    fn state(is_ac: bool, discharge_w: f64) -> RealTimePowerState {
        RealTimePowerState {
            is_ac_online: is_ac,
            discharge_rate_w: discharge_w,
            battery_percent: 77,
            battery_voltage_v: 16.0,
            is_voltage_measured: true,
            ..Default::default()
        }
    }

    #[test]
    fn power_history_samples_every_five_seconds_and_logs_ac_transitions() {
        let mut history = PowerHistory::default();
        let start = Instant::now();
        let tick = |seconds: u64| start + Duration::from_secs(seconds);

        history.add_at(&state(true, 0.0), tick(0), "t0".into());
        history.add_at(&state(true, 0.0), tick(1), "t1".into());
        assert_eq!(history.records().len(), 1);
        assert_eq!(history.records()[0].event_type, "AC 供電");

        // Unplugged between samples: the transition is logged immediately.
        history.add_at(&state(false, 30.0), tick(2), "t2".into());
        assert_eq!(history.records().len(), 2);
        assert_eq!(history.records()[0].event_type, "電池放電");
        assert_eq!(history.records()[0].badge, BadgeClass::Warning);

        history.add_at(&state(false, 30.0), tick(5), "t5".into());
        assert_eq!(history.records().len(), 3);
        assert_eq!(history.records()[0].event_type, "⚠ 高耗電警示");
        assert_eq!(history.records()[0].badge, BadgeClass::Danger);
    }

    #[test]
    fn unmeasured_readings_are_logged_as_absent() {
        let mut history = PowerHistory::default();
        let unmeasured = RealTimePowerState {
            is_ac_online: true,
            battery_voltage_v: 15.4,
            is_voltage_measured: false,
            screen_brightness_percent: 75,
            is_brightness_measured: false,
            ..Default::default()
        };
        history.add_at(&unmeasured, Instant::now(), "t0".into());

        let record = &history.records()[0];
        assert_eq!(record.battery_voltage_v, 0.0);
        assert_eq!(record.telemetry_text(), "-- V | -- A");
        assert!(!record.summary_text.contains("15.4"));
        assert!(!record.summary_text.contains("75%"));
    }

    #[test]
    fn power_history_is_capped() {
        let mut history = PowerHistory::default();
        let start = Instant::now();
        for i in 0..(MAX_RECORDS as u64 + 20) {
            history.add_at(&state(true, 0.0), start + Duration::from_secs(i * 5), format!("t{i}"));
        }
        assert_eq!(history.records().len(), MAX_RECORDS);
        assert_eq!(history.records()[0].timestamp_text, format!("t{}", MAX_RECORDS + 19));
    }

    #[test]
    fn csv_export_escapes_quotes() {
        let mut history = PowerHistory::default();
        history.insert(PowerHistoryRecord {
            timestamp_text: "2026-08-05 12:00:00".into(),
            event_type: "AC 供電".into(),
            badge: BadgeClass::Success,
            discharge_rate_w: 0.0,
            battery_percent: 80,
            cpu_usage_percent: 12.5,
            dgpu_usage_percent: 0.0,
            dgpu_power_w: 19.2,
            battery_voltage_v: 16.01,
            battery_current_a: 0.0,
            summary_text: "say \"hi\", ok".into(),
        });

        let csv = history.to_csv();
        assert!(csv.starts_with('\u{feff}'));
        assert_eq!(
            csv.lines().nth(1).unwrap(),
            "\"2026-08-05 12:00:00\",\"AC 供電\",0,80,16.01,0,12.5,0,19.2,\"say \"\"hi\"\", ok\""
        );
    }
}
