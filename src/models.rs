//! 電池報告與即時遙測的資料模型。
//!
//! 報告模型以 PascalCase 序列化，讓「匯出 JSON」的欄位名稱與先前版本一致。

use chrono::{Local, NaiveDate};
use serde::Serialize;

/// 四捨六入五成雙到小數一位（與先前版本的 `Math.Round` 行為一致）。
pub fn round1(value: f64) -> f64 {
    (value * 10.0).round_ties_even() / 10.0
}

/// 四捨六入五成雙到小數兩位。
pub fn round2(value: f64) -> f64 {
    (value * 100.0).round_ties_even() / 100.0
}

/// 以千分位逗號格式化整數（例如 76000 -> "76,000"）。
pub fn thousands(value: i64) -> String {
    let digits = value.unsigned_abs().to_string();
    let mut out = String::with_capacity(digits.len() + digits.len() / 3 + 1);
    if value < 0 {
        out.push('-');
    }
    for (i, ch) in digits.chars().enumerate() {
        if i > 0 && (digits.len() - i) % 3 == 0 {
            out.push(',');
        }
        out.push(ch);
    }
    out
}

/// 依製造日期計算役齡（年，小數一位）。
pub fn age_years(manufacture_date: NaiveDate) -> f64 {
    let days = (Local::now().date_naive() - manufacture_date).num_days() as f64;
    round1(days / 365.25)
}

/// 系統基本資訊（電腦名稱、產品型號、BIOS 與 OS 版本等）。
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
pub struct SystemInfo {
    pub computer_name: String,
    pub system_product_name: String,
    pub bios: String,
    pub os_build: String,
    pub report_time: String,
}

impl Default for SystemInfo {
    fn default() -> Self {
        Self {
            computer_name: "Unknown PC".into(),
            system_product_name: "Windows PC".into(),
            bios: "N/A".into(),
            os_build: "N/A".into(),
            report_time: "N/A".into(),
        }
    }
}

/// 電池硬體規格與設計/滿充容量。
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
pub struct BatterySpecs {
    pub name: String,
    pub manufacturer: String,
    pub serial_number: String,
    pub chemistry: String,
    /// 設計容量（預設單位 mWh）。
    pub design_capacity: i32,
    /// 完全充電容量（預設單位 mWh）。
    pub full_charge_capacity: i32,
    /// 充電循環次數（硬體不支援時為 None）。
    pub cycle_count: Option<i32>,
    /// 容量單位標示（mWh 或 mAh）。
    pub unit: String,
    /// 電池芯製造日期（由電池驅動程式讀取；powercfg 報告無此欄位）。
    pub manufacture_date: Option<NaiveDate>,
    /// 依製造日期推算的役齡（年）。
    pub age_years: Option<f64>,
    /// 容量數據是否來自即時電池驅動程式（而非 powercfg 報告快照）。
    pub capacities_from_driver: bool,
}

impl Default for BatterySpecs {
    fn default() -> Self {
        Self {
            name: "Primary Battery".into(),
            manufacturer: "Windows PC".into(),
            serial_number: "N/A".into(),
            chemistry: "Li-ion".into(),
            design_capacity: 0,
            full_charge_capacity: 0,
            cycle_count: None,
            unit: "mWh".into(),
            manufacture_date: None,
            age_years: None,
            capacities_from_driver: false,
        }
    }
}

/// 健康狀況等級，決定儀表與徽章的語意顏色。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
pub enum StatusClass {
    Good,
    Warning,
    Danger,
    None,
}

/// 電池健康度指標與摘要評估。
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
pub struct HealthMetrics {
    pub has_battery: bool,
    /// 健康度是否成功計算（若缺乏滿充容量則為 false）。
    pub is_health_measured: bool,
    pub health_percent: f64,
    pub wear_percent: f64,
    pub capacity_loss: i32,
    pub status_label: String,
    pub status_class: StatusClass,
    pub summary_text: String,
}

impl Default for HealthMetrics {
    fn default() -> Self {
        Self {
            has_battery: true,
            is_health_measured: false,
            health_percent: 0.0,
            wear_percent: 0.0,
            capacity_loss: 0,
            status_label: "良好".into(),
            status_class: StatusClass::Good,
            summary_text: String::new(),
        }
    }
}

/// 歷史容量變遷紀錄項目。
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
pub struct CapacityHistoryItem {
    pub period: String,
    pub full_charge_capacity: i32,
    pub design_capacity: i32,
    /// 根據該時期數據計算之健康度百分比。
    pub health_percent: f64,
}

impl CapacityHistoryItem {
    pub fn new(period: String, full_charge_capacity: i32, design_capacity: i32) -> Self {
        let health_percent = if design_capacity > 0 {
            round1(full_charge_capacity as f64 / design_capacity as f64 * 100.0).min(100.0)
        } else {
            0.0
        };
        Self { period, full_charge_capacity, design_capacity, health_percent }
    }
}

/// 歷史使用時間紀錄項目（電池模式 vs 插電模式時間）。
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
pub struct UsageHistoryItem {
    pub period: String,
    pub battery_duration: String,
    pub ac_duration: String,
}

/// 電池續航力估計項目。
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
pub struct BatteryLifeEstimateItem {
    pub period: String,
    pub full_charge_estimate: String,
    pub design_cap_estimate: String,
}

/// 近期電池使用歷程紀錄。
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
pub struct RecentUsageItem {
    pub start_time: String,
    pub state: String,
    pub source: String,
    pub capacity_remaining: String,
}

/// 診斷訊息類型。序列化為小寫字串（info / warning / danger / success / tip）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum DiagnosticKind {
    Info,
    Warning,
    Danger,
    Success,
    Tip,
}

/// 健康度或系統診斷提示項目。
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "PascalCase")]
pub struct DiagnosticItem {
    #[serde(rename = "Type")]
    pub kind: DiagnosticKind,
    pub title: String,
    pub description: String,
}

/// 完整電池報告解析數據模型。
#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "PascalCase")]
pub struct BatteryReportData {
    pub system_info: SystemInfo,
    pub battery_specs: BatterySpecs,
    pub health_metrics: HealthMetrics,
    pub capacity_history: Vec<CapacityHistoryItem>,
    pub usage_history: Vec<UsageHistoryItem>,
    pub battery_life_estimates: Vec<BatteryLifeEstimateItem>,
    pub recent_usage: Vec<RecentUsageItem>,
    pub diagnostics: Vec<DiagnosticItem>,
    /// 資料載入與解析時間。
    pub loaded_at: String,
}

/// 外接充電器/變壓器供電能力評估狀況（Windows.System.Power.PowerManager）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum PowerSupplyCapability {
    /// 無法取得 API 數據或無法推論。
    #[default]
    Unknown,
    /// 目前使用電池運作，未連接外接電源。
    NotPresent,
    /// 外接充電器供電不足（例如用 65W PD 充電器推高負載筆電，電池仍持續放電）。
    Inadequate,
    /// 外接充電器供電充足。
    Adequate,
}

/// 1Hz 即時遙測之系統功耗與硬體狀態。
#[derive(Debug, Clone)]
pub struct RealTimePowerState {
    /// 本機是否有電池。桌上型電腦為 false，此時所有電池數值都不適用。
    pub has_battery: bool,
    pub is_ac_online: bool,
    pub is_charging: bool,
    pub power_status_text: String,
    pub battery_percent: i32,
    /// 電池放電功率（瓦特 W，實測值）。
    pub discharge_rate_w: f64,
    /// 電池充電功率（瓦特 W，實測值）。
    pub charging_rate_w: f64,
    pub discharge_rate_text: String,
    pub charging_status_text: String,
    pub estimated_time_remaining_text: String,
    pub battery_voltage_v: f64,
    pub is_voltage_measured: bool,
    pub battery_current_a: f64,
    pub battery_telemetry_text: String,
    pub power_plan_name: String,
    pub battery_temperature_c: f64,
    pub is_battery_temperature_measured: bool,
    /// 是否成功由電池驅動讀取到精確容量（mWh）。
    pub is_energy_measured: bool,
    pub remaining_capacity_mwh: i32,
    pub full_charged_capacity_mwh: i32,
    pub designed_capacity_mwh: i32,
    /// 真實 SOC 百分比（剩餘容量 / 完全充電容量 * 100%）。
    pub true_soc_percent: f64,
    /// 即時健康度百分比（完全充電容量 / 設計容量 * 100%）。
    pub driver_health_percent: f64,
    pub battery_energy_text: String,
    pub battery_capacity_health_text: String,
    pub is_discharge_rate_measured: bool,
    pub is_charge_rate_measured: bool,
    pub supply_capability: PowerSupplyCapability,
    /// 外接供電不足標記（插電狀態下電池仍持續放電，充電器無法涵蓋全機功耗）。
    pub is_charger_deficit: bool,
    pub cpu_usage_percent: f64,
    pub igpu_name: String,
    pub igpu_usage_percent: f64,
    pub has_discrete_gpu: bool,
    pub dgpu_name: String,
    pub dgpu_usage_percent: f64,
    /// 獨立顯示卡實測功耗（瓦特 W）。
    pub dgpu_power_w: f64,
    pub is_dgpu_power_measured: bool,
    pub dgpu_status_text: String,
    pub screen_brightness_percent: i32,
    pub is_brightness_measured: bool,
}

impl Default for RealTimePowerState {
    fn default() -> Self {
        Self {
            has_battery: true,
            is_ac_online: false,
            is_charging: false,
            power_status_text: "讀取中...".into(),
            battery_percent: 0,
            discharge_rate_w: 0.0,
            charging_rate_w: 0.0,
            discharge_rate_text: "-- W".into(),
            charging_status_text: "讀取中...".into(),
            estimated_time_remaining_text: "--".into(),
            battery_voltage_v: 0.0,
            is_voltage_measured: false,
            battery_current_a: 0.0,
            battery_telemetry_text: "-- V | -- A".into(),
            power_plan_name: "平衡 (Balanced)".into(),
            battery_temperature_c: 0.0,
            is_battery_temperature_measured: false,
            is_energy_measured: false,
            remaining_capacity_mwh: 0,
            full_charged_capacity_mwh: 0,
            designed_capacity_mwh: 0,
            true_soc_percent: 0.0,
            driver_health_percent: 0.0,
            battery_energy_text: "--".into(),
            battery_capacity_health_text: "--".into(),
            is_discharge_rate_measured: false,
            is_charge_rate_measured: false,
            supply_capability: PowerSupplyCapability::Unknown,
            is_charger_deficit: false,
            cpu_usage_percent: 0.0,
            igpu_name: "內建顯示晶片 (iGPU)".into(),
            igpu_usage_percent: 0.0,
            has_discrete_gpu: false,
            dgpu_name: "獨立顯示卡 (dGPU)".into(),
            dgpu_usage_percent: 0.0,
            dgpu_power_w: 0.0,
            is_dgpu_power_measured: false,
            dgpu_status_text: "0% (待機省電)".into(),
            screen_brightness_percent: 75,
            is_brightness_measured: false,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn formats_thousands_separators() {
        assert_eq!(thousands(0), "0");
        assert_eq!(thousands(999), "999");
        assert_eq!(thousands(1000), "1,000");
        assert_eq!(thousands(76000), "76,000");
        assert_eq!(thousands(1234567), "1,234,567");
        assert_eq!(thousands(-20000), "-20,000");
    }

    #[test]
    fn capacity_history_health_is_capped_at_100() {
        assert_eq!(CapacityHistoryItem::new("p".into(), 55_900, 76_000).health_percent, 73.6);
        assert_eq!(CapacityHistoryItem::new("p".into(), 80_000, 76_000).health_percent, 100.0);
        assert_eq!(CapacityHistoryItem::new("p".into(), 80_000, 0).health_percent, 0.0);
    }
}
