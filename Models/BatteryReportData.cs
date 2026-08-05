using System;
using System.Collections.Generic;
using WinBatLens.Services;

namespace WinBatLens.Models
{
    /// <summary>
    /// 表示系統基本資訊（電腦名稱、產品型號、BIOS 與 OS 版本等）。
    /// </summary>
    public class SystemInfo
    {
        /// <summary>電腦名稱。</summary>
        public string ComputerName { get; set; } = "Unknown PC";

        /// <summary>系統產品名稱 / 型號。</summary>
        public string SystemProductName { get; set; } = "Windows PC";

        /// <summary>BIOS 版本號與日期。</summary>
        public string Bios { get; set; } = "N/A";

        /// <summary>作業系統組建版本。</summary>
        public string OsBuild { get; set; } = "N/A";

        /// <summary>報告產生時間。</summary>
        public string ReportTime { get; set; } = "N/A";
}
    /// <summary>
    /// 表示電池硬體規格與設計/滿充容量。
    /// </summary>
    public class BatterySpecs
    {
        /// <summary>電池名稱。</summary>
        public string Name { get; set; } = "Primary Battery";

        /// <summary>電池製造商名稱。</summary>
        public string Manufacturer { get; set; } = "Windows PC";

        /// <summary>電池序號。</summary>
        public string SerialNumber { get; set; } = "N/A";

        /// <summary>電池化學材質（如 Li-ion）。</summary>
        public string Chemistry { get; set; } = "Li-ion";

        /// <summary>設計容量（預設單位 mWh）。</summary>
        public int DesignCapacity { get; set; }

        /// <summary>完全充電容量（預設單位 mWh）。</summary>
        public int FullChargeCapacity { get; set; }

        /// <summary>充電循環次數（硬體不支援時可能為 null）。</summary>
        public int? CycleCount { get; set; }

        /// <summary>容量單位標示（如 mWh 或 mAh）。</summary>
        public string Unit { get; set; } = "mWh";

        /// <summary>
        /// 電池芯製造日期（由電池驅動程式讀取）。
        /// powercfg 報告無此欄位，且許多電池韌體未實作查詢，故可能為 null。
        /// </summary>
        public DateTime? ManufactureDate { get; set; }

        /// <summary>
        /// 計算電池的估計出廠年份壽命（根據製造日期）。
        /// </summary>
        public double? AgeYears => ManufactureDate.HasValue
            ? Math.Round((DateTime.Now - ManufactureDate.Value).TotalDays / 365.25, 1)
            : null;

        /// <summary>
        /// 標示上述容量數據是否來自即時電池驅動程式（而非 powercfg 報告快照）。
        /// </summary>
        public bool CapacitiesFromDriver { get; set; }
    }

    /// <summary>
    /// 表示電池健康度指標與摘要評估。
    /// </summary>
    public class HealthMetrics
    {
        /// <summary>系統是否安裝/偵測到電池。</summary>
        public bool HasBattery { get; set; } = true;

        /// <summary>健康度是否成功計算（若缺乏滿充容量則為 false）。</summary>
        public bool IsHealthMeasured { get; set; }

        /// <summary>健康度百分比（滿充容量 / 設計容量 * 100%）。</summary>
        public double HealthPercent { get; set; }

        /// <summary>損耗百分比（100% - 健康度百分比）。</summary>
        public double WearPercent { get; set; }

        /// <summary>累積損耗容量（設計容量 - 滿充容量）。</summary>
        public int CapacityLoss { get; set; }

        /// <summary>健康狀況狀態文字標籤（例如：良好、普通、需維修）。</summary>
        public string StatusLabel { get; set; } = "良好";

        /// <summary>健康狀況 CSS/UI 樣式類別（Good, Warning, Critical）。</summary>
        public string StatusClass { get; set; } = "Good";

        /// <summary>健康狀況分析說明摘要文字。</summary>
        public string SummaryText { get; set; } = string.Empty;
    }

    /// <summary>
    /// 表示歷史容量變遷紀錄項目。
    /// </summary>
    public class CapacityHistoryItem
    {
        /// <summary>統計時間區間說明。</summary>
        public string Period { get; set; } = string.Empty;

        /// <summary>該時期的完全充電容量。</summary>
        public int FullChargeCapacity { get; set; }

        /// <summary>該時期的設計容量。</summary>
        public int DesignCapacity { get; set; }

        /// <summary>根據該時期數據計算之健康度百分比。</summary>
        public double HealthPercent => DesignCapacity > 0 
            ? Math.Min(100.0, Math.Round((double)FullChargeCapacity / DesignCapacity * 100.0, 1)) 
            : 0;
    }

    /// <summary>
    /// 表示歷史使用時間紀錄項目（電池模式 vs 插電模式時間）。
    /// </summary>
    public class UsageHistoryItem
    {
        /// <summary>統計時間區間說明。</summary>
        public string Period { get; set; } = string.Empty;

        /// <summary>使用電池運作的時間。</summary>
        public string BatteryDuration { get; set; } = string.Empty;

        /// <summary>使用交流電（插電）運作的時間。</summary>
        public string AcDuration { get; set; } = string.Empty;
    }

    /// <summary>
    /// 表示電池續航力估計項目。
    /// </summary>
    public class BatteryLifeEstimateItem
    {
        /// <summary>統計時間區間說明。</summary>
        public string Period { get; set; } = string.Empty;

        /// <summary>基於完全充電容量之續航估計。</summary>
        public string FullChargeEstimate { get; set; } = string.Empty;

        /// <summary>基於設計容量之續航估計。</summary>
        public string DesignCapEstimate { get; set; } = string.Empty;
    }

    /// <summary>
    /// 表示近期電池使用歷程紀錄。
    /// </summary>
    public class RecentUsageItem
    {
        /// <summary>事件起始時間。</summary>
        public string StartTime { get; set; } = string.Empty;

        /// <summary>系統運作狀態（如 Active、Suspended）。</summary>
        public string State { get; set; } = string.Empty;

        /// <summary>電源來源（Battery 或 AC）。</summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>剩餘容量百分比與電量數值。</summary>
        public string CapacityRemaining { get; set; } = string.Empty;
    }

    /// <summary>
    /// 表示健康度或系統診斷提示項目。
    /// </summary>
    public class DiagnosticItem
    {
        /// <summary>診斷訊息類型（info, warning, danger）。</summary>
        public string Type { get; set; } = "info";

        /// <summary>診斷項目標題。</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>診斷項目詳細說明。</summary>
        public string Description { get; set; } = string.Empty;
    }

    /// <summary>
    /// 表示 1Hz 即時遙測之系統功耗與硬體狀態。
    /// </summary>
    public class RealTimePowerState
    {
        /// <summary>是否已連接交流電源（插電）。</summary>
        public bool IsAcOnline { get; set; }

        /// <summary>電池是否正在充電中。</summary>
        public bool IsCharging { get; set; }

        /// <summary>供電狀態描述文字（例如：插電中、使用電池中）。</summary>
        public string PowerStatusText { get; set; } = "讀取中...";

        /// <summary>電池剩餘百分比（0 - 100%）。</summary>
        public int BatteryPercent { get; set; }

        /// <summary>電池放電功率（瓦特 W，實測值）。</summary>
        public double DischargeRateW { get; set; }

        /// <summary>電池充電功率（瓦特 W，實測值）。</summary>
        public double ChargingRateW { get; set; }

        /// <summary>放電功率格式化顯示文字（例如：12.5 W）。</summary>
        public string DischargeRateText { get; set; } = "-- W";

        /// <summary>充電狀態格式化顯示文字。</summary>
        public string ChargingStatusText { get; set; } = "讀取中...";

        /// <summary>預估剩餘使用時間或充滿所需時間格式化文字。</summary>
        public string EstimatedTimeRemainingText { get; set; } = "--";

        /// <summary>電池端實測電壓（伏特 V）。</summary>
        public double BatteryVoltageV { get; set; }

        /// <summary>是否成功讀取到硬體電壓值。</summary>
        public bool IsVoltageMeasured { get; set; }

        /// <summary>電池端實測電流（安培 A）。</summary>
        public double BatteryCurrentA { get; set; }

        /// <summary>電池實測電壓與電流格式化顯示文字。</summary>
        public string BatteryTelemetryText { get; set; } = "-- V | -- A";

        /// <summary>目前 Windows 電源計劃名稱。</summary>
        public string PowerPlanName { get; set; } = "平衡 (Balanced)";

        /// <summary>電池包實測溫度（攝氏 ℃）。</summary>
        public double BatteryTemperatureC { get; set; }

        /// <summary>是否成功由電池驅動讀取到電池包溫度。</summary>
        public bool IsBatteryTemperatureMeasured { get; set; }

        /// <summary>是否成功由電池驅動讀取到精確容量（mWh）。</summary>
        public bool IsEnergyMeasured { get; set; }

        /// <summary>剩餘能量容量（mWh）。</summary>
        public int RemainingCapacityMWh { get; set; }

        /// <summary>當前完全充電容量（mWh）。</summary>
        public int FullChargedCapacityMWh { get; set; }

        /// <summary>設計容量（mWh）。</summary>
        public int DesignedCapacityMWh { get; set; }

        /// <summary>真實 SOC 百分比（剩餘容量 / 完全充電容量 * 100%）。</summary>
        public double TrueSocPercent { get; set; }

        /// <summary>即時健康度百分比（完全充電容量 / 設計容量 * 100%）。</summary>
        public double DriverHealthPercent { get; set; }

        /// <summary>電池能量容量格式化顯示文字。</summary>
        public string BatteryEnergyText { get; set; } = "--";

        /// <summary>電池容量健康度格式化顯示文字。</summary>
        public string BatteryCapacityHealthText { get; set; } = "--";

        /// <summary>是否成功讀取到放電功率。</summary>
        public bool IsDischargeRateMeasured { get; set; }

        /// <summary>是否成功讀取到充電功率。</summary>
        public bool IsChargeRateMeasured { get; set; }

        /// <summary>外接變壓器/充電器能力評估狀況。</summary>
        public PowerSupplyCapability SupplyCapability { get; set; } = PowerSupplyCapability.Unknown;

        /// <summary>
        /// 外接供電不足標記（插電狀態下電池仍持續放電，充電器無法涵蓋全機功耗）。
        /// </summary>
        public bool IsChargerDeficit { get; set; }

        /// <summary>CPU 使用率百分比。</summary>
        public double CpuUsagePercent { get; set; }

        /// <summary>內建顯示晶片 (iGPU) 名稱。</summary>
        public string IgpuName { get; set; } = "內建顯示晶片 (iGPU)";

        /// <summary>內建顯示晶片使用率百分比。</summary>
        public double IgpuUsagePercent { get; set; }

        /// <summary>系統是否配備獨立顯示卡 (dGPU)。</summary>
        public bool HasDiscreteGpu { get; set; }

        /// <summary>獨立顯示卡名稱。</summary>
        public string DgpuName { get; set; } = "獨立顯示卡 (dGPU)";

        /// <summary>獨立顯示卡使用率百分比。</summary>
        public double DgpuUsagePercent { get; set; }

        /// <summary>獨立顯示卡實測功耗（瓦特 W）。</summary>
        public double DgpuPowerW { get; set; }

        /// <summary>是否成功讀取到 dGPU 功耗。</summary>
        public bool IsDgpuPowerMeasured { get; set; }

        /// <summary>獨立顯示卡狀態格式化文字。</summary>
        public string DgpuStatusText { get; set; } = "0% (待機省電)";

        /// <summary>螢幕亮度百分比（0 - 100%）。</summary>
        public int ScreenBrightnessPercent { get; set; } = 75;

        /// <summary>是否成功讀取到螢幕亮度。</summary>
        public bool IsBrightnessMeasured { get; set; }
    }

    /// <summary>
    /// 表示完整電池報告解析數據模型。
    /// </summary>
    public class BatteryReportData
    {
        /// <summary>系統基本資訊。</summary>
        public SystemInfo SystemInfo { get; set; } = new SystemInfo();

        /// <summary>電池規格資訊。</summary>
        public BatterySpecs BatterySpecs { get; set; } = new BatterySpecs();

        /// <summary>健康度指標。</summary>
        public HealthMetrics HealthMetrics { get; set; } = new HealthMetrics();

        /// <summary>歷史容量變遷清單。</summary>
        public List<CapacityHistoryItem> CapacityHistory { get; set; } = new List<CapacityHistoryItem>();

        /// <summary>歷史使用時間清單。</summary>
        public List<UsageHistoryItem> UsageHistory { get; set; } = new List<UsageHistoryItem>();

        /// <summary>續航力估算清單。</summary>
        public List<BatteryLifeEstimateItem> BatteryLifeEstimates { get; set; } = new List<BatteryLifeEstimateItem>();

        /// <summary>近期使用歷程紀錄。</summary>
        public List<RecentUsageItem> RecentUsage { get; set; } = new List<RecentUsageItem>();

        /// <summary>診斷提示與建議清單。</summary>
        public List<DiagnosticItem> Diagnostics { get; set; } = new List<DiagnosticItem>();

        /// <summary>資料載入與解析時間。</summary>
        public DateTime LoadedAt { get; set; } = DateTime.Now;
    }
}
