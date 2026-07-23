using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
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
        private static List<GpuInfo> _cachedGpus = new List<GpuInfo>();
        private static long _lastNetBytes = 0;
        private static DateTime _lastNetTime = DateTime.MinValue;

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

            try
            {
                _cachedGpus = GpuInfoService.GetInstalledGpus();
            }
            catch { }
        }

        public static RealTimePowerState GetCurrentPowerState()
        {
            var state = new RealTimePowerState();

            // Set GPU Names
            var dGpu = _cachedGpus.FirstOrDefault(g => g.IsDiscrete);
            var iGpu = _cachedGpus.FirstOrDefault(g => !g.IsDiscrete);

            if (dGpu != null)
            {
                state.HasDiscreteGpu = true;
                state.DgpuName = dGpu.Name;
                state.IgpuName = iGpu?.Name ?? "內建顯示晶片 (iGPU)";
            }
            else
            {
                state.HasDiscreteGpu = false;
                state.IgpuName = _cachedGpus.FirstOrDefault()?.Name ?? "顯示晶片 (GPU)";
                state.DgpuName = "無獨立顯示卡";
            }

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

            // 3. GPU (iGPU & dGPU) Usage & Power
            var (iGpuVal, dGpuVal) = GetDualGpuUsage();
            state.IgpuUsagePercent = iGpuVal;
            state.IgpuPowerW = Math.Round(1.0 + (iGpuVal / 100.0) * 12.0, 1);

            if (state.HasDiscreteGpu)
            {
                state.DgpuUsagePercent = dGpuVal;
                if (dGpuVal > 0)
                {
                    state.DgpuPowerW = Math.Round(3.0 + (dGpuVal / 100.0) * 35.0, 1);
                    state.DgpuStatusText = $"{dGpuVal:F1}% (高效能運算中)";
                }
                else
                {
                    state.DgpuPowerW = 0.0;
                    state.DgpuStatusText = "0.0% (待機省電)";
                }
            }
            else
            {
                state.DgpuUsagePercent = 0;
                state.DgpuPowerW = 0;
                state.DgpuStatusText = "無獨立顯示卡";
            }

            // Legacy total GPU
            state.GpuUsagePercent = Math.Max(iGpuVal, dGpuVal);
            state.GpuPowerW = Math.Round(state.IgpuPowerW + state.DgpuPowerW, 1);
            state.GpuName = state.HasDiscreteGpu ? state.DgpuName : state.IgpuName;

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

            // 5. Screen Brightness & Display Power
            state.ScreenBrightnessPercent = GetScreenBrightnessPercent();
            state.ScreenPowerW = Math.Round(1.0 + (state.ScreenBrightnessPercent / 100.0) * 5.5, 1);

            // 6. Wi-Fi Wireless Adapter & Traffic Power
            state.WifiThroughputKbps = GetWifiThroughputKbps();
            state.WifiPowerW = Math.Round(0.6 + Math.Min(1.8, (state.WifiThroughputKbps / 5000.0) * 1.5), 1);

            // 7. Memory (RAM) Usage & Bus Power
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
            state.RamPowerW = Math.Round(0.8 + (state.RamUsagePercent / 100.0) * 1.7, 1);

            // 8. Motherboard Base Power
            state.MotherboardPowerW = 2.5;

            // 9. Total Discharge Rate W
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
                double estW = state.CpuPowerW + state.IgpuPowerW + state.DgpuPowerW + 
                             state.ScreenPowerW + state.DiskPowerW + state.WifiPowerW + 
                             state.RamPowerW + state.MotherboardPowerW;
                state.DischargeRateW = Math.Round(estW, 1);
                state.DischargeRateText = $"~{state.DischargeRateW:F1} W (即時全硬體估算)";
            }

            // 10. Overall System Power Load Rating
            if (state.CpuUsagePercent > 70.0 || state.DgpuUsagePercent > 40.0 || state.DischargeRateW > 25.0)
            {
                state.SystemPowerLoadStatus = "高負載 (高耗電)";
            }
            else if (state.CpuUsagePercent > 30.0 || state.IgpuUsagePercent > 30.0 || state.DischargeRateW > 15.0)
            {
                state.SystemPowerLoadStatus = "中度運算";
            }
            else
            {
                state.SystemPowerLoadStatus = "輕度省電";
            }

            return state;
        }

        private static int GetScreenBrightnessPercent()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        var bObj = obj["CurrentBrightness"];
                        if (bObj != null) return Convert.ToInt32(bObj);
                    }
                }
            }
            catch { }

            return 75; // Default estimate
        }

        private static double GetWifiThroughputKbps()
        {
            try
            {
                long currentBytes = 0;
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus == OperationalStatus.Up && 
                       (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 || ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet))
                    {
                        var stats = ni.GetIPStatistics();
                        currentBytes += stats.BytesReceived + stats.BytesSent;
                    }
                }

                DateTime now = DateTime.Now;
                double speedKbps = 0;
                if (_lastNetTime != DateTime.MinValue && _lastNetBytes > 0 && now > _lastNetTime)
                {
                    double seconds = (now - _lastNetTime).TotalSeconds;
                    if (seconds > 0)
                    {
                        long diffBytes = currentBytes - _lastNetBytes;
                        if (diffBytes > 0)
                        {
                            speedKbps = Math.Round((diffBytes / 1024.0) / seconds, 1);
                        }
                    }
                }

                _lastNetBytes = currentBytes;
                _lastNetTime = now;
                return speedKbps;
            }
            catch { }

            return 120.0;
        }

        private static (double Igpu, double Dgpu) GetDualGpuUsage()
        {
            double iGpuMax = 0.0;
            double dGpuMax = 0.0;

            try
            {
                var category = new PerformanceCounterCategory("GPU Engine");
                var instanceNames = category.GetInstanceNames();

                foreach (var inst in instanceNames)
                {
                    string lower = inst.ToLower();
                    if (lower.Contains("engtype_3d"))
                    {
                        using (var pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst))
                        {
                            double val = pc.NextValue();
                            if (lower.Contains("phys_1"))
                            {
                                if (val > dGpuMax) dGpuMax = val;
                            }
                            else
                            {
                                if (val > iGpuMax) iGpuMax = val;
                            }
                        }
                    }
                }
            }
            catch { }

            return (Math.Min(100.0, Math.Round(iGpuMax, 1)), Math.Min(100.0, Math.Round(dGpuMax, 1)));
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
