using System;

namespace WinBatLens.Models
{
    /// <summary>
    /// 表示單一時間點之即時功耗與系統遙測歷史紀錄。
    /// </summary>
    public class PowerHistoryRecord
    {
        /// <summary>紀錄產生之時間戳記文字（格式 yyyy-MM-dd HH:mm:ss）。</summary>
        public string TimestampText { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        /// <summary>事件類型說明（例如：「放電監測」、「AC市電充電」、「高負載警示」）。</summary>
        public string EventType { get; set; } = "放電監測";

        /// <summary>事件 UI 標籤樣式類別（如 Info, Warning, Success, Danger）。</summary>
        public string EventBadgeClass { get; set; } = "Info";

        /// <summary>電池端實測放電功率（瓦特 W）。0 表示當前無放電電流。</summary>
        public double DischargeRateW { get; set; }

        /// <summary>放電功率格式化文字。</summary>
        public string DischargeRateText => DischargeRateW > 0 ? $"{DischargeRateW:F1} W" : "0.0 W (未放電)";

        /// <summary>電池剩餘電量百分比。</summary>
        public int BatteryPercent { get; set; }

        /// <summary>電池電量百分比格式化文字。</summary>
        public string BatteryPercentText => $"{BatteryPercent}%";

        /// <summary>CPU 使用率百分比。</summary>
        public double CpuUsagePercent { get; set; }

        /// <summary>CPU 使用率格式化文字。</summary>
        public string CpuUsageText => $"{CpuUsagePercent:F1}%";

        /// <summary>獨立顯示卡 (dGPU) 使用率百分比。</summary>
        public double DgpuUsagePercent { get; set; }

        /// <summary>獨立顯示卡使用率格式化文字。</summary>
        public string DgpuUsageText => DgpuUsagePercent > 0 ? $"{DgpuUsagePercent:F1}%" : "0.0% (待機)";

        /// <summary>獨立顯示卡實測功耗（瓦特 W，來自 NVML 讀取）。</summary>
        public double DgpuPowerW { get; set; }

        /// <summary>獨立顯示卡功耗格式化文字。</summary>
        public string DgpuPowerText => DgpuPowerW > 0 ? $"{DgpuPowerW:F1} W" : "-- W";

        /// <summary>電池端實測電壓（伏特 V）。</summary>
        public double BatteryVoltageV { get; set; }

        /// <summary>電壓格式化文字。</summary>
        public string VoltageText => BatteryVoltageV > 0 ? $"{BatteryVoltageV:F2} V" : "-- V";

        /// <summary>電池端實測電流（安培 A）。</summary>
        public double BatteryCurrentA { get; set; }

        /// <summary>電流格式化文字。</summary>
        public string CurrentText => BatteryCurrentA > 0 ? $"{BatteryCurrentA:F2} A" : "-- A";

        /// <summary>物理遙測（電壓與電流）格式化綜合文字。</summary>
        public string TelemetryText => $"{VoltageText} | {CurrentText}";

        /// <summary>事件摘要說明。</summary>
        public string SummaryText { get; set; } = string.Empty;
    }
}
