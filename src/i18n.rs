//! 雙語系（繁體中文 / 英文）UI 字串切換與查詢。

use std::sync::atomic::{AtomicBool, Ordering};

static ENGLISH: AtomicBool = AtomicBool::new(false);

/// 目前是否為英文介面。
pub fn is_english() -> bool {
    #[cfg(test)]
    if let Some(english) = test_override::get() {
        return english;
    }
    ENGLISH.load(Ordering::Relaxed)
}

/// Tests run in parallel and share the process-wide language, so a test that
/// needs a particular language pins it for its own thread instead.
#[cfg(test)]
pub mod test_override {
    use std::cell::Cell;

    thread_local! {
        static LANGUAGE: Cell<Option<bool>> = const { Cell::new(None) };
    }

    pub fn get() -> Option<bool> {
        LANGUAGE.with(Cell::get)
    }

    pub fn set_english(english: bool) {
        LANGUAGE.with(|language| language.set(Some(english)));
    }
}

/// 切換目前應用程式的語系設定（繁體中文 <-> 英文）。
pub fn toggle_language() {
    ENGLISH.fetch_xor(true, Ordering::Relaxed);
}

/// 依目前語系二選一；用於只出現一次、不值得進字典的句子。
pub fn pick<'a>(zh: &'a str, en: &'a str) -> &'a str {
    if is_english() {
        en
    } else {
        zh
    }
}

/// 根據鍵值與目前選擇的語系取得多國語言文字，若不存在則傳回原鍵值。
pub fn tr(key: &'static str) -> &'static str {
    let en = is_english();
    STRINGS.iter().find(|(k, _, _)| *k == key).map(|(_, zh, english)| if en { *english } else { *zh }).unwrap_or(key)
}

/// (鍵值, 繁體中文, English)
const STRINGS: &[(&str, &str, &str)] = &[
    ("TabVoltageHistory", "電池電壓紀錄", "Battery Voltage History"),
    ("VoltageHistoryTitle", "電池電壓紀錄 (0-100%)", "Battery Voltage by Charge Level (0-100%)"),
    (
        "VoltageHistorySummary",
        "已記錄 {0} / 101 個電量區間，累積 {1} 次電量變化事件",
        "Recorded {0} / 101 charge-level ranges, {1} percentage-change events",
    ),
    (
        "VoltageHistoryHint",
        "僅顯示電池驅動回報的實測電壓；同一電量未變化時不重複記錄。",
        "Only battery-driver measurements are shown; the same percentage is not recorded again until it changes.",
    ),
    ("BtnClearVoltageHistory", "清除電壓紀錄", "Clear Voltage History"),
    ("VoltagePercentColumn", "電量 (%)", "Battery (%)"),
    ("VoltageAverageColumn", "平均電壓", "Average voltage"),
    ("VoltageRangeColumn", "實測範圍", "Measured range"),
    ("VoltageEventsColumn", "變化次數", "Change events"),
    ("VoltageLastColumn", "最後紀錄", "Last recorded"),
    (
        "AppTitle",
        "WinBat Lens - Windows 電池健康度與即時硬體耗電監測",
        "WinBat Lens - Windows Battery Health & Real-Time Hardware Power Monitor",
    ),
    (
        "SubTitle",
        "Windows 電池健康診斷與全系統耗電監測",
        "Windows Battery Health Diagnostics & Full System Power Monitor",
    ),
    ("AutoStart", "開機自動啟動", "Launch on Windows Startup"),
    ("BtnOpenReport", "開啟 HTML 報告", "Open HTML Report"),
    ("BtnCheck", "執行電池檢測", "Run Battery Check"),
    ("BtnExportJson", "匯出 JSON", "Export JSON"),
    ("BtnLanguage", "English", "繁體中文"),
    ("HealthTitle", "電池健康度 (Health Score)", "Battery Health Score"),
    (
        "HealthSummaryDefault",
        "請點擊右上角「執行電池檢測」按鈕載入最新報告。",
        "Click 'Run Battery Check' in the top right to load report.",
    ),
    ("BatterySpecsTitle", "電池規格參數", "Battery Specifications"),
    ("SpecName", "電池名稱", "Battery Name"),
    ("SpecMfg", "製造商", "Manufacturer"),
    ("SpecChem", "化學材質", "Chemistry"),
    ("SpecMade", "出廠日期", "Manufacture Date"),
    ("SpecDesign", "設計容量", "Design Capacity"),
    ("SpecFull", "滿電容量", "Full Charge Capacity"),
    ("SpecLoss", "容量損耗", "Capacity Loss"),
    ("SpecCycles", "充放電循環", "Cycle Count"),
    ("CapacityHistoryTitle", "容量歷史紀錄", "Capacity Degradation History"),
    ("ColPeriod", "期間", "Period"),
    ("ColFullCap", "滿電容量", "Full Charge Cap"),
    ("ColDesignCap", "設計容量", "Design Cap"),
    ("ColHealthPct", "健康%", "Health %"),
    ("TabRealTime", "全系統硬體功耗分佈", "Hardware Power Breakdown"),
    ("TabHistoryLogs", "即時功耗與充放電歷史紀錄", "Power & Event History Logs"),
    ("TabDiagnostics", "智慧診斷與維護建議", "Smart Diagnostics & Tips"),
    ("TabLifeEstimates", "續航估算表", "Battery Life Estimates"),
    ("TabRecentUsage", "最近使用紀錄", "Recent Battery Usage"),
    ("CardTotalPower", "電池充放電功率 (實測)", "Battery Charge & Discharge (measured)"),
    ("CardEstTime", "電腦估算剩餘續航時間", "Estimated Battery Life Remaining"),
    ("CardTelemetry", "電池物理狀態 (電壓 / 電流)", "Battery Hardware State (V / A)"),
    ("CardPowerPlan", "Windows 電源模式", "Active Windows Power Scheme"),
    ("WaveformTitle", "60 秒即時功率走勢", "60-Second Real-Time Power Waveform"),
    ("LegendDischarge", "放電 (W)", "Discharge (W)"),
    ("LegendCharge", "充電 (+W)", "Charge (+W)"),
    ("LegendGpu", "獨顯 (W)", "dGPU (W)"),
    ("HardwareTitle", "硬體實測功耗", "Measured Hardware Power"),
    ("HwBatteryTelemetry", "電池物理狀態 (電壓 / 電流)", "Battery Telemetry (V / A)"),
    ("HwEnergy", "電池蓄電量 (Wh)", "Battery Energy (Wh)"),
    ("HwPowerPlan", "Windows 運作電源計劃", "Windows Power Scheme"),
    ("HwDgpu", "獨立顯示卡 (dGPU)", "Discrete GPU (dGPU)"),
    ("HwIgpu", "內建顯示晶片 (iGPU)", "Integrated GPU (iGPU)"),
    ("HwTelemetrySource", "電池類別驅動程式", "Battery class driver"),
    ("HwPowerPlanState", "目前使用中", "Active scheme"),
    ("BtnExportCsv", "匯出 CSV 紀錄", "Export CSV"),
    ("BtnClearHistory", "清除紀錄", "Clear History"),
    ("HistoryLogHeader", "即時功耗與充放電事件紀錄", "Power and Charge/Discharge Event History"),
    (
        "PowerHistoryEmpty",
        "監測開始後，功耗事件會顯示在這裡。",
        "Power events will appear here after monitoring begins.",
    ),
    (
        "VoltageHistoryEmpty",
        "電量百分比改變且驅動回報實測電壓後，紀錄會顯示在這裡。",
        "Records appear after charge level changes and the battery driver reports a measured voltage.",
    ),
    (
        "DiagnosticsEmpty",
        "執行電池檢測後，診斷與維護建議會顯示在這裡。",
        "Run a battery check to see diagnostics and maintenance guidance.",
    ),
    (
        "LifeEstimatesEmpty",
        "載入含續航資料的電池報告後，估算結果會顯示在這裡。",
        "Load a battery report with runtime data to see estimates here.",
    ),
    (
        "RecentUsageEmpty",
        "載入含使用紀錄的電池報告後，資料會顯示在這裡。",
        "Load a battery report with usage records to see data here.",
    ),
    ("LoadingTitle", "正在執行 Windows 電池健康檢測", "Running Windows Battery Health Check"),
    (
        "LoadingDetail",
        "powercfg /batteryreport 正在產生並分析最新報告。",
        "powercfg /batteryreport is generating and analyzing the latest report.",
    ),
    ("ColTime", "時間戳記", "Timestamp"),
    ("ColEvent", "事件類型", "Event Type"),
    ("ColPower", "放電/充電功率", "Power (W)"),
    ("ColBattery", "電量 (%)", "Battery (%)"),
    ("ColTelemetry", "電池狀態 (V / A)", "Battery (V / A)"),
    ("ColCpu", "CPU 負載", "CPU Load"),
    ("ColDgpu", "獨顯負載", "dGPU Load"),
    ("ColDgpuPower", "獨顯功耗", "dGPU Power"),
    ("ColSummary", "詳細狀態記錄", "Log Details"),
    ("ColFullEstimate", "滿電容量續航", "At Full Charge"),
    ("ColDesignEstimate", "設計容量續航", "At Design Capacity"),
    ("ColStartTime", "開始時間", "Start Time"),
    ("ColState", "狀態", "State"),
    ("ColSource", "電源", "Source"),
    ("ColCapacityRemaining", "剩餘容量", "Capacity Remaining"),
    ("ReportTime", "報告時間", "Report time"),
    ("TrayShow", "開啟 WinBat Lens 主畫面", "Open WinBat Lens"),
    ("TrayCheck", "執行電池健康檢測", "Run Battery Health Check"),
    ("TrayAutoStart", "開機自動啟動", "Launch on Windows Startup"),
    ("TrayExit", "結束程式", "Exit"),
    (
        "TrayTooltip",
        "WinBat Lens - 電池健康度與即時耗電監測",
        "WinBat Lens - Battery Health & Live Power Monitor",
    ),
    ("TrayBalloonTitle", "WinBat Lens 已縮小至托盤", "WinBat Lens minimized to tray"),
    (
        "TrayBalloonText",
        "程式將在背景持續為您進行即時耗電與電池狀態監測。",
        "Power and battery monitoring continues in the background.",
    ),
    (
        "InstanceVersionTitle",
        "WinBat Lens - 偵測到不同版本",
        "WinBat Lens - Different Version Running",
    ),
    (
        "InstanceVersionText",
        "背景已有 WinBat Lens v{0} 正在執行，而您啟動的是 {1}。\n\n要結束執行中的 v{0}，改用 {1} 嗎？\n\n選擇「否」則會叫出執行中的 v{0} 視窗。",
        "WinBat Lens v{0} is already running in the background, but you launched {1}.\n\nClose the running v{0} and switch to {1}?\n\nChoosing No brings the running v{0} to the front instead.",
    ),
    (
        "InstanceReplaceFailedTitle",
        "WinBat Lens - 無法切換版本",
        "WinBat Lens - Could Not Switch Version",
    ),
    (
        "InstanceReplaceFailedText",
        "無法結束執行中的 WinBat Lens v{0}。\n\n請在系統匣圖示上按右鍵選擇「結束程式」，然後再啟動一次。",
        "The running WinBat Lens v{0} could not be closed.\n\nRight-click its system tray icon, choose Exit, then launch again.",
    ),
    ("InstanceRunningTitle", "WinBat Lens 已在執行中", "WinBat Lens Is Already Running"),
    (
        "InstanceRunningText",
        "WinBat Lens 已在背景執行，因此不會重複開啟。\n\n請點擊系統匣（工作列右下角）的 WinBat Lens 圖示來開啟主畫面。",
        "WinBat Lens is already running in the background, so a second copy was not started.\n\nClick its system tray icon to open the main window.",
    ),
];

/// 以 `{0}`、`{1}` 佔位符做簡單的位置替換。
pub fn format(template: &str, args: &[&str]) -> String {
    let mut out = template.to_string();
    for (i, arg) in args.iter().enumerate() {
        out = out.replace(&format!("{{{i}}}"), arg);
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn every_key_is_unique_and_translated() {
        for (i, (key, zh, en)) in STRINGS.iter().enumerate() {
            assert!(!zh.is_empty() && !en.is_empty(), "{key} is missing a translation");
            assert!(STRINGS[..i].iter().all(|(other, _, _)| other != key), "{key} is defined twice");
        }
    }

    #[test]
    fn language_can_be_pinned_per_test_thread() {
        test_override::set_english(true);
        assert_eq!(tr("TrayExit"), "Exit");
        assert_eq!(pick("中文", "English"), "English");

        test_override::set_english(false);
        assert_eq!(tr("TrayExit"), "結束程式");
        assert_eq!(tr("NoSuchKey"), "NoSuchKey");
    }

    #[test]
    fn formats_positional_placeholders() {
        assert_eq!(format("v{0} -> {1}, v{0}", &["1.1.8", "v2.0.0"]), "v1.1.8 -> v2.0.0, v1.1.8");
    }
}
