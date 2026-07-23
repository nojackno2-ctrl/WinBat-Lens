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
        public string PowerStatusText { get; set; } = "讀取中...";
        public int BatteryPercent { get; set; }
        public double DischargeRateW { get; set; }
        public string DischargeRateText { get; set; } = "-- W";
        public string EstimatedTimeRemainingText { get; set; } = "--";
        public double CpuUsagePercent { get; set; }
        public double RamUsageGB { get; set; }
        public double TotalRamGB { get; set; }
        public double RamUsagePercent { get; set; }
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
