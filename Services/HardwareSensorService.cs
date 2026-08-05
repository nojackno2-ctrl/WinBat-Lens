using System;
using System.Linq;
using System.Threading;
using LibreHardwareMonitor.Hardware;

namespace WinBatLens.Services
{
    /// <summary>
    /// 提供基於 LibreHardwareMonitor 之實體硬體感測器（如 dGPU NVML 功耗與電池電壓）讀取服務。
    /// 採非同步背景定時輪詢，並支援託管/待機模式切換（前台 1s / 托盤背景 5s）以最小化 CPU 資源佔用。
    /// </summary>
    public static class HardwareSensorService
    {
        private static Computer? _computer;
        private static IHardware? _dGpu;
        private static IHardware? _battery;

        private static readonly object _sync = new object();
        private static System.Threading.Timer? _pollTimer;
        private static int _polling;
        private static int _shuttingDown;

        /// <summary>前台高畫質圖表更新間隔（毫秒）。</summary>
        private const int RefreshMs = 1000;

        /// <summary>托盤背景節能更新間隔（毫秒）。</summary>
        private const int IdleRefreshMs = 5000;

        private static int _currentIntervalMs = RefreshMs;

        /// <summary>感測器服務是否已成功初始化。</summary>
        public static bool IsInitialized { get; private set; }

        /// <summary>獨立顯示卡 (dGPU) 實測 Package 功耗（瓦特 W），無數據時為 null。</summary>
        public static double? DgpuPackageW { get; private set; }

        /// <summary>電池端實測電壓（伏特 V），無數據時為 null。</summary>
        public static double? BatteryVoltageV { get; private set; }

        /// <summary>
        /// 初始化感測器堆疊並建立背景輪詢定時器。建議於背景工作執行緒呼叫。
        /// </summary>
        public static void Initialize()
        {
            lock (_sync)
            {
                if (IsInitialized) return;

                try
                {
                    Volatile.Write(ref _shuttingDown, 0);
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

                    _dGpu ??= _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuAmd);

                    IsInitialized = true;

                    // 預熱感測器（避免前兩次採樣為 null）
                    PollOnce();
                    PollOnce();

                    DisableValueHistory();

                    int interval = System.Threading.Volatile.Read(ref _currentIntervalMs);
                    _pollTimer = new System.Threading.Timer(_ => PollOnce(), null, interval, interval);

                    interval = System.Threading.Volatile.Read(ref _currentIntervalMs);
                    try { _pollTimer.Change(interval, interval); } catch (ObjectDisposedException) { }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"HardwareSensorService.Initialize failed: {ex.Message}");
                    try { _computer?.Close(); } catch { }
                    _computer = null;
                    _dGpu = null;
                    _battery = null;
                    DgpuPackageW = null;
                    BatteryVoltageV = null;
                    IsInitialized = false;
                }
            }
        }

        /// <summary>
        /// 設定背景待機模式（視窗隱藏至托盤時傳入 true 以降低採樣率至 5 秒）。
        /// </summary>
        /// <param name="idle">是否啟用待機節能模式。</param>
        public static void SetIdleMode(bool idle)
        {
            int interval = idle ? IdleRefreshMs : RefreshMs;
            if (System.Threading.Interlocked.Exchange(ref _currentIntervalMs, interval) == interval)
                return;

            var timer = _pollTimer;
            if (timer == null) return;

            try { timer.Change(idle ? interval : 0, interval); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>
        /// 執行單次硬體感測器採樣（於背景 ThreadPool 執行緒運作）。
        /// </summary>
        private static void PollOnce()
        {
            if (Volatile.Read(ref _shuttingDown) != 0) return;
            if (System.Threading.Interlocked.Exchange(ref _polling, 1) == 1) return;

            try
            {
                UpdateAndRead(_dGpu, hw =>
                {
                    DgpuPackageW = ReadPower(hw, "GPU Package", "GPU Power", "GPU PPT");
                });

                UpdateAndRead(_battery, hw =>
                {
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
        /// 關閉 LibreHardwareMonitor 感測器的滾動歷史紀錄，降低記憶體消耗。
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

        /// <summary>
        /// 更新硬體感測器並呼叫讀取委派。
        /// </summary>
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
        /// 自指定的硬體物件中尋找匹配名稱之功耗感測器數值。
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

        /// <summary>
        /// 停止背景輪詢定時器並關閉 LibreHardwareMonitor 資源。
        /// </summary>
        public static void Shutdown()
        {
            Volatile.Write(ref _shuttingDown, 1);
            System.Threading.Timer? timer;

            lock (_sync)
            {
                timer = _pollTimer;
                _pollTimer = null;
            }

            try { timer?.Dispose(); } catch { }

            for (int i = 0; i < 1000 && Volatile.Read(ref _polling) != 0; i++)
                Thread.Sleep(1);

            lock (_sync)
            {
                try { _computer?.Close(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Sensor shutdown: {ex.Message}"); }
                _computer = null;
                _dGpu = null;
                _battery = null;
                DgpuPackageW = null;
                BatteryVoltageV = null;
                IsInitialized = false;
            }
        }
    }
}
