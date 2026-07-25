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

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("PowrProf.dll", CharSet = CharSet.Unicode)]
        private static extern uint PowerGetActiveScheme(IntPtr UserPowerKey, out IntPtr ActivePolicyGuid);

        [DllImport("PowrProf.dll", CharSet = CharSet.Unicode)]
        private static extern uint PowerReadFriendlyName(
            IntPtr RootPowerKey,
            ref Guid SchemeGuid,
            IntPtr SubGroupOfPowerSettingsGuid,
            IntPtr PowerSettingGuid,
            IntPtr Buffer,
            ref uint BufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr hMem);

        private static PerformanceCounter? _cpuCounter;
        private static PerformanceCounter? _diskTimeCounter;
        private static PerformanceCounter? _diskBytesCounter;
        private static List<GpuInfo> _cachedGpus = new List<GpuInfo>();
        private static long _lastNetBytes = 0;
        private static long _lastNetSampleTick = 0;
        private static double _cachedWifiKbps = 0;

        // WMI (System.Management) queries are memory-heavy and allocate COM
        // objects on every call. The UI ticks once per second, but brightness
        // and battery charge/discharge rates change slowly, so these values are
        // cached and only refreshed from WMI every few seconds.
        // Throttling uses Environment.TickCount64 (a single cheap read, immune
        // to wall-clock changes) instead of DateTime.Now.
        private const long WmiRefreshMs = 4000;
        private static int _cachedBrightness = 75;
        private static bool _brightnessMeasured;
        private static long _lastBrightnessTick;
        private static double _cachedVoltageV = 0;
        private static bool _voltageMeasured;
        private static long _lastTelemetryTick;
        private static string _cachedPowerPlan = "平衡 (Balanced)";
        private static long _lastPowerPlanTick;

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

        /// <summary>
        /// GPU list discovered once at startup (WMI). Exposed so the UI can
        /// reuse it instead of issuing a second Win32_VideoController query.
        /// </summary>
        public static IReadOnlyList<GpuInfo> InstalledGpus => _cachedGpus;

        /// <summary>
        /// Warms up everything the first monitoring tick would otherwise pay
        /// for on the UI thread: the static constructor (PerformanceCounter and
        /// GPU WMI enumeration), the GPU Engine counter category, and the slow
        /// WMI caches. Intended to be called once from a background thread
        /// before the 1-second timer starts.
        /// </summary>
        public static void Initialize()
        {
            try
            {
                GetDualGpuUsage();
                GetScreenBrightnessPercent();
                // Opening the sensor stack can load a kernel driver, so it must
                // happen here on the warmup thread, never on the first UI tick.
                HardwareSensorService.Initialize();
                BatteryTelemetryService.Initialize();
                GetWmiBatteryVoltage();
                GetActivePowerPlanName();
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

            // 1. CPU Usage & Power. When the counter is unavailable report 0
            // rather than a fabricated value.
            try
            {
                if (_cpuCounter != null)
                {
                    state.CpuUsagePercent = Math.Round(_cpuCounter.NextValue(), 1);
                }
            }
            catch
            {
                state.CpuUsagePercent = 0.0;
            }
            // CPU package power is deliberately not reported. It is unreadable
            // here (the OEM utility owns the AMD SMU mailbox) and the linear
            // estimate that used to stand in for it has been removed.

            // 2. GPU (iGPU & dGPU) usage. Only dGPU package power is real.
            var (iGpuVal, dGpuVal) = GetDualGpuUsage();
            state.IgpuUsagePercent = iGpuVal;

            if (state.HasDiscreteGpu)
            {
                state.DgpuUsagePercent = dGpuVal;

                // A real GPU draws real watts even at 0% utilisation (~19 W idle
                // measured on an RTX 3060 Laptop), so the sensor value is used
                // whatever the load reads.
                double? dGpuMeasuredW = HardwareSensorService.DgpuPackageW;
                state.IsDgpuPowerMeasured = dGpuMeasuredW.HasValue;
                state.DgpuPowerW = dGpuMeasuredW ?? 0.0;

                state.DgpuStatusText = dGpuVal > 0
                    ? $"{dGpuVal:F1}% (高效能運算中)"
                    : $"{dGpuVal:F1}% (待機)";
            }
            else
            {
                state.DgpuUsagePercent = 0;
                state.DgpuPowerW = 0;
                state.IsDgpuPowerMeasured = false;
                state.DgpuStatusText = "無獨立顯示卡";
            }

            // Legacy total GPU
            state.GpuUsagePercent = Math.Max(iGpuVal, dGpuVal);
            state.GpuName = state.HasDiscreteGpu ? state.DgpuName : state.IgpuName;

            // 3. Disk (SSD / HDD) usage and throughput
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
                state.DiskUsagePercent = 0.0;
                state.DiskReadWriteMbps = 0.0;
                state.DiskStatusText = "即時吞吐量: --";
            }

            // 4. Screen brightness
            state.ScreenBrightnessPercent = GetScreenBrightnessPercent();
            state.IsBrightnessMeasured = _brightnessMeasured;

            // 5. Wi-Fi throughput
            state.WifiThroughputKbps = GetWifiThroughputKbps();

            // 6. Memory (RAM) usage
            try
            {
                var ramInfo = GetSystemRamInfo();
                state.RamUsageGB = ramInfo.UsedGb;
                state.TotalRamGB = ramInfo.TotalGb;
                state.RamUsagePercent = Math.Round((ramInfo.UsedGb / ramInfo.TotalGb) * 100.0, 1);
            }
            catch
            {
                state.TotalRamGB = 0.0;
                state.RamUsageGB = 0.0;
                state.RamUsagePercent = 0.0;
            }

            // There is no system-total wattage. Screen, disk, Wi-Fi, RAM and
            // chipset power were all linear guesses over utilisation and are
            // gone; summing them produced an equally invented total. On battery
            // the pack reports the real whole-system figure (section 9), which
            // is strictly better than any sum of estimates could be.

            // 8. Windows power status + the battery's own charge/discharge rate.
            bool haveBatt = BatteryTelemetryService.TryRead(out var batt);

            if (GetSystemPowerStatus(out var status))
            {
                state.IsAcOnline = status.ACLineStatus == 1;
                state.BatteryPercent = status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : 0;

                // The driver's own flag decides whether current is flowing into
                // the pack. Inferring it from a percentage threshold is wrong on
                // any laptop with a charge limit — this ASUS stops at ~95% by
                // design, which the old `>= 98` test reported as still charging.
                state.IsCharging = haveBatt
                    ? batt.IsCharging
                    : state.IsAcOnline && state.BatteryPercent < 98;

                if (state.IsAcOnline)
                {
                    if (state.IsCharging)
                    {
                        if (haveBatt && batt.IsRateKnown && batt.ChargeW > 0)
                        {
                            state.ChargingRateW = batt.ChargeW;
                            state.IsChargeRateMeasured = true;
                        }
                        else
                        {
                            // No invented constant here: an unknown charge rate
                            // is reported as unknown, not as a plausible number.
                            state.ChargingRateW = 0.0;
                            state.IsChargeRateMeasured = false;
                        }

                        state.PowerStatusText = "AC 充電中";

                        // Only the charge rate is real. AC adapter input cannot
                        // be measured at all on Windows, so no total is shown.
                        if (state.IsChargeRateMeasured)
                        {
                            state.DischargeRateText = $"+{state.ChargingRateW:F1} W (電池充電實測)";
                            state.ChargingStatusText = $"電池充電中 +{state.ChargingRateW:F1}W (電池實測)";
                        }
                        else
                        {
                            state.DischargeRateText = "-- W";
                            state.ChargingStatusText = "電池充電中 (充電功率無法讀取)";
                        }

                        if (state.IsChargeRateMeasured && state.ChargingRateW > 0)
                        {
                            double remainWh = (100 - state.BatteryPercent) / 100.0 * 56.0;
                            TimeSpan chargeTime = TimeSpan.FromHours(remainWh / state.ChargingRateW);
                            state.EstimatedTimeRemainingText = $"預估 {chargeTime.Hours}小時{chargeTime.Minutes}分 充飽";
                        }
                        else
                        {
                            state.EstimatedTimeRemainingText = "充電中...";
                        }
                    }
                    else
                    {
                        // On AC with no current flowing there is nothing real to
                        // measure: the pack reports 0 and adapter input is not
                        // exposed by Windows. Report that honestly.
                        state.ChargingRateW = 0.0;
                        state.PowerStatusText = "市電供電中 (未充電)";
                        state.ChargingStatusText = "市電直供 | 電池未充放電 (無可量測功率)";
                        state.EstimatedTimeRemainingText = state.BatteryPercent >= 98
                            ? "電量已充滿 100%"
                            : $"電量 {state.BatteryPercent}% (電池保養未充電)";
                        state.DischargeRateText = "-- W";
                    }
                }
                else
                {
                    state.IsCharging = false;
                    state.ChargingRateW = 0.0;
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

            // 9. Discharge rate. On battery this is the single most valuable
            // number the app has: it is the whole machine's real power draw,
            // measured at the pack, with no per-component estimation involved.
            if (!state.IsAcOnline)
            {
                if (haveBatt && batt.IsRateKnown && batt.DischargeW > 0)
                {
                    state.DischargeRateW = batt.DischargeW;
                    state.IsDischargeRateMeasured = true;
                    state.DischargeRateText = $"-{state.DischargeRateW:F1} W (電池實測)";
                }
                else
                {
                    // No substitute figure: if the pack will not report a rate
                    // there is no honest number to show.
                    state.DischargeRateW = 0.0;
                    state.IsDischargeRateMeasured = false;
                    state.DischargeRateText = "-- W";
                }
            }

            // 10. Load rating, from utilisation only — no wattage involved.
            if (state.CpuUsagePercent > 70.0 || state.DgpuUsagePercent > 40.0)
            {
                state.SystemPowerLoadStatus = "高負載 (高耗電)";
            }
            else if (state.CpuUsagePercent > 30.0 || state.IgpuUsagePercent > 30.0)
            {
                state.SystemPowerLoadStatus = "中度運算";
            }
            else
            {
                state.SystemPowerLoadStatus = "輕度省電";
            }

            // 11. Battery Physical Telemetry (Voltage, Current, Temperature)
            double volts = GetWmiBatteryVoltage();
            state.BatteryVoltageV = volts;
            state.IsVoltageMeasured = _voltageMeasured;

            double activePowerW = state.IsAcOnline ? state.ChargingRateW : state.DischargeRateW;
            if (volts > 0 && activePowerW > 0)
            {
                state.BatteryCurrentA = Math.Round(activePowerW / volts, 2);
            }
            else
            {
                state.BatteryCurrentA = 0.0;
            }

            string voltText = state.IsVoltageMeasured ? $"{state.BatteryVoltageV:F2} V" : "-- V";
            string currText = state.BatteryCurrentA > 0 ? $"{state.BatteryCurrentA:F2} A" : "-- A";
            state.BatteryTelemetryText = $"{voltText} | {currText}";

            // 12. Windows Active Power Plan (PowrProf.dll)
            state.PowerPlanName = GetActivePowerPlanName();

            return state;
        }

        // Charge/discharge rates come from BatteryTelemetryService via
        // IOCTL_BATTERY_QUERY_STATUS. The Win32_Battery ChargeRate and
        // DischargeRate properties formerly read here are blank on a great many
        // laptops (this one included), which is why the app fell back to a
        // utilisation estimate while the pack was reporting a real figure.

        private static int GetScreenBrightnessPercent()
        {
            if (Environment.TickCount64 - _lastBrightnessTick < WmiRefreshMs)
                return _cachedBrightness;
            _lastBrightnessTick = Environment.TickCount64;

            int measured = QueryScreenBrightnessPercent();
            _brightnessMeasured = measured >= 0;
            // Desktop monitors and many external panels do not implement
            // WmiMonitorBrightness; fall back to 75 for the power estimate but
            // flag it so the UI does not present it as a real reading.
            _cachedBrightness = _brightnessMeasured ? measured : 75;
            return _cachedBrightness;
        }

        // Returns -1 when brightness cannot be read.
        private static int QueryScreenBrightnessPercent()
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

            return -1;
        }

        // Enumerating every network interface and reading its IP statistics is
        // the heaviest remaining per-tick call, so it is throttled like the WMI
        // queries. The reported value is the average over the sampling window,
        // which also smooths out the second-to-second spikes.
        private static double GetWifiThroughputKbps()
        {
            long nowTick = Environment.TickCount64;
            if (_lastNetSampleTick != 0 && nowTick - _lastNetSampleTick < WmiRefreshMs)
                return _cachedWifiKbps;

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

                double speedKbps = 0;
                if (_lastNetSampleTick != 0 && _lastNetBytes > 0)
                {
                    double seconds = (nowTick - _lastNetSampleTick) / 1000.0;
                    long diffBytes = currentBytes - _lastNetBytes;
                    if (seconds > 0 && diffBytes > 0)
                    {
                        speedKbps = Math.Round((diffBytes / 1024.0) / seconds, 1);
                    }
                }

                _lastNetBytes = currentBytes;
                _lastNetSampleTick = nowTick;
                _cachedWifiKbps = speedKbps;
            }
            catch
            {
                _lastNetSampleTick = nowTick;
                _cachedWifiKbps = 0.0;
            }

            return _cachedWifiKbps;
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

        private static (double UsedGb, double TotalGb) GetSystemRamInfo()
        {
            // Native GlobalMemoryStatusEx avoids the heavy per-tick WMI/COM
            // allocations of a Win32_OperatingSystem query. The UI polls this
            // once per second, so keeping it allocation-free matters.
            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref mem) && mem.ullTotalPhys > 0)
            {
                const double gib = 1024.0 * 1024.0 * 1024.0;
                double totalGb = Math.Round(mem.ullTotalPhys / gib, 1);
                double usedGb = Math.Round((mem.ullTotalPhys - mem.ullAvailPhys) / gib, 1);
                return (usedGb, totalGb);
            }

            return (8.0, 16.0);
        }

        /// <summary>
        /// Reads live battery pack voltage. Temperature is deliberately not read
        /// here: the only zone Windows exposes on a typical laptop is the
        /// CPU/system thermal zone, which this method used to return and the UI
        /// used to present as the battery's own temperature.
        /// </summary>
        private static double GetWmiBatteryVoltage()
        {
            if (Environment.TickCount64 - _lastTelemetryTick < WmiRefreshMs)
                return _cachedVoltageV;
            _lastTelemetryTick = Environment.TickCount64;

            // The battery IOCTL returns voltage in the same call the rate comes
            // from, so prefer it; the sensor stack is the next best source.
            double voltageV = 0;
            if (BatteryTelemetryService.TryRead(out var bt) && bt.VoltageV > 0)
                voltageV = bt.VoltageV;
            if (voltageV <= 0)
                voltageV = HardwareSensorService.BatteryVoltageV ?? 0;

            if (voltageV <= 0)
            {
                try
                {
                    using (var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT Voltage FROM BatteryStatus"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            var vObj = obj["Voltage"];
                            if (vObj != null)
                            {
                                double mv = Convert.ToDouble(vObj);
                                if (mv > 0)
                                {
                                    voltageV = Math.Round(mv / 1000.0, 2);
                                    break;
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            // Many laptops' embedded controllers do not expose live voltage.
            // Keep a nominal figure so the I = P / V maths stays sane, but
            // record that it was not measured so the UI shows "--".
            _voltageMeasured = voltageV > 0;
            _cachedVoltageV = _voltageMeasured ? voltageV : 15.4;

            return _cachedVoltageV;
        }

        private static string GetActivePowerPlanName()
        {
            if (Environment.TickCount64 - _lastPowerPlanTick < WmiRefreshMs)
                return _cachedPowerPlan;
            _lastPowerPlanTick = Environment.TickCount64;

            try
            {
                if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr pGuid) == 0 && pGuid != IntPtr.Zero)
                {
                    try
                    {
                        Guid schemeGuid = Marshal.PtrToStructure<Guid>(pGuid);
                        uint bufferSize = 256;
                        IntPtr pBuffer = Marshal.AllocHGlobal((int)bufferSize);
                        try
                        {
                            if (PowerReadFriendlyName(IntPtr.Zero, ref schemeGuid, IntPtr.Zero, IntPtr.Zero, pBuffer, ref bufferSize) == 0)
                            {
                                string name = Marshal.PtrToStringUni(pBuffer) ?? string.Empty;
                                if (!string.IsNullOrWhiteSpace(name))
                                {
                                    _cachedPowerPlan = name;
                                    return _cachedPowerPlan;
                                }
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(pBuffer);
                        }
                    }
                    finally
                    {
                        LocalFree(pGuid);
                    }
                }
            }
            catch { }

            return _cachedPowerPlan;
        }
    }
}
