using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using WinBatLens.Models;

namespace WinBatLens.Services
{
    /// <summary>
    /// 提供 1Hz 全系統即時電源與硬體功耗遙測整合主服務。
    /// 整合 CPU 使用率、iGPU/dGPU 負載與功耗 (DXGI/NVML)、電池端原生充放電功率 (IOCTL) 與螢幕亮度等數據。
    /// </summary>
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

        // Raw GPU Engine samples from the previous tick, keyed by instance name.
        // GPU utilization is a rate: it needs two raw samples taken over an
        // interval, so last tick's must survive to compute this tick's value.
        // A single reading in isolation always yields 0 — the original bug
        // behind dGPU 0W.
        private static readonly Dictionary<string, CounterSample> _gpuEngineSamples
            = new Dictionary<string, CounterSample>(StringComparer.OrdinalIgnoreCase);
        private static PerformanceCounterCategory? _gpuEngineCategory;
        // Reused across ticks so a ~600-instance sweep does not allocate two
        // fresh collections every second.
        private static readonly HashSet<string> _gpuEngineSeen
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> _gpuEngineStale = new List<string>();

        // The LUID and engine type parsed out of a GPU Engine instance name,
        // cached for as long as that instance exists. Instance names are stable
        // — the same ~600 strings come back from the counter category every
        // tick — but parsing them was not: lower-casing plus two Substring
        // calls each meant roughly 1,800 throwaway strings a second, all of
        // them identical to the previous second's.
        private readonly struct GpuEngineKey
        {
            public GpuEngineKey(string luid, string engineType)
            {
                Luid = luid;
                EngineType = engineType;
            }

            public string Luid { get; }
            public string EngineType { get; }

            // default(GpuEngineKey) is the "this instance name does not parse"
            // marker, and its strings are null — so this must not dereference.
            public bool IsValid => !string.IsNullOrEmpty(Luid) && !string.IsNullOrEmpty(EngineType);
        }

        private static readonly Dictionary<string, GpuEngineKey> _gpuEngineKeys
            = new Dictionary<string, GpuEngineKey>(StringComparer.OrdinalIgnoreCase);

        // luid -> (engine type -> summed utilization) for the current tick.
        // Reused across ticks and cleared in place: a machine has two or three
        // adapters and a handful of engine types, so the same few dictionaries
        // serve for the life of the process instead of being rebuilt each time.
        private static readonly Dictionary<string, Dictionary<string, double>> _perAdapter
            = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);

        // Maps a GPU Engine LUID token (e.g. "luid_0x00000000_0x00010666") to
        // whether that adapter is the discrete GPU. Built once from DXGI.
        private static Dictionary<string, bool>? _luidIsDiscrete;

        // Adapter names never change while the process runs, so the list is
        // resolved to the three strings the UI wants exactly once instead of
        // being re-scanned with LINQ on every tick.
        private static bool _gpuNamesResolved;
        private static bool _hasDiscreteGpu;
        private static string _dgpuName = "無獨立顯示卡";
        private static string _igpuName = "顯示晶片 (GPU)";

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
        /// 預熱監測服務所需的效能計數器、GPU 分類與底層硬體感測器（應於背景 ThreadPool 執行緒呼叫）。
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
                // WinRT activation is COM work; it belongs here rather than on
                // the first tick. Reads afterwards are effectively free.
                PowerSupplyService.Initialize();
                bool haveWarmup = BatteryTelemetryService.TryRead(out var warmup);
                GetBatteryVoltage(haveWarmup, warmup.VoltageV);
                GetActivePowerPlanName();
            }
            catch { }
        }

        /// <summary>
        /// 採樣並傳回當前 1Hz 全系統即時功耗與硬體狀態模型。
        /// </summary>
        /// <returns><see cref="RealTimePowerState"/> 即時狀態。</returns>
        public static RealTimePowerState GetCurrentPowerState()
        {
            var state = new RealTimePowerState();

            // Set GPU Names (resolved once — the adapter list cannot change
            // under a running process, so this used to be two LINQ scans and a
            // pair of closures per second for a constant answer).
            EnsureGpuNames();
            state.HasDiscreteGpu = _hasDiscreteGpu;
            state.DgpuName = _dgpuName;
            state.IgpuName = _igpuName;

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

            // 4. Screen brightness
            state.ScreenBrightnessPercent = GetScreenBrightnessPercent();
            state.IsBrightnessMeasured = _brightnessMeasured;

            // There is no system-total wattage. Screen, disk, Wi-Fi, RAM and
            // chipset power were all linear guesses over utilisation and are
            // gone; summing them produced an equally invented total. On battery
            // the pack reports the real whole-system figure (section 9), which
            // is strictly better than any sum of estimates could be.

            // 8. Windows power status + the battery's own charge/discharge rate.
            bool haveBatt = BatteryTelemetryService.TryRead(out var batt);

            // Windows' verdict on the charger itself. This is the only thing
            // the platform will say about the external supply rather than
            // about the pack — the USB-C PD contract, and with it a real
            // adapter wattage, is not reachable unelevated (PowerSupplyService
            // documents what was tried).
            state.SupplyCapability = PowerSupplyService.GetStatus();

            // 8a. Energy in the pack and the health that follows from it, both
            // read from the battery driver. A relative-capacity pack reports
            // capacities in its own arbitrary units, so it is excluded here
            // rather than having those numbers presented as watt-hours.
            var pack = BatteryTelemetryService.GetPackInfo();
            bool energyUsable = haveBatt && batt.IsCapacityKnown
                && pack.IsValid && !pack.IsCapacityRelative
                && pack.FullChargedCapacityMWh > 0;

            if (energyUsable)
            {
                state.IsEnergyMeasured = true;
                state.RemainingCapacityMWh = batt.RemainingCapacityMWh;
                state.FullChargedCapacityMWh = pack.FullChargedCapacityMWh;
                state.DesignedCapacityMWh = pack.DesignedCapacityMWh;
                state.TrueSocPercent = Math.Round(
                    Math.Min(100.0, batt.RemainingCapacityMWh / (double)pack.FullChargedCapacityMWh * 100.0), 1);
                state.DriverHealthPercent = pack.HealthPercent ?? 0.0;

                state.BatteryEnergyText =
                    $"{batt.RemainingCapacityMWh / 1000.0:F1} / {pack.FullChargedCapacityMWh / 1000.0:F1} Wh";
                state.BatteryCapacityHealthText = pack.DesignedCapacityMWh > 0
                    ? $"{pack.FullChargedCapacityMWh / 1000.0:F1} / {pack.DesignedCapacityMWh / 1000.0:F1} Wh"
                    : $"{pack.FullChargedCapacityMWh / 1000.0:F1} Wh";
            }

            // Real pack temperature, when the firmware implements the level.
            if (haveBatt && batt.IsTemperatureKnown)
            {
                state.IsBatteryTemperatureMeasured = true;
                state.BatteryTemperatureC = batt.TemperatureC;
            }

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

                        // Time to full, from the energy still missing and the
                        // measured charge rate. The pack's own capacity is used
                        // here; the previous version assumed a 56 Wh battery,
                        // which was this machine's size hard-coded into the
                        // formula and wrong everywhere else.
                        if (state.IsChargeRateMeasured && state.ChargingRateW > 0
                            && state.IsEnergyMeasured
                            && state.FullChargedCapacityMWh > state.RemainingCapacityMWh)
                        {
                            double missingWh = (state.FullChargedCapacityMWh - state.RemainingCapacityMWh) / 1000.0;
                            TimeSpan chargeTime = TimeSpan.FromHours(missingWh / state.ChargingRateW);

                            state.EstimatedTimeRemainingText = chargeTime.TotalHours < 24
                                ? $"預估 {chargeTime.Hours}小時{chargeTime.Minutes}分 充飽"
                                : "充電中...";
                        }
                        else
                        {
                            state.EstimatedTimeRemainingText = "充電中...";
                        }
                    }
                    else if (haveBatt && batt.IsRateKnown && batt.DischargeW > 0)
                    {
                        // External power is connected and the pack is *still*
                        // draining. The charger is not covering the load, and
                        // the battery is silently making up the difference.
                        //
                        // This is the everyday failure mode of charging a
                        // laptop over USB-C: a 65 W PD brick cannot hold up a
                        // machine that wants more than 65 W, so the pack drains
                        // even though the cable is plugged in. It used to be
                        // folded into the "on AC, battery idle" branch below
                        // and displayed as "-- W", which hid the one wattage
                        // that actually matters in this state.
                        //
                        // The shortfall is measured at the pack, so unlike any
                        // adapter figure it is a reading rather than a guess.
                        state.ChargingRateW = 0.0;
                        state.IsChargerDeficit = true;
                        state.DischargeRateW = batt.DischargeW;
                        state.IsDischargeRateMeasured = true;

                        state.PowerStatusText = "外接電源供電不足";
                        state.ChargingStatusText =
                            $"外接電源供電不足 | 電池補上 -{state.DischargeRateW:F1}W (電池實測)";
                        state.DischargeRateText =
                            $"-{state.DischargeRateW:F1} W (外接電源不足，由電池補足)";

                        // How long the pack can keep covering the shortfall.
                        // Windows' own forecast is not offered while on AC, so
                        // this comes from the pack's remaining energy over the
                        // measured deficit — both real readings.
                        if (energyUsable)
                        {
                            TimeSpan t = TimeSpan.FromHours(
                                state.RemainingCapacityMWh / 1000.0 / state.DischargeRateW);
                            state.EstimatedTimeRemainingText = t.TotalHours < 24
                                ? $"仍在耗電，約 {t.Hours} 小時 {t.Minutes} 分鐘 (以 -{state.DischargeRateW:F1}W 計算)"
                                : "外接電源不足，電池仍在耗電";
                        }
                        else
                        {
                            state.EstimatedTimeRemainingText = "外接電源不足，電池仍在耗電";
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

                        // The pack's own state of charge, where the Windows
                        // percentage is a rounded integer. A charge limit that
                        // stops at 95% now reads as 95.0%, not as "full".
                        if (state.IsEnergyMeasured)
                        {
                            state.EstimatedTimeRemainingText = state.TrueSocPercent >= 99.0
                                ? $"電量已充滿 ({state.TrueSocPercent:F1}%)"
                                : $"電量 {state.TrueSocPercent:F1}% (電池保養未充電)";
                        }
                        else
                        {
                            state.EstimatedTimeRemainingText = state.BatteryPercent >= 98
                                ? "電量已充滿 100%"
                                : $"電量 {state.BatteryPercent}% (電池保養未充電)";
                        }

                        state.DischargeRateText = "-- W";
                    }
                }
                else
                {
                    state.IsCharging = false;
                    state.ChargingRateW = 0.0;
                    state.PowerStatusText = "電池放電中";
                    state.ChargingStatusText = "使用電池供電";

                    // Three sources, best first. Windows' own forecast is
                    // preferred because it smooths the rate over time; the two
                    // fallbacks exist because it reports nothing at all for the
                    // first minute or so after unplugging, which used to leave
                    // the card stuck on "估算中...".
                    if (status.BatteryLifeTime > 0 && status.BatteryLifeTime < 86400)
                    {
                        TimeSpan t = TimeSpan.FromSeconds(status.BatteryLifeTime);
                        state.EstimatedTimeRemainingText = $"{t.Hours} 小時 {t.Minutes} 分鐘";
                    }
                    else if (haveBatt && batt.IsEstimatedRuntimeKnown)
                    {
                        TimeSpan t = TimeSpan.FromSeconds(batt.EstimatedRuntimeSeconds);
                        state.EstimatedTimeRemainingText = $"{t.Hours} 小時 {t.Minutes} 分鐘 (電池驅動估算)";
                    }
                    else if (energyUsable && batt.IsRateKnown && batt.DischargeW > 0)
                    {
                        // Remaining watt-hours over the present draw. Both terms
                        // are measured at the pack, so this is arithmetic on real
                        // readings rather than a utilisation curve.
                        TimeSpan t = TimeSpan.FromHours(state.RemainingCapacityMWh / 1000.0 / batt.DischargeW);
                        state.EstimatedTimeRemainingText = t.TotalHours < 24
                            ? $"{t.Hours} 小時 {t.Minutes} 分鐘 (以目前 {batt.DischargeW:F1}W 計算)"
                            : "估算中...";
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



            // 11. Battery Physical Telemetry (Voltage, Current, Temperature).
            // The pack voltage arrives in the same IOCTL the rate came from, so
            // this tick's reading is handed over rather than taken again — the
            // second read cost three more device IOCTLs every second.
            double volts = GetBatteryVoltage(haveBatt, batt.VoltageV);
            state.BatteryVoltageV = volts;
            state.IsVoltageMeasured = _voltageMeasured;

            // Current follows whichever way power is actually flowing. On AC
            // that is normally the charge rate, but not when the charger is
            // being out-run and the pack is discharging into the machine.
            double activePowerW = (!state.IsAcOnline || state.IsChargerDeficit)
                ? state.DischargeRateW
                : state.ChargingRateW;
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

        private static void EnsureGpuNames()
        {
            if (_gpuNamesResolved) return;
            _gpuNamesResolved = true;

            GpuInfo? dGpu = null;
            GpuInfo? iGpu = null;
            foreach (var gpu in _cachedGpus)
            {
                if (gpu.IsDiscrete) dGpu ??= gpu;
                else iGpu ??= gpu;
            }

            if (dGpu != null)
            {
                _hasDiscreteGpu = true;
                _dgpuName = dGpu.Name;
                _igpuName = iGpu?.Name ?? "內建顯示晶片 (iGPU)";
            }
            else
            {
                _hasDiscreteGpu = false;
                _igpuName = iGpu?.Name ?? (_cachedGpus.Count > 0 ? _cachedGpus[0].Name : "顯示晶片 (GPU)");
                _dgpuName = "無獨立顯示卡";
            }
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

            // Cleared rather than rebuilt: the adapters and engine types are
            // the same every tick, so the dictionaries are reused in place.
            foreach (var engMap in _perAdapter.Values) engMap.Clear();

            try
            {
                _gpuEngineCategory ??= new PerformanceCounterCategory("GPU Engine");

                // One snapshot of the whole category per tick.
                //
                // This used to hold a PerformanceCounter per instance and call
                // NextValue() on each. Every one of those calls re-reads the
                // category's entire performance data block, so the loop was
                // quadratic in the instance count — and this machine exposes
                // ~600 GPU Engine instances (one per process per engine type).
                // Measured: 480-925 ms of CPU per tick, on the UI thread, once
                // a second. ReadCategory() takes the same readings in a single
                // pass in ~2 ms; the per-instance rate is then computed from
                // last tick's raw sample, which is exactly what NextValue() did
                // internally.
                var util = ReadGpuEngineUtilization();
                if (util == null) return (0.0, 0.0);

                _gpuEngineSeen.Clear();

                foreach (InstanceData id in util.Values)
                {
                    string inst = id.InstanceName;
                    _gpuEngineSeen.Add(inst);

                    // A new instance has no baseline and contributes 0; it
                    // becomes accurate on the next tick.
                    double val = 0.0;
                    var sample = id.Sample;
                    if (_gpuEngineSamples.TryGetValue(inst, out var previous))
                    {
                        try { val = CounterSampleCalculator.ComputeCounterValue(previous, sample); }
                        catch { val = 0.0; }
                    }
                    _gpuEngineSamples[inst] = sample;

                    // Idle instances are the overwhelming majority every tick,
                    // and they cannot move any adapter's figure — so the name
                    // is only parsed once something is actually running on it.
                    if (val <= 0) continue;

                    if (!_gpuEngineKeys.TryGetValue(inst, out var key))
                    {
                        key = ParseGpuEngineInstance(inst);
                        _gpuEngineKeys[inst] = key;
                    }
                    if (!key.IsValid) continue;

                    if (!_perAdapter.TryGetValue(key.Luid, out var engMap))
                    {
                        engMap = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                        _perAdapter[key.Luid] = engMap;
                    }
                    engMap.TryGetValue(key.EngineType, out double running);
                    engMap[key.EngineType] = running + val;
                }

                // Forget instances that no longer exist (e.g. a process that
                // closed) so the dictionaries do not grow without bound.
                if (_gpuEngineSamples.Count > _gpuEngineSeen.Count)
                {
                    _gpuEngineStale.Clear();
                    foreach (var key in _gpuEngineSamples.Keys)
                    {
                        if (!_gpuEngineSeen.Contains(key)) _gpuEngineStale.Add(key);
                    }
                    foreach (var key in _gpuEngineStale)
                    {
                        _gpuEngineSamples.Remove(key);
                        _gpuEngineKeys.Remove(key);
                    }
                }
            }
            catch { }

            double iGpuMax = 0.0;
            double dGpuMax = 0.0;

            foreach (var kv in _perAdapter)
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

        /// <summary>
        /// One pass over the "GPU Engine" category, returning the per-instance
        /// data for "Utilization Percentage". Returns null when the category or
        /// the counter is unavailable, in which case the caller reports 0%
        /// rather than guessing.
        /// </summary>
        private static InstanceDataCollection? ReadGpuEngineUtilization()
        {
            if (_gpuEngineCategory == null) return null;

            var data = _gpuEngineCategory.ReadCategory();

            var util = data["Utilization Percentage"];
            if (util != null) return util;

            // Counter names come back localised on some systems, so the English
            // key is a preference rather than a guarantee.
            foreach (InstanceDataCollection c in data.Values)
            {
                if (c.CounterName.IndexOf("Utilization", StringComparison.OrdinalIgnoreCase) >= 0)
                    return c;
            }

            return null;
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

        /// <summary>
        /// Splits a GPU Engine instance name (e.g.
        /// "pid_1234_luid_0x00000000_0x00010666_phys_0_eng_0_engtype_3D") into
        /// the adapter's LUID token and its engine type. Both dictionaries that
        /// consume these compare case-insensitively, so nothing is lower-cased
        /// here — that allocation was the point of the exercise.
        /// </summary>
        private static GpuEngineKey ParseGpuEngineInstance(string instance)
        {
            string luid = ExtractToken(instance, "luid_", "_phys_");
            string engType = ExtractToken(instance, "engtype_", null);
            if (luid.Length == 0 || engType.Length == 0) return default;
            return new GpuEngineKey(string.Concat("luid_", luid), engType);
        }

        // Extracts the text between <start> and <end> markers. If <end> is null,
        // reads to the end of the string. Returns "" if not found.
        private static string ExtractToken(string source, string start, string? end)
        {
            int s = source.IndexOf(start, StringComparison.OrdinalIgnoreCase);
            if (s < 0) return string.Empty;
            s += start.Length;
            if (end == null) return source.Substring(s);
            int e = source.IndexOf(end, s, StringComparison.OrdinalIgnoreCase);
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
        private static double GetBatteryVoltage(bool packReadingValid, double packVoltageV)
        {
            if (Environment.TickCount64 - _lastTelemetryTick < WmiRefreshMs)
                return _cachedVoltageV;
            _lastTelemetryTick = Environment.TickCount64;

            // The battery IOCTL returns voltage in the same call the rate comes
            // from, so prefer it; the sensor stack is the next best source.
            double voltageV = 0;
            if (packReadingValid && packVoltageV > 0)
                voltageV = packVoltageV;
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
