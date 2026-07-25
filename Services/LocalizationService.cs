using System;
using System.Collections.Generic;

namespace WinBatLens.Services
{
    public enum AppLanguage
    {
        TraditionalChinese,
        English
    }

    public class LocalizationService
    {
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
            ["HwPowerPlan"] = "⚙️ Windows 運作電源計劃",
            ["HwDgpu"] = "🎮 獨立顯示卡 (dGPU)",
            ["GpuListTitle"] = "🎮 系統顯示卡清單 (Graphics Hardware List)",
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
            ["TrayBalloonText"] = "程式將在背景持續為您進行即時耗電與電池狀態監測。"
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
            ["HwPowerPlan"] = "⚙️ Windows Power Scheme",
            ["HwDgpu"] = "🎮 Discrete GPU (dGPU)",
            ["GpuListTitle"] = "🎮 System Graphics Hardware List",
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
            ["TrayBalloonText"] = "Power and battery monitoring continues in the background."
        };

        public static string Get(string key)
        {
            var dict = CurrentLanguage == AppLanguage.English ? EnUsStrings : ZhTwStrings;
            return dict.TryGetValue(key, out var val) ? val : key;
        }

        public static void ToggleLanguage()
        {
            CurrentLanguage = CurrentLanguage == AppLanguage.TraditionalChinese 
                ? AppLanguage.English 
                : AppLanguage.TraditionalChinese;
        }
    }
}
