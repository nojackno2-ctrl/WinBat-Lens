using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using WinBatLens.Models;
using WinBatLens.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using MediaColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;

namespace WinBatLens
{
    public partial class MainWindow : Window
    {
        private BatteryReportData? _currentReport;
        private DispatcherTimer? _livePowerTimer;

        // Working-set re-trim cadence while hidden, in 1-second timer ticks.
        private const int TrimIntervalTicks = 60;
        private int _hiddenTickCount;
        private NotifyIcon? _notifyIcon;
        private bool _isExitRequested = false;
        private bool _trayBalloonShown = false;

        // Tray menu items kept as fields so ApplyLanguage can relabel them.
        private ToolStripMenuItem? _trayItemShow;
        private ToolStripMenuItem? _trayItemCheck;
        private ToolStripMenuItem? _trayItemAutoStart;
        private ToolStripMenuItem? _trayItemExit;

        private readonly Queue<(double DischargeW, double ChargeW, double CpuW, double GpuW)> _chartHistory =
            new Queue<(double, double, double, double)>();
        private const int MAX_CHART_POINTS = 60;

        // Frozen, shared brushes reused across timer ticks. UpdateLivePowerUI
        // runs once per second; allocating fresh SolidColorBrush objects every
        // tick created needless GC pressure. Freezing also lets WPF share them
        // across threads without cloning.
        private static readonly SolidColorBrush BrushEmerald = CreateFrozen(MediaColor.FromRgb(0x10, 0xB9, 0x81));
        private static readonly SolidColorBrush BrushEmeraldBadge = CreateFrozen(MediaColor.FromArgb(0x20, 0x10, 0xB9, 0x81));
        private static readonly SolidColorBrush BrushAmber = CreateFrozen(MediaColor.FromRgb(0xF5, 0x9E, 0x0B));
        private static readonly SolidColorBrush BrushAmberBadge = CreateFrozen(MediaColor.FromArgb(0x20, 0xF5, 0x9E, 0x0B));
        private static readonly SolidColorBrush BrushGridStrong = CreateFrozen(MediaColor.FromArgb(0x20, 0x94, 0xA3, 0xB8));
        private static readonly SolidColorBrush BrushGridFaint = CreateFrozen(MediaColor.FromArgb(0x15, 0x94, 0xA3, 0xB8));
        private static readonly DoubleCollection DashStrong = CreateFrozenDashes(4, 4);
        private static readonly DoubleCollection DashFaint = CreateFrozenDashes(2, 4);

        private static SolidColorBrush CreateFrozen(MediaColor color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static DoubleCollection CreateFrozenDashes(double a, double b)
        {
            var dashes = new DoubleCollection { a, b };
            dashes.Freeze();
            return dashes;
        }

        public MainWindow()
        {
            InitializeComponent();

            try
            {
                this.Icon = BitmapFrame.Create(new Uri("pack://application:,,,/app_icon.png"));
            }
            catch { }

            TxtSystemModel.Text = $"{Environment.MachineName} ({Environment.OSVersion}) - C# WPF";
            Loaded += MainWindow_Loaded;
            Unloaded += MainWindow_Unloaded;
            Closing += MainWindow_Closing;
            StateChanged += MainWindow_StateChanged;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Warm up PerformanceCounters, GPU enumeration and WMI caches on a
            // background thread. Without this the first monitoring tick pays
            // the whole cost (potentially seconds) on the UI thread.
            var warmup = Task.Run(RealTimePowerService.Initialize);

            // Apply Initial Language
            ApplyLanguage();

            // Bind Power History List
            LvPowerHistory.ItemsSource = RealTimePowerHistoryService.Records;

            // Initialize System Tray Icon & AutoStart State
            InitSystemTrayIcon();
            InitAutoStartState();

            // Draw Background Gridlines
            DrawChartGridlines();

            await warmup;

            // Bind GPU Specs List (reuses the list discovered during warmup)
            LoadGpuSpecs();

            // Start live power monitoring timer (1s interval)
            StartLivePowerMonitoring();

            // Run initial battery report scan
            await RunBatteryCheckAsync();
        }

        private void BtnLanguageToggle_Click(object sender, RoutedEventArgs e)
        {
            LocalizationService.ToggleLanguage();
            ApplyLanguage();
            UpdateLivePowerUI();
        }

        private void ApplyLanguage()
        {
            this.Title = LocalizationService.Get("AppTitle");
            TxtSystemModel.Text = $"{Environment.MachineName} ({Environment.OSVersion}) - {LocalizationService.Get("SubTitle")}";
            ChkAutoStart.Content = LocalizationService.Get("AutoStart");
            BtnOpenReport.Content = LocalizationService.Get("BtnOpenReport");
            BtnGenerateReport.Content = LocalizationService.Get("BtnCheck");
            BtnExportData.Content = LocalizationService.Get("BtnExportJson");
            BtnLanguageToggle.Content = LocalizationService.Get("BtnLanguage");

            // Cards
            TxtHealthCardTitle.Text = LocalizationService.Get("HealthTitle");
            TxtBatterySpecsTitle.Text = LocalizationService.Get("BatterySpecsTitle");
            LblSpecName.Text = LocalizationService.Get("SpecName");
            LblSpecMfg.Text = LocalizationService.Get("SpecMfg");
            LblSpecChem.Text = LocalizationService.Get("SpecChem");
            LblSpecDesign.Text = LocalizationService.Get("SpecDesign");
            LblSpecFull.Text = LocalizationService.Get("SpecFull");
            LblSpecLoss.Text = LocalizationService.Get("SpecLoss");
            LblSpecCycles.Text = LocalizationService.Get("SpecCycles");

            TxtCapacityHistoryTitle.Text = LocalizationService.Get("CapacityHistoryTitle");
            TxtColPeriod.Text = LocalizationService.Get("ColPeriod");
            TxtColFullCap.Text = LocalizationService.Get("ColFullCap");
            TxtColDesignCap.Text = LocalizationService.Get("ColDesignCap");
            TxtColHealthPct.Text = LocalizationService.Get("ColHealthPct");

            // Tabs
            TabRealTime.Header = LocalizationService.Get("TabRealTime");
            TabHistoryLogs.Header = LocalizationService.Get("TabHistoryLogs");
            TabDiagnostics.Header = LocalizationService.Get("TabDiagnostics");
            TabLifeEstimates.Header = LocalizationService.Get("TabLifeEstimates");
            TabRecentUsage.Header = LocalizationService.Get("TabRecentUsage");

            TxtCardTotalPower.Text = LocalizationService.Get("CardTotalPower");
            TxtCardEstTime.Text = LocalizationService.Get("CardEstTime");
            TxtCardTelemetry.Text = LocalizationService.Get("CardTelemetry");
            TxtWaveformTitle.Text = LocalizationService.Get("WaveformTitle");
            TxtLegendDischarge.Text = LocalizationService.Get("LegendDischarge");
            TxtLegendCharge.Text = LocalizationService.Get("LegendCharge");
            TxtLegendGpu.Text = LocalizationService.Get("LegendGpu");

            TxtHardwareTitle.Text = LocalizationService.Get("HardwareTitle");
            LblHwBatteryTelemetry.Text = LocalizationService.Get("HwBatteryTelemetry");
            LblHwPowerPlan.Text = LocalizationService.Get("HwPowerPlan");
            LblHwCpu.Text = LocalizationService.Get("HwCpu");
            LblHwScreen.Text = LocalizationService.Get("HwScreen");
            LblHwWifi.Text = LocalizationService.Get("HwWifi");
            LblHwDisk.Text = LocalizationService.Get("HwDisk");
            LblHwRam.Text = LocalizationService.Get("HwRam");
            TxtGpuListTitle.Text = LocalizationService.Get("GpuListTitle");

            TxtHistoryLogHeader.Text = LocalizationService.Get("HistoryLogHeader");
            BtnExportPowerCsv.Content = LocalizationService.Get("BtnExportCsv");
            BtnClearPowerHistory.Content = LocalizationService.Get("BtnClearHistory");

            // System tray menu follows the same language as the window.
            if (_trayItemShow != null) _trayItemShow.Text = LocalizationService.Get("TrayShow");
            if (_trayItemCheck != null) _trayItemCheck.Text = LocalizationService.Get("TrayCheck");
            if (_trayItemAutoStart != null) _trayItemAutoStart.Text = LocalizationService.Get("TrayAutoStart");
            if (_trayItemExit != null) _trayItemExit.Text = LocalizationService.Get("TrayExit");
        }

        private void GridChartContainer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            DrawChartGridlines();
            RedrawWaveformChart();
        }

        private void DrawChartGridlines()
        {
            try
            {
                CanvasGridlines.Children.Clear();
                double w = GridChartContainer.ActualWidth;
                double h = GridChartContainer.ActualHeight;

                if (w <= 0 || h <= 0) return;

                // Horizontal Gridlines (4 divisions)
                for (int i = 1; i <= 3; i++)
                {
                    double y = (h / 4.0) * i;
                    var line = new Line
                    {
                        X1 = 0,
                        Y1 = y,
                        X2 = w,
                        Y2 = y,
                        Stroke = BrushGridStrong,
                        StrokeThickness = 1,
                        StrokeDashArray = DashStrong
                    };
                    CanvasGridlines.Children.Add(line);
                }

                // Vertical Gridlines (10 divisions)
                for (int i = 1; i <= 9; i++)
                {
                    double x = (w / 10.0) * i;
                    var line = new Line
                    {
                        X1 = x,
                        Y1 = 0,
                        X2 = x,
                        Y2 = h,
                        Stroke = BrushGridFaint,
                        StrokeThickness = 1,
                        StrokeDashArray = DashFaint
                    };
                    CanvasGridlines.Children.Add(line);
                }
            }
            catch { }
        }

        private void UpdateWaveformChart(RealTimePowerState state)
        {
            // Only plot values that were actually measured; an unavailable
            // reading contributes 0 rather than a fabricated curve.
            double disW = (!state.IsAcOnline && state.IsDischargeRateMeasured) ? state.DischargeRateW : 0.0;
            double chgW = (state.IsCharging && state.IsChargeRateMeasured) ? state.ChargingRateW : 0.0;
            double gpuW = state.IsDgpuPowerMeasured ? state.DgpuPowerW : 0.0;

            // The CPU series is gone: its wattage was a formula over
            // utilisation, and CPU package power is unreadable on this hardware.
            _chartHistory.Enqueue((disW, chgW, 0.0, gpuW));
            while (_chartHistory.Count > MAX_CHART_POINTS)
            {
                _chartHistory.Dequeue();
            }

            // Keep collecting history while minimized to tray, but skip the
            // expensive redraw when nobody can see the chart.
            if (IsVisible) RedrawWaveformChart();
        }

        private void RedrawWaveformChart()
        {
            try
            {
                double w = GridChartContainer.ActualWidth;
                double h = GridChartContainer.ActualHeight;

                if (w <= 0 || h <= 0 || _chartHistory.Count == 0) return;

                var dischargePoints = new PointCollection();
                var chargePoints = new PointCollection();
                var cpuPoints = new PointCollection();
                var gpuPoints = new PointCollection();

                // Iterate the queue directly (oldest→newest) instead of
                // materialising a List and running a LINQ Max each tick.
                double peakPower = 0.0;
                foreach (var item in _chartHistory)
                {
                    double p = Math.Max(Math.Max(item.DischargeW, item.ChargeW), Math.Max(item.CpuW, item.GpuW));
                    if (p > peakPower) peakPower = p;
                }
                double maxPowerW = Math.Max(35.0, peakPower * 1.15);

                // Update Y-Axis Scale Coordinates Text
                TxtYAxis100.Text = $"{maxPowerW:F0} W (100%)";
                TxtYAxis75.Text = $"{(maxPowerW * 0.75):F0} W (75%)";
                TxtYAxis50.Text = $"{(maxPowerW * 0.50):F0} W (50%)";
                TxtYAxis25.Text = $"{(maxPowerW * 0.25):F0} W (25%)";
                TxtYAxis0.Text = "0 W (0%)";

                int i = 0;
                foreach (var item in _chartHistory)
                {
                    double x = (i / (double)(MAX_CHART_POINTS - 1)) * w;

                    // Y values (0 at bottom, Height at top)
                    double yDischarge = h - Math.Min(h, Math.Max(0, (item.DischargeW / maxPowerW) * h));
                    double yCharge = h - Math.Min(h, Math.Max(0, (item.ChargeW / maxPowerW) * h));
                    double yCpu = h - Math.Min(h, Math.Max(0, (item.CpuW / maxPowerW) * h));
                    double yGpu = h - Math.Min(h, Math.Max(0, (item.GpuW / maxPowerW) * h));

                    dischargePoints.Add(new WpfPoint(x, yDischarge));
                    chargePoints.Add(new WpfPoint(x, yCharge));
                    cpuPoints.Add(new WpfPoint(x, yCpu));
                    gpuPoints.Add(new WpfPoint(x, yGpu));
                    i++;
                }

                PolylineDischarge.Points = dischargePoints;
                PolylineCharge.Points = chargePoints;
                PolylineCpu.Points = cpuPoints;
                PolylineGpu.Points = gpuPoints;
            }
            catch { }
        }

        private void InitAutoStartState()
        {
            try
            {
                bool isEnabled = StartupService.IsAutoStartEnabled();
                ChkAutoStart.IsChecked = isEnabled;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"InitAutoStartState error: {ex.Message}");
            }
        }

        private void ChkAutoStart_Click(object sender, RoutedEventArgs e)
        {
            bool enable = ChkAutoStart.IsChecked == true;
            bool success = StartupService.SetAutoStart(enable);

            if (success)
            {
                string statusMsg = enable ? "已設定開機自動啟動。" : "已取消開機自動啟動。";
                MessageBox.Show(statusMsg, "開機啟動設定", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                ChkAutoStart.IsChecked = !enable; // Revert
                MessageBox.Show("無法修改系統開機啟動登錄碼，請確認權限。", "設定失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void InitSystemTrayIcon()
        {
            try
            {
                _notifyIcon = new NotifyIcon();

                try
                {
                    var streamInfo = Application.GetResourceStream(new Uri("pack://application:,,,/app_icon.ico"));
                    if (streamInfo != null && streamInfo.Stream != null)
                    {
                        _notifyIcon.Icon = new System.Drawing.Icon(streamInfo.Stream);
                    }
                }
                catch { }

                if (_notifyIcon.Icon == null)
                {
                    try
                    {
                        var pngStreamInfo = Application.GetResourceStream(new Uri("pack://application:,,,/app_icon.png"));
                        if (pngStreamInfo != null && pngStreamInfo.Stream != null)
                        {
                            using (var bitmap = new System.Drawing.Bitmap(pngStreamInfo.Stream))
                            {
                                IntPtr hIcon = bitmap.GetHicon();
                                _notifyIcon.Icon = System.Drawing.Icon.FromHandle(hIcon);
                            }
                        }
                    }
                    catch { }
                }

                if (_notifyIcon.Icon == null)
                {
                    _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
                }

                _notifyIcon.Text = LocalizationService.Get("TrayTooltip");
                _notifyIcon.Visible = true;

                _notifyIcon.DoubleClick += (s, args) => RestoreFromTray();

                // Context Menu for System Tray
                var contextMenu = new ContextMenuStrip();

                _trayItemShow = new ToolStripMenuItem(LocalizationService.Get("TrayShow"), null, (s, args) => RestoreFromTray());
                _trayItemShow.Font = new System.Drawing.Font(_trayItemShow.Font, System.Drawing.FontStyle.Bold);

                _trayItemCheck = new ToolStripMenuItem(LocalizationService.Get("TrayCheck"), null, async (s, args) => {
                    RestoreFromTray();
                    await RunBatteryCheckAsync();
                });

                _trayItemAutoStart = new ToolStripMenuItem(LocalizationService.Get("TrayAutoStart"));
                _trayItemAutoStart.Checked = StartupService.IsAutoStartEnabled();
                _trayItemAutoStart.Click += (s, args) => {
                    bool newState = !_trayItemAutoStart!.Checked;
                    if (StartupService.SetAutoStart(newState))
                    {
                        _trayItemAutoStart.Checked = newState;
                        ChkAutoStart.IsChecked = newState;
                    }
                };

                _trayItemExit = new ToolStripMenuItem(LocalizationService.Get("TrayExit"), null, (s, args) => {
                    _isExitRequested = true;
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                    Application.Current.Shutdown();
                });

                contextMenu.Items.Add(_trayItemShow);
                contextMenu.Items.Add(_trayItemCheck);
                contextMenu.Items.Add(new ToolStripSeparator());
                contextMenu.Items.Add(_trayItemAutoStart);
                contextMenu.Items.Add(new ToolStripSeparator());
                contextMenu.Items.Add(_trayItemExit);

                _notifyIcon.ContextMenuStrip = contextMenu;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"InitSystemTrayIcon error: {ex.Message}");
            }
        }

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                HideToTray();
            }
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_isExitRequested)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            // Real exit: Unloaded is not reliably raised for a top-level
            // Window, so release the timer, sensors and tray icon here.
            _livePowerTimer?.Stop();
            try { HardwareSensorService.Shutdown(); } catch { }
            try { BatteryTelemetryService.Shutdown(); } catch { }
            try
            {
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                }
            }
            catch { }
        }

        [DllImport("psapi.dll")]
        private static extern int EmptyWorkingSet(IntPtr hProcess);

        private void HideToTray()
        {
            this.Hide();

            // Only explain the tray behaviour the first time; after that the
            // balloon is just noise on every minimize.
            if (_notifyIcon != null && !_trayBalloonShown)
            {
                _trayBalloonShown = true;
                _notifyIcon.ShowBalloonTip(2000,
                    LocalizationService.Get("TrayBalloonTitle"),
                    LocalizationService.Get("TrayBalloonText"),
                    ToolTipIcon.Info);
            }

            // The app spends most of its life minimized in the tray. Once the
            // window's visuals are torn down, compact the heap and hand idle
            // pages back to the OS so the background working set stays small.
            // Pages are transparently reloaded on demand when the user restores.
            TrimWorkingSet();
        }

        private static void TrimWorkingSet()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle);
            }
            catch { }
        }

        private void RestoreFromTray()
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();

            // Visual updates are skipped while hidden; refresh everything now
            // so the window doesn't show stale values for up to a second.
            UpdateLivePowerUI();
        }

        private void LoadGpuSpecs()
        {
            try
            {
                // The service already enumerated GPUs via WMI at startup;
                // reuse that list instead of running the query a second time.
                IcGpuList.ItemsSource = RealTimePowerService.InstalledGpus;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LoadGpuSpecs error: {ex.Message}");
            }
        }

        private void MainWindow_Unloaded(object sender, RoutedEventArgs e)
        {
            _livePowerTimer?.Stop();
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
        }

        private void StartLivePowerMonitoring()
        {
            _livePowerTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _livePowerTimer.Tick += LivePowerTimer_Tick;
            _livePowerTimer.Start();

            UpdateLivePowerUI();
        }

        private void LivePowerTimer_Tick(object? sender, EventArgs e)
        {
            UpdateLivePowerUI();

            // Trimming once on minimize is not enough: the OS pages the app
            // back in as it keeps sampling, so the working set climbs back to
            // roughly its windowed size within a few minutes. Re-trim on a slow
            // cadence for as long as the window stays hidden.
            if (!IsVisible)
            {
                if (++_hiddenTickCount >= TrimIntervalTicks)
                {
                    _hiddenTickCount = 0;
                    TrimWorkingSet();
                }
            }
            else
            {
                _hiddenTickCount = 0;
            }
        }

        /// <summary>
        /// Renders a wattage and says plainly where it came from. A real sensor
        /// reading gets an exact value; anything derived from a utilisation
        /// curve is prefixed with ~ and labelled, so the two are never confused.
        /// </summary>
        private static string FormatPower(double watts, bool measured)
        {
            bool en = LocalizationService.CurrentLanguage == AppLanguage.English;
            return measured
                ? $"{watts:F1} W ({(en ? "measured" : "實測")})"
                : $"~{watts:F1} W ({(en ? "estimated" : "推估")})";
        }

        private void UpdateLivePowerUI()
        {
            try
            {
                var state = RealTimePowerService.GetCurrentPowerState();

                // Add to Power & Battery Event History Service
                RealTimePowerHistoryService.AddRecordFromPowerState(state);

                // Update 60-Second Waveform Chart
                UpdateWaveformChart(state);

                // Update Dynamic Real-Time Wattage System Tray Icon (Green for Charging > Power, Red for Discharging)
                if (_notifyIcon != null)
                {
                    DynamicTrayIconService.UpdateTrayIcon(_notifyIcon, state);

                    string powerStatusStr = state.IsAcOnline ? $"AC Input: {state.AcTotalInputW:F1}W" : $"-{state.DischargeRateW:F1}W Discharging";
                    _notifyIcon.Text = $"WinBat Lens - {state.PowerStatusText}\nLevel: {state.BatteryPercent}% | {powerStatusStr}";
                }

                // While hidden in the tray only the icon, tooltip and history
                // need refreshing — skip every visual control update so the
                // background working set and per-tick allocations stay minimal.
                if (!IsVisible) return;

                // Charge / Discharge Wattage & Status Display
                bool en = LocalizationService.CurrentLanguage == AppLanguage.English;

                if (state.IsAcOnline)
                {
                    // AC adapter input cannot be measured: Windows exposes no API
                    // for it and it is vendor-EC territory. It stays a sum of the
                    // charge rate and the hardware estimate, marked with a tilde.
                    TxtLiveDischargeRate.Text = $"~{state.AcTotalInputW:F1} W";
                    TxtLiveDischargeRate.Foreground = BrushEmerald; // Emerald Green

                    BadgeLiveAcState.Background = BrushEmeraldBadge;
                    BadgeLiveAcState.BorderBrush = BrushEmerald;
                    TxtLiveAcState.Foreground = BrushEmerald;

                    if (state.IsCharging)
                    {
                        string chg = state.IsChargeRateMeasured
                            ? (en ? $"Charging +{state.ChargingRateW:F1}W measured"
                                  : $"電池充電 +{state.ChargingRateW:F1}W 實測")
                            : (en ? "Charging +--W" : "電池充電 +--W");

                        TxtLiveAcState.Text = en
                            ? $"🔌 AC Input ~{state.AcTotalInputW:F1}W estimated ({chg} | Hardware ~{state.TotalSystemHardwareW:F1}W)"
                            : $"🔌 AC 變壓器總供電 ~{state.AcTotalInputW:F1}W 推估 ({chg} | 硬體耗電 ~{state.TotalSystemHardwareW:F1}W)";
                    }
                    else
                    {
                        TxtLiveAcState.Text = en
                            ? $"🔌 AC Input ~{state.AcTotalInputW:F1}W estimated (Direct Pass-Through | Battery not charging)"
                            : $"🔌 AC 變壓器總供電 ~{state.AcTotalInputW:F1}W 推估 (市電直供硬體 | 電池未充電)";
                    }
                }
                else
                {
                    // On battery the pack itself reports the draw, so this is the
                    // one headline figure that is a genuine whole-system
                    // measurement rather than a sum of estimates.
                    TxtLiveDischargeRate.Text = state.IsDischargeRateMeasured
                        ? $"-{state.DischargeRateW:F1} W"
                        : $"~-{state.DischargeRateW:F1} W";
                    TxtLiveDischargeRate.Foreground = BrushAmber; // Amber

                    BadgeLiveAcState.Background = BrushAmberBadge;
                    BadgeLiveAcState.BorderBrush = BrushAmber;
                    TxtLiveAcState.Foreground = BrushAmber;

                    if (state.IsDischargeRateMeasured)
                    {
                        TxtLiveAcState.Text = en
                            ? $"🔋 Battery Discharging (-{state.DischargeRateW:F1}W measured at the pack — whole system)"
                            : $"🔋 電池放電中 (-{state.DischargeRateW:F1}W 電池實測 — 全系統真實耗電)";
                    }
                    else
                    {
                        TxtLiveAcState.Text = en
                            ? $"🔋 Battery Discharging (~-{state.DischargeRateW:F1}W estimated)"
                            : $"🔋 電池放電中 (~-{state.DischargeRateW:F1}W 推估)";
                    }
                }

                // Battery Remaining Time & Level
                TxtLiveRemainingTime.Text = state.EstimatedTimeRemainingText;
                TxtLiveBatteryPercent.Text = LocalizationService.CurrentLanguage == AppLanguage.English
                    ? $"Current Battery Level: {state.BatteryPercent}%"
                    : $"目前電池剩餘電量: {state.BatteryPercent}%";

                // Battery Hardware Telemetry (Voltage / Current)
                TxtLiveBatteryTelemetry.Text = state.BatteryTelemetryText;
                TxtHwTelemetryVal.Text = state.BatteryTelemetryText;
                TxtHwPowerPlanVal.Text = state.PowerPlanName;

                // Per-component rows below report utilisation, throughput and
                // brightness — all really measured. The only per-component
                // wattage that exists is the dGPU's, read over NVML.

                // CPU load
                PbCpuUsage.Value = state.CpuUsagePercent;
                TxtCpuUsageVal.Text = $"{state.CpuUsagePercent:F1}%";

                // Discrete GPU (dGPU) load and real package power
                TxtDgpuName.Text = $"🎮 {state.DgpuName}";
                PbDgpuUsage.Value = state.DgpuUsagePercent;
                TxtDgpuUsageVal.Text = state.DgpuStatusText;
                TxtDgpuPowerW.Text = state.IsDgpuPowerMeasured
                    ? $"{state.DgpuPowerW:F1} W"
                    : "-- W";

                // Integrated GPU (iGPU) load
                TxtIgpuName.Text = $"🖼️ {state.IgpuName}";
                PbIgpuUsage.Value = state.IgpuUsagePercent;
                TxtIgpuUsageVal.Text = $"{state.IgpuUsagePercent:F1}%";

                // Screen brightness. Panels that do not expose
                // WmiMonitorBrightness show "--" rather than a fallback number.
                PbScreenBrightness.Value = state.ScreenBrightnessPercent;
                if (state.IsBrightnessMeasured)
                {
                    TxtScreenBrightnessVal.Text = LocalizationService.CurrentLanguage == AppLanguage.English
                        ? $"{state.ScreenBrightnessPercent}% Brightness"
                        : $"{state.ScreenBrightnessPercent}% 亮度";
                }
                else
                {
                    TxtScreenBrightnessVal.Text = LocalizationService.CurrentLanguage == AppLanguage.English
                        ? "-- (not reported)"
                        : "-- (無法讀取)";
                }

                // Wi-Fi throughput
                TxtWifiSpeedVal.Text = LocalizationService.CurrentLanguage == AppLanguage.English
                    ? $"Network Traffic: {state.WifiThroughputKbps:N0} KB/s"
                    : $"即時傳輸流量: {state.WifiThroughputKbps:N0} KB/s";

                // Disk load and throughput
                PbDiskUsage.Value = state.DiskUsagePercent;
                TxtDiskUsageVal.Text = $"{state.DiskUsagePercent:F1}%";
                TxtDiskStatusText.Text = $"{state.DiskReadWriteMbps:F1} MB/s";

                // RAM usage
                PbRamUsage.Value = state.RamUsagePercent;
                TxtRamUsageVal.Text = $"{state.RamUsageGB:F1} GB / {state.TotalRamGB:F1} GB ({state.RamUsagePercent:F1}%)";

                // Power Load Rating
                TxtPowerLoadStatus.Text = state.SystemPowerLoadStatus;

                // Live tips. Every wattage quoted here is a real reading; when
                // nothing real is available the tip says so rather than
                // inventing a figure.
                string dgpuPart = state.IsDgpuPowerMeasured
                    ? (en ? $" Discrete GPU {state.DgpuPowerW:F1} W." : $" 獨顯實測 {state.DgpuPowerW:F1} W。")
                    : string.Empty;

                if (state.IsAcOnline)
                {
                    if (state.IsCharging && state.IsChargeRateMeasured)
                    {
                        TxtLivePowerTip.Text = en
                            ? $"🔌 On AC. Battery charging at {state.ChargingRateW:F1} W (measured at the pack). {state.EstimatedTimeRemainingText}.{dgpuPart}"
                            : $"🔌 市電供電中，電池充電功率 {state.ChargingRateW:F1} W（電池實測）。{state.EstimatedTimeRemainingText}。{dgpuPart}";
                    }
                    else
                    {
                        TxtLivePowerTip.Text = en
                            ? $"🔌 On AC, no current flowing to or from the battery — there is no measurable system wattage in this state. Adapter input is not exposed by Windows.{dgpuPart}"
                            : $"🔌 市電直供中，電池無充放電電流，此狀態下沒有可量測的系統功率（變壓器輸入功率 Windows 並未提供）。{dgpuPart}";
                    }
                }
                else if (state.IsDischargeRateMeasured)
                {
                    if (state.DischargeRateW > 20.0 || state.DgpuUsagePercent > 40.0)
                    {
                        TxtLivePowerTip.Text = en
                            ? $"⚠️ High draw: {state.DischargeRateW:F1} W measured at the battery — the whole machine.{dgpuPart} Lowering screen brightness (now {state.ScreenBrightnessPercent}%) extends runtime."
                            : $"⚠️ 目前放電 {state.DischargeRateW:F1} W（電池實測，為整機真實耗電）。{dgpuPart}建議調低螢幕亮度（目前 {state.ScreenBrightnessPercent}%）以延長續航。";
                    }
                    else
                    {
                        TxtLivePowerTip.Text = en
                            ? $"💡 On battery: {state.DischargeRateW:F1} W measured at the pack — the whole machine's real draw.{dgpuPart}"
                            : $"💡 電池供電中，實測放電 {state.DischargeRateW:F1} W（量測自電池，即整機真實耗電）。{dgpuPart}";
                    }
                }
                else
                {
                    TxtLivePowerTip.Text = en
                        ? $"💡 On battery. This machine's battery does not report a discharge rate, so no wattage can be shown.{dgpuPart}"
                        : $"💡 電池供電中。此裝置的電池未回報放電功率，因此無法顯示瓦數。{dgpuPart}";
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Live monitor tick error: {ex.Message}");
            }
        }

        private void BtnExportPowerCsv_Click(object sender, RoutedEventArgs e)
        {
            var saveDialog = new SaveFileDialog
            {
                Title = LocalizationService.CurrentLanguage == AppLanguage.English ? "Export Live Power History Logs" : "匯出即時功耗與充放電歷史日誌",
                FileName = $"WinBat_Power_Log_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
                Filter = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*"
            };

            if (saveDialog.ShowDialog() == true)
            {
                bool success = RealTimePowerHistoryService.ExportToCsv(saveDialog.FileName);
                if (success)
                {
                    MessageBox.Show(LocalizationService.CurrentLanguage == AppLanguage.English ? $"Successfully exported to:\n{saveDialog.FileName}" : $"已成功匯出歷史日誌至:\n{saveDialog.FileName}", 
                        LocalizationService.CurrentLanguage == AppLanguage.English ? "Export Success" : "匯出成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(LocalizationService.CurrentLanguage == AppLanguage.English ? "Export failed due to write permissions." : "匯出失敗，請確認檔案寫入權限。", 
                        LocalizationService.CurrentLanguage == AppLanguage.English ? "Error" : "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnClearPowerHistory_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(LocalizationService.CurrentLanguage == AppLanguage.English ? "Clear all live power and event history logs?" : "確定要清除所有即時功耗與充放電事件紀錄嗎？", 
                LocalizationService.CurrentLanguage == AppLanguage.English ? "Confirm Clear" : "確認清除", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                RealTimePowerHistoryService.ClearHistory();
            }
        }

        private async void BtnGenerateReport_Click(object sender, RoutedEventArgs e)
        {
            await RunBatteryCheckAsync();
        }

        private async Task RunBatteryCheckAsync()
        {
            OverlayLoading.Visibility = Visibility.Visible;

            try
            {
                var result = await PowerCfgService.GenerateReportAsync();
                OverlayLoading.Visibility = Visibility.Collapsed;

                if (result.Success && !string.IsNullOrWhiteSpace(result.HtmlContent))
                {
                    // The report HTML can exceed 1 MB and parsing is regex
                    // heavy — keep it off the UI thread.
                    var parsed = await Task.Run(() => BatteryReportParser.Parse(result.HtmlContent));
                    DisplayReport(parsed);
                }
                else
                {
                    MessageBox.Show(result.ErrorMessage, "電池檢測失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                OverlayLoading.Visibility = Visibility.Collapsed;
                MessageBox.Show($"發生意外錯誤: {ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnOpenReport_Click(object sender, RoutedEventArgs e)
        {
            var openDialog = new OpenFileDialog
            {
                Title = "選擇 Windows Battery Report (battery-report.html)",
                Filter = "HTML Files (*.html;*.htm)|*.html;*.htm|All Files (*.*)|*.*"
            };

            if (openDialog.ShowDialog() == true)
            {
                try
                {
                    string html = File.ReadAllText(openDialog.FileName);
                    var parsed = BatteryReportParser.Parse(html);
                    DisplayReport(parsed);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"無法讀取檔案: {ex.Message}", "讀取錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnExportData_Click(object sender, RoutedEventArgs e)
        {
            if (_currentReport == null)
            {
                MessageBox.Show(LocalizationService.CurrentLanguage == AppLanguage.English ? "No battery report data available to export." : "目前尚無可供匯出的電池資料。", 
                    LocalizationService.CurrentLanguage == AppLanguage.English ? "Info" : "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var saveDialog = new SaveFileDialog
            {
                Title = LocalizationService.CurrentLanguage == AppLanguage.English ? "Export Battery Summary Report" : "匯出電池健康度摘要報告",
                FileName = $"WinBat_Lens_Summary_{DateTime.Now:yyyyMMdd}.json",
                Filter = "JSON Data (*.json)|*.json|All Files (*.*)|*.*"
            };

            if (saveDialog.ShowDialog() == true)
            {
                try
                {
                    string json = JsonSerializer.Serialize(_currentReport, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(saveDialog.FileName, json);
                    MessageBox.Show(LocalizationService.CurrentLanguage == AppLanguage.English ? $"Successfully exported to:\n{saveDialog.FileName}" : $"已成功匯出至:\n{saveDialog.FileName}", 
                        LocalizationService.CurrentLanguage == AppLanguage.English ? "Export Success" : "匯出成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"匯出失敗: {ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void DisplayReport(BatteryReportData report)
        {
            _currentReport = report;
            var metrics = report.HealthMetrics;
            var specs = report.BatterySpecs;

            bool isEn = LocalizationService.CurrentLanguage == AppLanguage.English;
            string naText = isEn ? "N/A" : "未提供";
            string dash = "—";

            // Score & Badge
            TxtHealthPercent.Text = metrics.HasBattery ? metrics.HealthPercent.ToString("F1") : dash;
            TxtHealthPercentSign.Visibility = metrics.HasBattery ? Visibility.Visible : Visibility.Collapsed;
            TxtStatusLabel.Text = metrics.StatusLabel;
            TxtSummary.Text = metrics.SummaryText;

            // Specs Grid
            TxtSpecName.Text = specs.Name;
            TxtSpecMfg.Text = specs.Manufacturer;
            TxtSpecChem.Text = specs.Chemistry;
            TxtSpecDesign.Text = specs.DesignCapacity > 0 ? $"{specs.DesignCapacity:N0} {specs.Unit}" : dash;
            TxtSpecFull.Text = specs.FullChargeCapacity > 0 ? $"{specs.FullChargeCapacity:N0} {specs.Unit}" : dash;
            TxtSpecLoss.Text = metrics.HasBattery ? $"{metrics.CapacityLoss:N0} {specs.Unit} ({metrics.WearPercent}%)" : dash;
            TxtSpecCycles.Text = specs.CycleCount.HasValue ? $"{specs.CycleCount.Value} 次" : naText;

            TxtReportTime.Text = string.IsNullOrWhiteSpace(report.SystemInfo.ReportTime) 
                ? $"{DateTime.Now:yyyy-MM-dd HH:mm}" 
                : report.SystemInfo.ReportTime;

            // Bind Lists
            LvCapacityHistory.ItemsSource = report.CapacityHistory;
            IcDiagnostics.ItemsSource = report.Diagnostics;
            LvLifeEstimates.ItemsSource = report.BatteryLifeEstimates;
            LvRecentUsage.ItemsSource = report.RecentUsage;
        }
    }
}
