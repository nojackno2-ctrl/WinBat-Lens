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

        // Persistent GPU Engine counters, keyed by instance name. GPU utilization
        // counters need two samples taken over an interval to produce a non-zero
        // value, so the same counter object must survive across update ticks
        // (the UI polls once per second). Recreating a counter every tick and
        // reading it once always yields 0 — the original bug behind dGPU 0W.
        private static readonly Dictionary<string, PerformanceCounter> _gpuEngineCounters
            = new Dictionary<string, PerformanceCounter>(StringComparer.OrdinalIgnoreCase);
        // Maps a GPU Engine LUID token (e.g. "luid_0x00000000_0x00010666") to
        // whether that adapter is the discrete GPU. Built once from DXGI.
        private static Dictionary<string, bool>? _luidIsDiscrete;

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

            // 1. CPU Usage & Power
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

            // 2. GPU (iGPU & dGPU) Usage & Power
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

            // 3. Disk (SSD / HDD) Usage & Power
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

            // 4. Screen Brightness & Display Power
            state.ScreenBrightnessPercent = GetScreenBrightnessPercent();
            state.ScreenPowerW = Math.Round(1.0 + (state.ScreenBrightnessPercent / 100.0) * 5.5, 1);

            // 5. Wi-Fi Wireless Adapter & Traffic Power
            state.WifiThroughputKbps = GetWifiThroughputKbps();
            state.WifiPowerW = Math.Round(0.6 + Math.Min(1.8, (state.WifiThroughputKbps / 5000.0) * 1.5), 1);

            // 6. Memory (RAM) Usage & Bus Power
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

            // 7. Motherboard Base Power
            state.MotherboardPowerW = 2.5;

            // Calculate Total System Hardware Power W
            state.TotalSystemHardwareW = Math.Round(
                state.CpuPowerW + state.IgpuPowerW + state.DgpuPowerW + 
                state.ScreenPowerW + state.DiskPowerW + state.WifiPowerW + 
                state.RamPowerW + state.MotherboardPowerW, 1);

            // 8. Get Windows System Power Status & Calculate AC Input W
            if (GetSystemPowerStatus(out var status))
            {
                state.IsAcOnline = status.ACLineStatus == 1;
                state.BatteryPercent = status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : 0;

                if (state.IsAcOnline)
                {
                    if (state.BatteryPercent >= 98)
                    {
                        state.IsCharging = false;
                        state.ChargingRateW = 0.0;
                        state.AcTotalInputW = state.TotalSystemHardwareW;
                        state.PowerStatusText = "市電供電中 (已充滿)";
                        state.ChargingStatusText = $"市電直供硬體 ({state.TotalSystemHardwareW:F1}W) | 電池滿電保護中 (0 W 充電)";
                        state.EstimatedTimeRemainingText = "電量已充滿 100%";
                        state.DischargeRateText = $"{state.AcTotalInputW:F1} W (AC 變壓器總供電)";
                    }
                    else
                    {
                        state.IsCharging = true;
                        
                        // Calculate AC charging wattage
                        double wmiChargeMw = GetWmiChargeRateMw();
                        if (wmiChargeMw > 0)
                        {
                            state.ChargingRateW = Math.Round(wmiChargeMw / 1000.0, 1);
                        }
                        else
                        {
                            if (state.BatteryPercent >= 90) state.ChargingRateW = 12.5;
                            else if (state.BatteryPercent >= 75) state.ChargingRateW = 28.0;
                            else state.ChargingRateW = 45.0;
                        }

                        // Total AC Input W = Charging W + Hardware W
                        state.AcTotalInputW = Math.Round(state.ChargingRateW + state.TotalSystemHardwareW, 1);
                        state.PowerStatusText = "AC 充電中";
                        state.DischargeRateText = $"{state.AcTotalInputW:F1} W (AC 變壓器總供電)";

                        if (state.BatteryPercent >= 90)
                        {
                            state.ChargingStatusText = $"AC 總輸出 {state.AcTotalInputW:F1}W (充電 +{state.ChargingRateW:F1}W + 硬體 {state.TotalSystemHardwareW:F1}W)";
                        }
                        else if (state.BatteryPercent >= 75)
                        {
                            state.ChargingStatusText = $"AC 總輸出 {state.AcTotalInputW:F1}W (充電 +{state.ChargingRateW:F1}W + 硬體 {state.TotalSystemHardwareW:F1}W)";
                        }
                        else
                        {
                            state.ChargingStatusText = $"⚡ AC 總輸出 {state.AcTotalInputW:F1}W (充電 +{state.ChargingRateW:F1}W + 硬體 {state.TotalSystemHardwareW:F1}W)";
                        }

                        // Estimate time to full charge
                        int remainPct = 100 - state.BatteryPercent;
                        double estHours = (remainPct / 100.0 * 56.0) / Math.Max(10.0, state.ChargingRateW);
                        TimeSpan chargeTime = TimeSpan.FromHours(estHours);
                        state.EstimatedTimeRemainingText = $"預估 {chargeTime.Hours}小時{chargeTime.Minutes}分 充飽";
                    }
                }
                else
                {
                    state.IsCharging = false;
                    state.ChargingRateW = 0.0;
                    state.AcTotalInputW = 0.0;
                    state.PowerStatusText = "電池放電中";
                    state.ChargingStatusText = "使用電池供電";
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

            // 9. Total Discharge Rate W for Battery mode
            if (!state.IsAcOnline)
            {
                double dischargeRateMw = GetWmiDischargeRateMw();
                if (dischargeRateMw > 0)
                {
                    state.DischargeRateW = Math.Round(dischargeRateMw / 1000.0, 1);
                    state.DischargeRateText = $"-{state.DischargeRateW:F1} W ({dischargeRateMw:N0} mW)";
                }
                else
                {
                    state.DischargeRateW = state.TotalSystemHardwareW;
                    state.DischargeRateText = $"-{state.DischargeRateW:F1} W (即時全硬體估算)";
                }
            }

            // 10. Overall System Power Load Rating
            if (state.CpuUsagePercent > 70.0 || state.DgpuUsagePercent > 40.0 || state.TotalSystemHardwareW > 25.0)
            {
                state.SystemPowerLoadStatus = "高負載 (高耗電)";
            }
            else if (state.CpuUsagePercent > 30.0 || state.IgpuUsagePercent > 30.0 || state.TotalSystemHardwareW > 15.0)
            {
                state.SystemPowerLoadStatus = "中度運算";
            }
            else
            {
                state.SystemPowerLoadStatus = "輕度省電";
            }

            return state;
        }

        private static double GetWmiChargeRateMw()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT ChargeRate FROM Win32_Battery"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        var rateObj = obj["ChargeRate"];
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

        // Maps each GPU's LUID token to its per-engine-type utilization sums for
        // the current tick. Task Manager reports a GPU's headline utilization as
        // the busiest single engine, so we sum instances within an engine type
        // and then take the max engine type per adapter.
        private static (double Igpu, double Dgpu) GetDualGpuUsage()
        {
            EnsureLuidMap();

            // luid -> (engtype -> summed utilization)
            var perAdapter = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var category = new PerformanceCounterCategory("GPU Engine");
                var currentInstances = new HashSet<string>(category.GetInstanceNames(), StringComparer.OrdinalIgnoreCase);

                // Drop counters for instances that no longer exist (e.g. a process
                // that closed) so the dictionary does not grow without bound.
                var stale = new List<string>();
                foreach (var key in _gpuEngineCounters.Keys)
                {
                    if (!currentInstances.Contains(key)) stale.Add(key);
                }
                foreach (var key in stale)
                {
                    try { _gpuEngineCounters[key].Dispose(); } catch { }
                    _gpuEngineCounters.Remove(key);
                }

                foreach (var inst in currentInstances)
                {
                    string lower = inst.ToLowerInvariant();
                    string luid = ExtractToken(lower, "luid_", "_phys_");
                    string engType = ExtractToken(lower, "engtype_", null);
                    if (luid.Length == 0 || engType.Length == 0) continue;
                    luid = "luid_" + luid;

                    double val;
                    try
                    {
                        if (!_gpuEngineCounters.TryGetValue(inst, out var counter))
                        {
                            // New instance: create and prime it. The first read has
                            // no baseline and returns 0; it becomes accurate next tick.
                            counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, true);
                            _gpuEngineCounters[inst] = counter;
                            counter.NextValue();
                            val = 0.0;
                        }
                        else
                        {
                            val = counter.NextValue();
                        }
                    }
                    catch
                    {
                        continue;
                    }

                    if (val <= 0) continue;

                    if (!perAdapter.TryGetValue(luid, out var engMap))
                    {
                        engMap = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                        perAdapter[luid] = engMap;
                    }
                    engMap.TryGetValue(engType, out double running);
                    engMap[engType] = running + val;
                }
            }
            catch { }

            double iGpuMax = 0.0;
            double dGpuMax = 0.0;

            foreach (var kv in perAdapter)
            {
                // Busiest single engine on this adapter.
                double adapterUtil = 0.0;
                foreach (var eng in kv.Value.Values)
                {
                    if (eng > adapterUtil) adapterUtil = eng;
                }
                adapterUtil = Math.Min(100.0, adapterUtil);

                bool isDiscrete = _luidIsDiscrete != null &&
                                  _luidIsDiscrete.TryGetValue(kv.Key, out bool disc) && disc;

                if (isDiscrete)
                {
                    if (adapterUtil > dGpuMax) dGpuMax = adapterUtil;
                }
                else
                {
                    // Unknown LUIDs (not in the DXGI map, e.g. software adapters)
                    // fall through here and count toward the integrated bucket.
                    if (adapterUtil > iGpuMax) iGpuMax = adapterUtil;
                }
            }

            return (Math.Round(iGpuMax, 1), Math.Round(dGpuMax, 1));
        }

        private static void EnsureLuidMap()
        {
            if (_luidIsDiscrete != null) return;

            var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var adapters = DxgiAdapterService.GetAdapters();

                // DXGI returned nothing — enumeration failed this tick (rare, but
                // possible under session-0 / remote contexts). Do NOT cache the
                // empty result: caching it here would permanently strand the
                // discrete GPU at 0% and lump all GPU load into the iGPU bucket
                // for the rest of the session. Leave the map null so the next
                // 1-second tick retries.
                if (adapters.Count == 0) return;

                foreach (var adapter in adapters)
                {
                    if (adapter.IsSoftware) continue;
                    if (string.IsNullOrEmpty(adapter.LuidKey)) continue;
                    map[adapter.LuidKey] = adapter.IsDiscrete;
                }
            }
            catch
            {
                // Unexpected failure — retry next tick rather than caching a blank map.
                return;
            }

            _luidIsDiscrete = map;
        }

        // Extracts the text between <start> and <end> markers. If <end> is null,
        // reads to the end of the string. Returns "" if not found.
        private static string ExtractToken(string source, string start, string? end)
        {
            int s = source.IndexOf(start, StringComparison.Ordinal);
            if (s < 0) return string.Empty;
            s += start.Length;
            if (end == null) return source.Substring(s);
            int e = source.IndexOf(end, s, StringComparison.Ordinal);
            if (e < 0) return string.Empty;
            return source.Substring(s, e - s);
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
