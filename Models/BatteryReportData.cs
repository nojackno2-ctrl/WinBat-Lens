using System;
using System.Collections.Generic;

namespace WinBatLens.Models
{
    public class SystemInfo
    {
        public string ComputerName { get; set; } = "Unknown PC";
        public string SystemProductName { get; set; } = "Windows PC";
        public string Bios { get; set; } = "N/A";
        public string OsBuild { get; set; } = "N/A";
        public string ReportTime { get; set; } = "N/A";
    }

    public class BatterySpecs
    {
        public string Name { get; set; } = "Primary Battery";
        public string Manufacturer { get; set; } = "Windows PC";
        public string SerialNumber { get; set; } = "N/A";
        public string Chemistry { get; set; } = "Li-ion";
        public int DesignCapacity { get; set; }
        public int FullChargeCapacity { get; set; }
        public int? CycleCount { get; set; }
        public string Unit { get; set; } = "mWh";
    }

    public class HealthMetrics
    {
        public bool HasBattery { get; set; } = true;
        public double HealthPercent { get; set; }
        public double WearPercent { get; set; }
        public int CapacityLoss { get; set; }
        public string StatusLabel { get; set; } = "良好";
        public string StatusClass { get; set; } = "Good";
        public string SummaryText { get; set; } = string.Empty;
    }

    public class CapacityHistoryItem
    {
        public string Period { get; set; } = string.Empty;
        public int FullChargeCapacity { get; set; }
        public int DesignCapacity { get; set; }
        public double HealthPercent => DesignCapacity > 0 
            ? Math.Min(100.0, Math.Round((double)FullChargeCapacity / DesignCapacity * 100.0, 1)) 
            : 0;
    }

    public class UsageHistoryItem
    {
        public string Period { get; set; } = string.Empty;
        public string BatteryDuration { get; set; } = string.Empty;
        public string AcDuration { get; set; } = string.Empty;
    }

    public class BatteryLifeEstimateItem
    {
        public string Period { get; set; } = string.Empty;
        public string FullChargeEstimate { get; set; } = string.Empty;
        public string DesignCapEstimate { get; set; } = string.Empty;
    }

    public class RecentUsageItem
    {
        public string StartTime { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string CapacityRemaining { get; set; } = string.Empty;
    }

    public class DiagnosticItem
    {
        public string Type { get; set; } = "info";
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

    public class RealTimePowerState
    {
        public bool IsAcOnline { get; set; }
        public bool IsCharging { get; set; }
        public string PowerStatusText { get; set; } = "讀取中...";
        public int BatteryPercent { get; set; }

        // The two real battery figures, both from the battery driver.
        // There is no system total and no AC adapter input: the components that
        // used to be summed had no power sensors, and Windows exposes no API
        // for adapter input at all.
        public double DischargeRateW { get; set; }
        public double ChargingRateW { get; set; }

        public string DischargeRateText { get; set; } = "-- W";
        public string ChargingStatusText { get; set; } = "讀取中...";
        public string EstimatedTimeRemainingText { get; set; } = "--";

        // Battery Physical Telemetry. The *Measured flags are false when the
        // hardware/firmware does not expose the value, so the UI can show "--"
        // instead of presenting a fallback constant as a real reading.
        //
        // Temperature is deliberately absent: the only thermal zone Windows
        // exposes here is the CPU/system zone, which was previously displayed
        // as if it were the battery's own temperature.
        public double BatteryVoltageV { get; set; }
        public bool IsVoltageMeasured { get; set; }
        public double BatteryCurrentA { get; set; }
        public string BatteryTelemetryText { get; set; } = "-- V | -- A";
        public string PowerPlanName { get; set; } = "平衡 (Balanced)";

        // Read from the battery driver itself (IOCTL_BATTERY_QUERY_STATUS).
        // When IsDischargeRateMeasured is true, DischargeRateW is the whole
        // machine's real power draw measured at the pack.
        public bool IsDischargeRateMeasured { get; set; }
        public bool IsChargeRateMeasured { get; set; }

        // NOTE ON WATTAGE
        // Only three power figures exist in this model, and every one of them
        // comes from real hardware: battery discharge and charge (battery
        // driver IOCTL) and discrete GPU package power (NVML).
        //
        // Per-component wattage for CPU, iGPU, RAM, screen, disk, Wi-Fi and
        // chipset — plus the system total and AC adapter input derived from
        // them — used to be computed from linear formulas over utilisation and
        // have been removed outright rather than shown as estimates. None of
        // them are obtainable on this hardware: AMD SMU (CPU) is held
        // exclusively by the OEM utility, no consumer iGPU exposes package
        // power, and Windows has no API at all for adapter input wattage.
        // Utilisation, throughput and brightness below are all really measured.

        // CPU
        public double CpuUsagePercent { get; set; }

        // RAM
        public double RamUsageGB { get; set; }
        public double TotalRamGB { get; set; }
        public double RamUsagePercent { get; set; }

        // Integrated GPU (iGPU)
        public string IgpuName { get; set; } = "內建顯示晶片 (iGPU)";
        public double IgpuUsagePercent { get; set; }

        // Discrete GPU (dGPU). Power is real, read over NVML.
        public bool HasDiscreteGpu { get; set; }
        public string DgpuName { get; set; } = "獨立顯示卡 (dGPU)";
        public double DgpuUsagePercent { get; set; }
        public double DgpuPowerW { get; set; }
        public bool IsDgpuPowerMeasured { get; set; }
        public string DgpuStatusText { get; set; } = "0% (待機省電)";

        // Screen / Display Backlight
        public int ScreenBrightnessPercent { get; set; } = 75;
        public bool IsBrightnessMeasured { get; set; }

        // Wi-Fi / Wireless Network
        public double WifiThroughputKbps { get; set; }

        // Legacy / Main GPU summary
        public double GpuUsagePercent { get; set; }
        public string GpuName { get; set; } = "顯示晶片";

        // Disk (SSD / HDD)
        public double DiskUsagePercent { get; set; }
        public double DiskReadWriteMbps { get; set; }
        public string DiskStatusText { get; set; } = "讀寫 0 MB/s";

        // Load rating is derived from utilisation only — no wattage involved.
        public string SystemPowerLoadStatus { get; set; } = "一般";
    }

    public class BatteryReportData
    {
        public SystemInfo SystemInfo { get; set; } = new SystemInfo();
        public BatterySpecs BatterySpecs { get; set; } = new BatterySpecs();
        public HealthMetrics HealthMetrics { get; set; } = new HealthMetrics();
        public List<CapacityHistoryItem> CapacityHistory { get; set; } = new List<CapacityHistoryItem>();
        public List<UsageHistoryItem> UsageHistory { get; set; } = new List<UsageHistoryItem>();
        public List<BatteryLifeEstimateItem> BatteryLifeEstimates { get; set; } = new List<BatteryLifeEstimateItem>();
        public List<RecentUsageItem> RecentUsage { get; set; } = new List<RecentUsageItem>();
        public List<DiagnosticItem> Diagnostics { get; set; } = new List<DiagnosticItem>();
        public DateTime LoadedAt { get; set; } = DateTime.Now;
    }
}
