using System;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using WinBatLens.Models;

namespace WinBatLens.Services
{
    public class RealTimePowerService
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

        private static PerformanceCounter? _cpuCounter;
        private static PerformanceCounter? _diskTimeCounter;
        private static PerformanceCounter? _diskBytesCounter;
        private static string _gpuNameCache = "顯示晶片";

        static RealTimePowerService()
        {
            try
            {
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
                _cpuCounter.NextValue();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CPU PerformanceCounter init warning: {ex.Message}");
            }

            try
            {
                _diskTimeCounter = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total", true);
                _diskTimeCounter.NextValue();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DiskTime PerformanceCounter init warning: {ex.Message}");
            }

            try
            {
                _diskBytesCounter = new PerformanceCounter("PhysicalDisk", "Disk Bytes/sec", "_Total", true);
                _diskBytesCounter.NextValue();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DiskBytes PerformanceCounter init warning: {ex.Message}");
            }

            _gpuNameCache = FetchGpuName();
        }

        public static RealTimePowerState GetCurrentPowerState()
        {
            var state = new RealTimePowerState();
            state.GpuName = _gpuNameCache;

            // 1. Get Windows System Power Status
            if (GetSystemPowerStatus(out var status))
            {
                state.IsAcOnline = status.ACLineStatus == 1;
                state.BatteryPercent = status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : 0;

                if (state.IsAcOnline)
                {
                    state.PowerStatusText = state.BatteryPercent >= 98 ? "市電供電中 (已充滿)" : "市電供電中 (充電中)";
                    state.EstimatedTimeRemainingText = "連接 AC 電源中";
                }
                else
                {
                    state.PowerStatusText = "電池放電中";
                    if (status.BatteryLifeTime > 0 && status.BatteryLifeTime < 86400)
                    {
                        TimeSpan t = TimeSpan.FromSeconds(status.BatteryLifeTime);
                        state.EstimatedTimeRemainingText = $"{t.Hours} 小時 {t.Minutes} 分鐘";
                    }
                    else
                    {
                        state.EstimatedTimeRemainingText = "估算中...";
                    }
                }
            }

            // 2. CPU Usage & Power
            try
            {
                if (_cpuCounter != null)
                {
                    state.CpuUsagePercent = Math.Round(_cpuCounter.NextValue(), 1);
                }
                else
                {
                    state.CpuUsagePercent = 12.5;
                }
            }
            catch
            {
                state.CpuUsagePercent = 12.5;
            }
            state.CpuPowerW = Math.Round(2.5 + (state.CpuUsagePercent / 100.0) * 22.5, 1);

            // 3. GPU Usage & Power
            state.GpuUsagePercent = GetGpuUsagePercent();
            state.GpuPowerW = Math.Round(1.5 + (state.GpuUsagePercent / 100.0) * 18.5, 1);

            // 4. Disk (SSD / HDD) Usage & Power
            try
            {
                if (_diskTimeCounter != null)
                {
                    double dTime = _diskTimeCounter.NextValue();
                    state.DiskUsagePercent = Math.Min(100.0, Math.Round(dTime, 1));
                }

                if (_diskBytesCounter != null)
                {
                    double bytesPerSec = _diskBytesCounter.NextValue();
                    double mbps = Math.Round(bytesPerSec / (1024.0 * 1024.0), 1);
                    state.DiskReadWriteMbps = mbps;
                    state.DiskStatusText = $"即時吞吐量: {mbps:F1} MB/s";
                }
            }
            catch
            {
                state.DiskUsagePercent = 2.0;
                state.DiskReadWriteMbps = 0.5;
                state.DiskStatusText = "即時吞吐量: 0.5 MB/s";
            }
            state.DiskPowerW = Math.Round(0.4 + (state.DiskUsagePercent / 100.0) * 3.2, 1);

            // 5. Total Discharge Rate W
            double dischargeRateMw = GetWmiDischargeRateMw();
            if (dischargeRateMw > 0)
            {
                state.DischargeRateW = Math.Round(dischargeRateMw / 1000.0, 1);
                state.DischargeRateText = $"{state.DischargeRateW:F1} W ({dischargeRateMw:N0} mW)";
            }
            else if (state.IsAcOnline)
            {
                state.DischargeRateW = 0;
                state.DischargeRateText = "AC 供電中 (0 W 放電)";
            }
            else
            {
                // Sum estimated component powers if WMI discharge rate unavailable
                double estW = state.CpuPowerW + state.GpuPowerW + state.DiskPowerW + 3.0; // +3W display/board
                state.DischargeRateW = Math.Round(estW, 1);
                state.DischargeRateText = $"~{state.DischargeRateW:F1} W (即時估算總瓦數)";
            }

            // 6. Memory (RAM) Usage
            try
            {
                var ramInfo = GetSystemRamInfo();
                state.RamUsageGB = ramInfo.UsedGb;
                state.TotalRamGB = ramInfo.TotalGb;
                state.RamUsagePercent = Math.Round((ramInfo.UsedGb / ramInfo.TotalGb) * 100.0, 1);
            }
            catch
            {
                state.TotalRamGB = 16.0;
                state.RamUsageGB = 8.0;
                state.RamUsagePercent = 50.0;
            }

            // 7. Overall System Power Load Rating
            if (state.CpuUsagePercent > 70.0 || state.GpuUsagePercent > 60.0 || state.DischargeRateW > 25.0)
            {
                state.SystemPowerLoadStatus = "高負載 (高耗電)";
            }
            else if (state.CpuUsagePercent > 30.0 || state.GpuUsagePercent > 20.0 || state.DischargeRateW > 15.0)
            {
                state.SystemPowerLoadStatus = "中度運算";
            }
            else
            {
                state.SystemPowerLoadStatus = "輕度省電";
            }

            return state;
        }

        private static double GetGpuUsagePercent()
        {
            try
            {
                var category = new PerformanceCounterCategory("GPU Engine");
                var instanceNames = category.GetInstanceNames();
                double maxVal = 0.0;

                foreach (var inst in instanceNames)
                {
                    if (inst.ToLower().Contains("engtype_3d"))
                    {
                        using (var pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst))
                        {
                            double val = pc.NextValue();
                            if (val > maxVal) maxVal = val;
                        }
                    }
                }

                if (maxVal > 0) return Math.Min(100.0, Math.Round(maxVal, 1));
            }
            catch { }

            return 0.0;
        }

        private static string FetchGpuName()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string name = obj["Name"]?.ToString() ?? "";
                        if (!string.IsNullOrWhiteSpace(name)) return name;
                    }
                }
            }
            catch { }

            return "顯示晶片 (GPU)";
        }

        private static double GetWmiDischargeRateMw()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT DischargeRate, BatteryStatus FROM Win32_Battery"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        var rateObj = obj["DischargeRate"];
                        if (rateObj != null)
                        {
                            double val = Convert.ToDouble(rateObj);
                            if (val > 0) return val;
                        }
                    }
                }
            }
            catch { }
            return 0;
        }

        private static (double UsedGb, double TotalGb) GetSystemRamInfo()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT FreePhysicalMemory, TotalVisibleMemorySize FROM Win32_OperatingSystem"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        double totalKb = Convert.ToDouble(obj["TotalVisibleMemorySize"]);
                        double freeKb = Convert.ToDouble(obj["FreePhysicalMemory"]);
                        double usedKb = totalKb - freeKb;

                        double totalGb = Math.Round(totalKb / (1024 * 1024), 1);
                        double usedGb = Math.Round(usedKb / (1024 * 1024), 1);
                        return (usedGb, totalGb);
                    }
                }
            }
            catch { }

            return (8.0, 16.0);
        }
    }
}
