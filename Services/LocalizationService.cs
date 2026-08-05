using System;
using System.Collections.Generic;

namespace WinBatLens.Services
{
    /// <summary>
    /// 表示應用程式支援之語系（繁體中文與英文）。
    /// </summary>
    public enum AppLanguage
    {
        /// <summary>繁體中文 (Traditional Chinese)</summary>
        TraditionalChinese,

        /// <summary>英文 (English)</summary>
        English
    }

    /// <summary>
    /// 提供雙語系（繁體中文 / 英文）UI 字串切換與查詢服務。
    /// </summary>
    public class LocalizationService
    {
        /// <summary>目前應用程式顯示語系設定。</summary>
        public static AppLanguage CurrentLanguage { get; set; } = AppLanguage.TraditionalChinese;

        private static readonly Dictionary<string, string> ZhTwStrings = new Dictionary<string, string>
        {
            ["AppTitle"] = "WinBat Lens - Windows 電池健康度與即時硬體耗電監測",
            ["SubTitle"] = "Windows 電池健康診斷與全系統耗電監測",
            ["AutoStart"] = "🚀 開機自動啟動",
            ["BtnOpenReport"] = "📁 開啟 HTML 報告",
            ["BtnCheck"] = "⚡ 執行電池檢測",
            ["BtnExportJson"] = "📥 匯出 JSON",
            ["BtnLanguage"] = "🌐 English",
            ["HealthTitle"] = "電池健康度 (Health Score)",
            ["HealthSummaryDefault"] = "請點擊右上角「執行電池檢測」按鈕載入最新報告。",
            ["BatterySpecsTitle"] = "🔋 電池規格參數",
            ["SpecName"] = "電池名稱",
            ["SpecMfg"] = "製造商",
            ["SpecChem"] = "化學材質",
            ["SpecMade"] = "出廠日期",
            ["SpecDesign"] = "設計容量",
            ["SpecFull"] = "滿電容量",
            ["SpecLoss"] = "容量損耗",
            ["SpecCycles"] = "充放電循環",
            ["CapacityHistoryTitle"] = "📈 容量歷史紀錄",
            ["ColPeriod"] = "期間",
            ["ColFullCap"] = "滿電容量",
            ["ColDesignCap"] = "設計容量",
            ["ColHealthPct"] = "健康%",
            ["TabRealTime"] = " ⚡ 全系統硬體功耗分佈 ",
            ["TabHistoryLogs"] = " 📉 即時功耗與充放電歷史紀錄 ",
            ["TabDiagnostics"] = " 💡 智慧診斷與維護建議 ",
            ["TabLifeEstimates"] = " ⏱️ 續航估算表 ",
            ["TabRecentUsage"] = " 📊 最近使用紀錄 ",
            ["CardTotalPower"] = "⚡ 電池充放電功率 (實測)",
            ["CardEstTime"] = "⏱️ 電腦估算剩餘續航時間",
            ["CardTelemetry"] = "🔋 電池物理狀態 (電壓 / 電流)",
            ["CardPowerPlan"] = "⚙️ Windows 電源模式",
            ["WaveformTitle"] = "📈 60 秒即時動態走勢波形圖 (Task Manager Style Live Graph)",
            ["LegendDischarge"] = "放電 (W)",
            ["LegendCharge"] = "充電 (+W)",
            ["LegendGpu"] = "獨顯 (W)",
            ["HardwareTitle"] = "💻 硬體實測功耗 (Measured Hardware Power)",
            ["HwBatteryTelemetry"] = "🔋 電池物理狀態 (電壓 / 電流)",
            ["HwEnergy"] = "🔋 電池蓄電量 (Wh)",
            ["HwPowerPlan"] = "⚙️ Windows 運作電源計劃",
            ["HwDgpu"] = "🎮 獨立顯示卡 (dGPU)",
            ["BtnExportCsv"] = "📥 匯出 CSV 紀錄",
            ["BtnClearHistory"] = "🗑️ 清除紀錄",
            ["HistoryLogHeader"] = "📋 即時動態功耗與充放電事件歷史日誌 (Live Power & Event Logs)",
            ["ColTime"] = "時間戳記",
            ["ColEvent"] = "事件類型",
            ["ColPower"] = "放電/充電功率",
            ["ColBattery"] = "電量 (%)",
            ["ColTelemetry"] = "電池狀態 (V / A)",
            ["ColCpu"] = "CPU 負載",
            ["ColDgpu"] = "獨顯負載",
            ["ColScreen"] = "螢幕功耗",
            ["ColSummary"] = "詳細狀態記錄",
            ["TrayShow"] = "⚡ 開啟 WinBat Lens 主畫面",
            ["TrayCheck"] = "🔄 執行電池健康檢測",
            ["TrayAutoStart"] = "🚀 開機自動啟動",
            ["TrayExit"] = "❌ 結束程式",
            ["TrayTooltip"] = "WinBat Lens - 電池健康度與即時耗電監測",
            ["TrayBalloonTitle"] = "WinBat Lens 已縮小至托盤",
            ["TrayBalloonText"] = "程式將在背景持續為您進行即時耗電與電池狀態監測。",
            ["InstanceVersionTitle"] = "WinBat Lens - 偵測到不同版本",
            ["InstanceVersionText"] = "背景已有 WinBat Lens v{0} 正在執行，而您啟動的是 {1}。\n\n要結束執行中的 v{0}，改用 {1} 嗎？\n\n選擇「否」則會叫出執行中的 v{0} 視窗。",
            ["InstanceReplaceFailedTitle"] = "WinBat Lens - 無法切換版本",
            ["InstanceReplaceFailedText"] = "無法結束執行中的 WinBat Lens v{0}。\n\n請在系統匣圖示上按右鍵選擇「結束程式」，然後再啟動一次。",
            ["InstanceRunningTitle"] = "WinBat Lens 已在執行中",
            ["InstanceRunningText"] = "WinBat Lens 已在背景執行，因此不會重複開啟。\n\n請點擊系統匣（工作列右下角）的 WinBat Lens 圖示來開啟主畫面。"
        };

        private static readonly Dictionary<string, string> EnUsStrings = new Dictionary<string, string>
        {
            ["AppTitle"] = "WinBat Lens - Windows Battery Health & Real-Time Hardware Power Monitor",
            ["SubTitle"] = "Windows Battery Health Diagnostics & Full System Power Monitor",
            ["AutoStart"] = "🚀 Launch on Windows Startup",
            ["BtnOpenReport"] = "📁 Open HTML Report",
            ["BtnCheck"] = "⚡ Run Battery Check",
            ["BtnExportJson"] = "📥 Export JSON",
            ["BtnLanguage"] = "🌐 繁體中文",
            ["HealthTitle"] = "Battery Health Score",
            ["HealthSummaryDefault"] = "Click 'Run Battery Check' in the top right to load report.",
            ["BatterySpecsTitle"] = "🔋 Battery Specifications",
            ["SpecName"] = "Battery Name",
            ["SpecMfg"] = "Manufacturer",
            ["SpecChem"] = "Chemistry",
            ["SpecMade"] = "Manufacture Date",
            ["SpecDesign"] = "Design Capacity",
            ["SpecFull"] = "Full Charge Capacity",
            ["SpecLoss"] = "Capacity Loss",
            ["SpecCycles"] = "Cycle Count",
            ["CapacityHistoryTitle"] = "📈 Capacity Degradation History",
            ["ColPeriod"] = "Period",
            ["ColFullCap"] = "Full Charge Cap",
            ["ColDesignCap"] = "Design Cap",
            ["ColHealthPct"] = "Health %",
            ["TabRealTime"] = " ⚡ Hardware Power Breakdown ",
            ["TabHistoryLogs"] = " 📉 Power & Event History Logs ",
            ["TabDiagnostics"] = " 💡 Smart Diagnostics & Tips ",
            ["TabLifeEstimates"] = " ⏱️ Battery Life Estimates ",
            ["TabRecentUsage"] = " 📊 Recent Battery Usage ",
            ["CardTotalPower"] = "⚡ Battery Charge & Discharge (measured)",
            ["CardEstTime"] = "⏱️ Estimated Battery Life Remaining",
            ["CardTelemetry"] = "🔋 Battery Hardware State (V / A)",
            ["CardPowerPlan"] = "⚙️ Active Windows Power Scheme",
            ["WaveformTitle"] = "📈 60-Second Real-Time Power & Load Waveform Graph",
            ["LegendDischarge"] = "Discharge (W)",
            ["LegendCharge"] = "Charge (+W)",
            ["LegendGpu"] = "dGPU (W)",
            ["HardwareTitle"] = "💻 Measured Hardware Power",
            ["HwBatteryTelemetry"] = "🔋 Battery Telemetry (V / A)",
            ["HwEnergy"] = "🔋 Battery Energy (Wh)",
            ["HwPowerPlan"] = "⚙️ Windows Power Scheme",
            ["HwDgpu"] = "🎮 Discrete GPU (dGPU)",
            ["BtnExportCsv"] = "📥 Export CSV",
            ["BtnClearHistory"] = "🗑️ Clear History",
            ["HistoryLogHeader"] = "📋 Real-Time Dynamic Power & Charge/Discharge Event Logs",
            ["ColTime"] = "Timestamp",
            ["ColEvent"] = "Event Type",
            ["ColPower"] = "Power (W)",
            ["ColBattery"] = "Battery (%)",
            ["ColTelemetry"] = "Battery (V / A)",
            ["ColCpu"] = "CPU Load",
            ["ColDgpu"] = "dGPU Load",
            ["ColScreen"] = "Screen Power",
            ["ColSummary"] = "Log Details",
            ["TrayShow"] = "⚡ Open WinBat Lens",
            ["TrayCheck"] = "🔄 Run Battery Health Check",
            ["TrayAutoStart"] = "🚀 Launch on Windows Startup",
            ["TrayExit"] = "❌ Exit",
            ["TrayTooltip"] = "WinBat Lens - Battery Health & Live Power Monitor",
            ["TrayBalloonTitle"] = "WinBat Lens minimized to tray",
            ["TrayBalloonText"] = "Power and battery monitoring continues in the background.",
            ["InstanceVersionTitle"] = "WinBat Lens - Different Version Running",
            ["InstanceVersionText"] = "WinBat Lens v{0} is already running in the background, but you launched {1}.\n\nClose the running v{0} and switch to {1}?\n\nChoosing No brings the running v{0} to the front instead.",
            ["InstanceReplaceFailedTitle"] = "WinBat Lens - Could Not Switch Version",
            ["InstanceReplaceFailedText"] = "The running WinBat Lens v{0} could not be closed.\n\nRight-click its system tray icon, choose Exit, then launch again.",
            ["InstanceRunningTitle"] = "WinBat Lens Is Already Running",
            ["InstanceRunningText"] = "WinBat Lens is already running in the background, so a second copy was not started.\n\nClick its system tray icon to open the main window."
        };

        /// <summary>
        /// 根據鍵值與目前選擇的語系取得多國語言文字。
        /// </summary>
        /// <param name="key">語系鍵值。</param>
        /// <returns>翻譯後的文字內容，若不存在則傳回原鍵值。</returns>
        public static string Get(string key)
        {
            var dict = CurrentLanguage == AppLanguage.English ? EnUsStrings : ZhTwStrings;
            return dict.TryGetValue(key, out var val) ? val : key;
        }

        /// <summary>
        /// 切換目前應用程式的語系設定（繁體中文 &lt;-&gt; 英文）。
        /// </summary>
        public static void ToggleLanguage()
        {
            CurrentLanguage = CurrentLanguage == AppLanguage.TraditionalChinese 
                ? AppLanguage.English 
                : AppLanguage.TraditionalChinese;
        }
    }
}
