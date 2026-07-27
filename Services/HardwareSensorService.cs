using System;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace WinBatLens.Services
{
    /// <summary>
    /// Reads real power/temperature sensors via LibreHardwareMonitor.
    /// Every reading is exposed as a nullable: <c>null</c> means this machine
    /// genuinely does not report that value, and the caller must fall back to
    /// an estimate and say so. Nothing here ever invents a number.
    /// </summary>
    /// <remarks>
    /// Measured on the development machine (Ryzen + RTX 3060 Laptop):
    /// NVIDIA GPU package power and temperatures work at normal privilege
    /// because NVML is a userspace API, but CPU package power comes from RAPL
    /// via a kernel driver and reads 0 W unless the process is elevated. The UI
    /// does not report CPU power for that reason, so the CPU group is not opened
    /// at all — which also keeps the ring-0 driver out of the process.
    /// <para>
    /// Only sensors something actually displays are swept on the timer. An
    /// <c>IHardware.Update()</c> is not free — measured per sweep on this
    /// machine: dGPU 15.6 ms of CPU, iGPU 6.2 ms, CPU 3.1 ms, battery 0.0 ms —
    /// and at one sweep a second the ones feeding nothing were pure cost.
    /// </para>
    /// </remarks>
    public static class HardwareSensorService
    {
        private static Computer? _computer;
        private static IHardware? _dGpu;
        private static IHardware? _battery;

        private static readonly object _sync = new object();
        private static System.Threading.Timer? _pollTimer;
        private static int _polling;

        // A sweep is far too slow to sit on the UI thread's 1 s tick (it was
        // 85-256 ms when every hardware group was polled, ~16 ms now that only
        // the dGPU and the battery are). Polling therefore runs on a background
        // timer and the UI only ever reads cached fields.
        private const int RefreshMs = 1000;

        public static bool IsInitialized { get; private set; }

        /// <summary>True when discrete GPU package power is actually being reported.</summary>
        public static bool IsGpuPowerAvailable { get; private set; }

        public static double? DgpuPackageW { get; private set; }
        public static double? DgpuTempC { get; private set; }
        public static double? BatteryRateW { get; private set; }
        public static double? BatteryVoltageV { get; private set; }

        /// <summary>
        /// Opens the sensor stack. Slow (loads a kernel driver on some systems),
        /// so call it from the background warmup, never on the UI thread.
        /// </summary>
        public static void Initialize()
        {
            lock (_sync)
            {
                if (IsInitialized) return;

                try
                {
                    // Only the hardware we actually report on. Enabling storage
                    // and motherboard costs real time on Open() and adds
                    // sensors we never read. The CPU group is off for the same
                    // reason plus one more: it is the part that loads the ring-0
                    // driver, and RAPL reads 0 W unelevated on this machine
                    // anyway. CPU load already comes from a PerformanceCounter.
                    _computer = new Computer
                    {
                        IsCpuEnabled = false,
                        IsGpuEnabled = true,
                        IsBatteryEnabled = true,
                    };

                    _computer.Open();

                    _battery = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Battery);
                    _dGpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuNvidia)
                            ?? _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuIntel);

                    // An AMD-only machine has no NVIDIA/Intel part, so the AMD
                    // GPU is the discrete one rather than the integrated one.
                    // Where a discrete part exists the AMD one is integrated,
                    // and nothing displays its wattage, so it is never polled.
                    _dGpu ??= _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuAmd);

                    IsInitialized = true;

                    // Prime the sensors: several report null until the second
                    // update, which would otherwise look like "unavailable".
                    PollOnce();
                    PollOnce();

                    IsGpuPowerAvailable = DgpuPackageW.HasValue;

                    DisableValueHistory();

                    _pollTimer = new System.Threading.Timer(_ => PollOnce(), null, RefreshMs, RefreshMs);
                }
                catch (Exception ex)
                {
                    // A locked-down machine, a blocked driver or a hostile AV can
                    // all fail here. The app must still run on formula estimates.
                    System.Diagnostics.Debug.WriteLine($"HardwareSensorService.Initialize failed: {ex.Message}");
                    IsInitialized = false;
                }
            }
        }

        /// <summary>
        /// One sensor sweep. Only ever runs on the polling thread (or on the
        /// warmup thread during <see cref="Initialize"/>), never on the UI
        /// thread. Re-entrancy is skipped rather than queued: if a sweep is
        /// still running when the timer fires again, that tick is simply lost.
        /// </summary>
        private static void PollOnce()
        {
            if (System.Threading.Interlocked.Exchange(ref _polling, 1) == 1) return;

            try
            {
                UpdateAndRead(_dGpu, hw =>
                {
                    DgpuPackageW = ReadPower(hw, "GPU Package", "GPU Power", "GPU PPT");
                    DgpuTempC = ReadTemp(hw, "GPU Core", "GPU Hot Spot");
                });

                UpdateAndRead(_battery, hw =>
                {
                    BatteryRateW = ReadPower(hw, "Charge/Discharge Rate", "Charge Rate", "Discharge Rate");
                    var v = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Voltage && s.Value.HasValue && s.Value.Value > 0);
                    BatteryVoltageV = v?.Value;
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"HardwareSensorService.PollOnce error: {ex.Message}");
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _polling, 0);
            }
        }

        /// <summary>
        /// Every ISensor retains a rolling history of past readings — one day's
        /// worth by default. Polling once a second across every sensor of the
        /// CPU/GPU/battery, that retention grew private bytes by ~20 MB in three
        /// minutes. Only the instantaneous value is ever read here, so the
        /// window is collapsed to zero and any primed values are dropped.
        /// </summary>
        private static void DisableValueHistory()
        {
            if (_computer == null) return;

            foreach (var hw in _computer.Hardware)
            {
                Apply(hw);
                foreach (var sub in hw.SubHardware) Apply(sub);
            }

            static void Apply(IHardware hw)
            {
                foreach (var s in hw.Sensors)
                {
                    try
                    {
                        s.ValuesTimeWindow = TimeSpan.Zero;
                        s.ClearValues();
                    }
                    catch { }
                }
            }
        }

        private static void UpdateAndRead(IHardware? hw, Action<IHardware> read)
        {
            if (hw == null) return;
            try
            {
                hw.Update();
                foreach (var sub in hw.SubHardware) sub.Update();
                read(hw);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Sensor update failed for {hw.Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns the first matching power sensor with a usable reading.
        /// A sensor present but reading exactly 0 W is treated as unavailable:
        /// that is what RAPL reports when the driver could not be loaded, and a
        /// powered-on component never genuinely draws 0 W.
        /// </summary>
        private static double? ReadPower(IHardware hw, params string[] preferredNames)
        {
            foreach (var name in preferredNames)
            {
                var s = hw.Sensors.FirstOrDefault(x =>
                    x.SensorType == SensorType.Power &&
                    string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (s?.Value is float v && v > 0.01f) return Math.Round(v, 1);
            }
            return null;
        }

        private static double? ReadTemp(IHardware hw, params string[] preferredNames)
        {
            foreach (var name in preferredNames)
            {
                var s = hw.Sensors.FirstOrDefault(x =>
                    x.SensorType == SensorType.Temperature &&
                    string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (s?.Value is float v && v > 0.01f) return Math.Round(v, 1);
            }
            return null;
        }

        public static void Shutdown()
        {
            lock (_sync)
            {
                try { _pollTimer?.Dispose(); } catch { }
                _pollTimer = null;

                try { _computer?.Close(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Sensor shutdown: {ex.Message}"); }
                _computer = null;
                IsInitialized = false;
            }
        }
    }
}
