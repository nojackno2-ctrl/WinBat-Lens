using System;

namespace WinBatLens.Models
{
    public class PowerHistoryRecord
    {
        public string TimestampText { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        public string EventType { get; set; } = "放電監測"; // "電池放電", "AC市電充電", "電源狀態切換", "高負載警示"
        public string EventBadgeClass { get; set; } = "Info"; // "Info", "Warning", "Success", "Danger"
        
        // Measured at the pack. 0 means no current was flowing (on AC, or a
        // full battery), which is a real state rather than a missing reading.
        public double DischargeRateW { get; set; }
        public string DischargeRateText => DischargeRateW > 0 ? $"{DischargeRateW:F1} W" : "0.0 W (未放電)";

        public int BatteryPercent { get; set; }
        public string BatteryPercentText => $"{BatteryPercent}%";

        public double CpuUsagePercent { get; set; }
        public string CpuUsageText => $"{CpuUsagePercent:F1}%";

        public double DgpuUsagePercent { get; set; }
        public string DgpuUsageText => DgpuUsagePercent > 0 ? $"{DgpuUsagePercent:F1}%" : "0.0% (待機)";

        // Real NVML reading. Screen wattage used to be logged here and was a
        // formula over brightness, so it has been replaced rather than kept.
        public double DgpuPowerW { get; set; }
        public string DgpuPowerText => DgpuPowerW > 0 ? $"{DgpuPowerW:F1} W" : "-- W";

        public double BatteryVoltageV { get; set; }
        public string VoltageText => BatteryVoltageV > 0 ? $"{BatteryVoltageV:F2} V" : "-- V";

        public double BatteryCurrentA { get; set; }
        public string CurrentText => BatteryCurrentA > 0 ? $"{BatteryCurrentA:F2} A" : "-- A";

        // No temperature column: the value previously logged here was the
        // CPU/system thermal zone, not the battery's temperature.
        public string TelemetryText => $"{VoltageText} | {CurrentText}";

        public string SummaryText { get; set; } = string.Empty;
    }
}
