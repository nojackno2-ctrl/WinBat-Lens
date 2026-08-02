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

        // The CPU series is gone from this tuple: its wattage was a formula over
        // utilisation, and CPU package power is unreadable on this hardware.
        private readonly Queue<(double DischargeW, double ChargeW, double GpuW)> _chartHistory =
            new Queue<(double, double, double)>();
        private const int MAX_CHART_POINTS = 60;

        // Frozen, shared brushes reused across timer ticks. UpdateLivePowerUI
        // runs once per second; allocating fresh SolidColorBrush objects every
        // tick created needless GC pressure. Freezing also lets WPF share them
        // across threads without cloning.
        // These are the semantic colours from App.xaml, repeated here because a
        // brush assigned per tick cannot come from a StaticResource lookup.
        // Keep the two in step: green means charging or saving power, red means
        // spending it, blue is the dGPU series, amber is a battery figure that
        // is neither.
        private static readonly SolidColorBrush BrushEmerald = CreateFrozen(MediaColor.FromRgb(0x10, 0xB9, 0x81));
        private static readonly SolidColorBrush BrushEmeraldBadge = CreateFrozen(MediaColor.FromArgb(0x20, 0x10, 0xB9, 0x81));
        private static readonly SolidColorBrush BrushAmber = CreateFrozen(MediaColor.FromRgb(0xF5, 0x9E, 0x0B));
        private static readonly SolidColorBrush BrushAmberBadge = CreateFrozen(MediaColor.FromArgb(0x20, 0xF5, 0x9E, 0x0B));
        private static readonly SolidColorBrush BrushRose = CreateFrozen(MediaColor.FromRgb(0xF4, 0x3F, 0x5E));
        private static readonly SolidColorBrush BrushRoseBadge = CreateFrozen(MediaColor.FromArgb(0x20, 0xF4, 0x3F, 0x5E));
        private static readonly SolidColorBrush BrushSlate = CreateFrozen(MediaColor.FromRgb(0x94, 0xA3, 0xB8));
        private static readonly SolidColorBrush BrushSlateBadge = CreateFrozen(MediaColor.FromArgb(0x20, 0x94, 0xA3, 0xB8));
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

        /// <summary>
        /// The colour for a health percentage. The 80% / 60% boundaries are the
        /// ones BatteryReportParser uses to pick the status wording, so the ring
        /// can never show green next to a label that says the pack is degraded.
        /// </summary>
        private static SolidColorBrush HealthBrush(double percent) =>
            percent < 60.0 ? BrushRose : percent < 80.0 ? BrushAmber : BrushEmerald;

        private static SolidColorBrush HealthBadgeBrush(double percent) =>
            percent < 60.0 ? BrushRoseBadge : percent < 80.0 ? BrushAmberBadge : BrushEmeraldBadge;

        /// <summary>
        /// Green below <paramref name="idleBelow"/>, red at or above
        /// <paramref name="busyAtOrAbove"/>, amber in between — the
        /// saving/spending scale used by the hardware rows.
        /// </summary>
        private static SolidColorBrush LoadBrush(double value, double idleBelow, double busyAtOrAbove) =>
            value < idleBelow ? BrushEmerald : value < busyAtOrAbove ? BrushAmber : BrushRose;

        /// <summary>
        /// Grades the active Windows power plan by what it costs. Matched on
        /// both languages because PowrProf hands back whatever the plan is
        /// named on this machine, and custom OEM plans (ASUS "Performance")
        /// keep the English word even on a Chinese Windows.
        /// </summary>
        private static SolidColorBrush PowerPlanBrush(string? planName)
        {
            if (string.IsNullOrWhiteSpace(planName)) return BrushSlate;

            if (planName.Contains("高效能") || planName.Contains("效能") ||
                planName.Contains("Performance", StringComparison.OrdinalIgnoreCase) ||
                planName.Contains("Ultimate", StringComparison.OrdinalIgnoreCase))
                return BrushRose;

            if (planName.Contains("省電") || planName.Contains("節能") ||
                planName.Contains("Saver", StringComparison.OrdinalIgnoreCase) ||
                planName.Contains("Eco", StringComparison.OrdinalIgnoreCase))
                return BrushEmerald;

            return BrushAmber;
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
            LblSpecMade.Text = LocalizationService.Get("SpecMade");
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
            LblHwEnergy.Text = LocalizationService.Get("HwEnergy");
            LblHwPowerPlan.Text = LocalizationService.Get("HwPowerPlan");

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
            // IsDischargeRateMeasured now also covers being plugged into a
            // charger that cannot keep up, so the shortfall is plotted on the
            // discharge trace where it belongs instead of vanishing.
            double disW = state.IsDischargeRateMeasured ? state.DischargeRateW : 0.0;
            double chgW = (state.IsCharging && state.IsChargeRateMeasured) ? state.ChargingRateW : 0.0;
            double gpuW = state.IsDgpuPowerMeasured ? state.DgpuPowerW : 0.0;

            _chartHistory.Enqueue((disW, chgW, gpuW));
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
                var gpuPoints = new PointCollection();

                // Iterate the queue directly (oldest→newest) instead of
                // materialising a List and running a LINQ Max each tick.
                //
                // Battery and dGPU are scaled separately. A loaded discrete GPU
                // draws an order of magnitude more than the pack — 76 W against
                // a handful of watts — and a shared axis pressed the charge and
                // discharge lines flat onto the floor whenever the GPU woke up.
                double batteryPeak = 0.0;
                double gpuPeak = 0.0;
                foreach (var item in _chartHistory)
                {
                    double b = Math.Max(item.DischargeW, item.ChargeW);
                    if (b > batteryPeak) batteryPeak = b;
                    if (item.GpuW > gpuPeak) gpuPeak = item.GpuW;
                }

                double maxPowerW = Math.Max(35.0, batteryPeak * 1.15);
                double maxGpuW = gpuPeak > 0.0 ? Math.Max(35.0, gpuPeak * 1.15) : 0.0;

                // Update Y-Axis Scale Coordinates Text
                TxtYAxis100.Text = $"{maxPowerW:F0} W (100%)";
                TxtYAxis75.Text = $"{(maxPowerW * 0.75):F0} W (75%)";
                TxtYAxis50.Text = $"{(maxPowerW * 0.50):F0} W (50%)";
                TxtYAxis25.Text = $"{(maxPowerW * 0.25):F0} W (25%)";
                TxtYAxis0.Text = "0 W (0%)";

                // The right-hand scale exists only while a GPU wattage is
                // actually being measured; with none there is nothing for it to
                // label and it would just be a second set of numbers.
                if (maxGpuW > 0.0)
                {
                    GridGpuAxis.Visibility = Visibility.Visible;
                    bool en = LocalizationService.CurrentLanguage == AppLanguage.English;

                    TxtGpuAxis100.Text = en ? $"dGPU {maxGpuW:F0} W" : $"獨顯 {maxGpuW:F0} W";
                    TxtGpuAxis75.Text = $"{(maxGpuW * 0.75):F0} W";
                    TxtGpuAxis50.Text = $"{(maxGpuW * 0.50):F0} W";
                    TxtGpuAxis25.Text = $"{(maxGpuW * 0.25):F0} W";
                    TxtGpuAxis0.Text = "0 W";
                }
                else
                {
                    GridGpuAxis.Visibility = Visibility.Collapsed;
                }

                int i = 0;
                foreach (var item in _chartHistory)
                {
                    double x = (i / (double)(MAX_CHART_POINTS - 1)) * w;

                    // Y values (0 at bottom, Height at top). The GPU series is
                    // plotted against its own maximum, hence the right-hand axis.
                    double yDischarge = h - Math.Min(h, Math.Max(0, (item.DischargeW / maxPowerW) * h));
                    double yCharge = h - Math.Min(h, Math.Max(0, (item.ChargeW / maxPowerW) * h));
                    double yGpu = maxGpuW > 0.0
                        ? h - Math.Min(h, Math.Max(0, (item.GpuW / maxGpuW) * h))
                        : h;

                    dischargePoints.Add(new WpfPoint(x, yDischarge));
                    chargePoints.Add(new WpfPoint(x, yCharge));
                    gpuPoints.Add(new WpfPoint(x, yGpu));
                    i++;
                }

                PolylineDischarge.Points = dischargePoints;
                PolylineCharge.Points = chargePoints;
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
            try { PowerSupplyService.Shutdown(); } catch { }
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

                    string powerStatusStr;
                    if (state.IsCharging && state.IsChargeRateMeasured)
                        powerStatusStr = $"+{state.ChargingRateW:F1}W Charging";
                    else if (state.IsChargerDeficit)
                        powerStatusStr = $"-{state.DischargeRateW:F1}W (charger too weak)";
                    else if (!state.IsAcOnline && state.IsDischargeRateMeasured)
                        powerStatusStr = $"-{state.DischargeRateW:F1}W Discharging";
                    else
                        powerStatusStr = "-- W";

                    _notifyIcon.Text = $"WinBat Lens - {state.PowerStatusText}\nLevel: {state.BatteryPercent}% | {powerStatusStr}";
                }

                // While hidden in the tray only the icon, tooltip and history
                // need refreshing — skip every visual control update so the
                // background working set and per-tick allocations stay minimal.
                if (!IsVisible) return;

                // Charge / Discharge Wattage & Status Display
                bool en = LocalizationService.CurrentLanguage == AppLanguage.English;

                if (state.IsChargerDeficit)
                {
                    // Plugged in and still draining. The shortfall is painted in
                    // the discharge colour because that is exactly what it is —
                    // the machine is spending battery. Green would read as
                    // "charging", which is the opposite of what is happening.
                    TxtLiveDischargeRate.Text = $"-{state.DischargeRateW:F1} W";
                    TxtLiveDischargeRate.Foreground = BrushRose;

                    BadgeLiveAcState.Background = BrushRoseBadge;
                    BadgeLiveAcState.BorderBrush = BrushRose;
                    TxtLiveAcState.Foreground = BrushRose;

                    TxtLiveAcState.Text = en
                        ? $"🔌⚠️ Charger cannot keep up — battery covering -{state.DischargeRateW:F1}W (measured at the pack)"
                        : $"🔌⚠️ 外接電源供電不足 — 電池補上 -{state.DischargeRateW:F1}W（電池實測）";
                }
                else if (state.IsAcOnline)
                {
                    // Charging is a real measurement, so it belongs in the
                    // headline. There is deliberately no adapter-input figure:
                    // Windows exposes no API for it.
                    TxtLiveDischargeRate.Text = state.IsCharging && state.IsChargeRateMeasured
                        ? $"+{state.ChargingRateW:F1} W"
                        : "-- W";
                    TxtLiveDischargeRate.Foreground = BrushEmerald; // Emerald Green

                    BadgeLiveAcState.Background = BrushEmeraldBadge;
                    BadgeLiveAcState.BorderBrush = BrushEmerald;
                    TxtLiveAcState.Foreground = BrushEmerald;

                    if (state.IsCharging && state.IsChargeRateMeasured)
                    {
                        TxtLiveAcState.Text = en
                            ? $"🔌 Charging the battery at +{state.ChargingRateW:F1}W (measured at the pack)"
                            : $"🔌 電池充電中 +{state.ChargingRateW:F1}W（電池實測）";
                    }
                    else if (state.IsCharging)
                    {
                        TxtLiveAcState.Text = en
                            ? "🔌 Charging — this battery does not report a charge rate"
                            : "🔌 電池充電中 — 此電池未回報充電功率";
                    }
                    else
                    {
                        TxtLiveAcState.Text = en
                            ? "🔌 On AC, battery idle — no current in or out, so there is nothing to measure"
                            : "🔌 市電直供中，電池無充放電電流 — 此狀態下沒有可量測的功率";
                    }
                }
                else
                {
                    // On battery the pack itself reports the draw, so this is a
                    // genuine whole-system measurement.
                    TxtLiveDischargeRate.Text = state.IsDischargeRateMeasured
                        ? $"-{state.DischargeRateW:F1} W"
                        : "-- W";
                    TxtLiveDischargeRate.Foreground = BrushRose; // Red: spending power

                    BadgeLiveAcState.Background = BrushRoseBadge;
                    BadgeLiveAcState.BorderBrush = BrushRose;
                    TxtLiveAcState.Foreground = BrushRose;

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

                // Windows' verdict on the charger. It carries no wattage,
                // because Windows exposes none for the adapter — see
                // PowerSupplyService for everything that was tried — but "is
                // the supply keeping up" is the question that actually matters
                // when a laptop is charging over USB-C.
                switch (state.SupplyCapability)
                {
                    case PowerSupplyCapability.Inadequate:
                        // Amber, not red: red means power leaving the pack, and
                        // this line is a warning about the supply, not a rate.
                        TxtLiveChargerSupply.Text = en
                            ? "⚠️ Windows reports the external supply as inadequate for this system"
                            : "⚠️ Windows 判定：外接電源供電能力不足以支撐目前的系統負載";
                        TxtLiveChargerSupply.Foreground = BrushAmber;
                        TxtLiveChargerSupply.Visibility = Visibility.Visible;
                        break;

                    case PowerSupplyCapability.Adequate:
                        TxtLiveChargerSupply.Text = en
                            ? "🔌 Windows reports the external supply as adequate. The adapter's own wattage is not exposed by Windows."
                            : "🔌 Windows 判定：外接電源供電充足（變壓器 / USB-C 充電器本身的瓦數，Windows 並未提供）";
                        TxtLiveChargerSupply.Foreground = BrushSlate;
                        TxtLiveChargerSupply.Visibility = Visibility.Visible;
                        break;

                    default:
                        // NotPresent means running on battery, and Unknown means
                        // the API did not answer. Neither says anything worth a
                        // row, so the line disappears rather than showing filler.
                        TxtLiveChargerSupply.Visibility = Visibility.Collapsed;
                        break;
                }

                // Battery Remaining Time & Level. The watt-hour figure is the
                // pack's own reading, which has real resolution where the
                // Windows percentage is a rounded integer.
                TxtLiveRemainingTime.Text = state.EstimatedTimeRemainingText;

                string energyPart = state.IsEnergyMeasured ? $" · {state.BatteryEnergyText}" : string.Empty;
                TxtLiveBatteryPercent.Text = en
                    ? $"Current Battery Level: {state.BatteryPercent}%{energyPart}"
                    : $"目前電池剩餘電量: {state.BatteryPercent}%{energyPart}";

                // Battery Hardware Telemetry (Voltage / Current). Coloured by
                // which way the current is flowing: out of the pack is red,
                // into it is green, and a pack sitting idle on AC is neither.
                TxtLiveBatteryTelemetry.Text = state.BatteryTelemetryText;
                TxtHwTelemetryVal.Text = state.BatteryTelemetryText;

                SolidColorBrush flowBrush = state.IsCharging ? BrushEmerald
                    : state.IsAcOnline ? BrushAmber
                    : BrushRose;
                TxtLiveBatteryTelemetry.Foreground = flowBrush;
                TxtHwTelemetryVal.Foreground = flowBrush;
                TxtTelemetrySub.Foreground = flowBrush;

                // Power plan, coloured by what it costs rather than by nothing
                // at all: performance spends, saver conserves, balanced sits
                // between the two.
                TxtHwPowerPlanVal.Text = state.PowerPlanName;
                SolidColorBrush planBrush = PowerPlanBrush(state.PowerPlanName);
                TxtHwPowerPlanVal.Foreground = planBrush;
                TxtHwPowerPlanSub.Foreground = planBrush;

                // Pack temperature, asked of the battery driver itself. Shown
                // only where the firmware implements the query; where it does
                // not, the subtitle says so instead of displaying a "--" that
                // looks like a failed read.
                TxtLiveBatteryTelemetrySub.Text = state.IsBatteryTemperatureMeasured
                    ? (en
                        ? $"Pack temperature {state.BatteryTemperatureC:F1} °C (battery driver)"
                        : $"電池溫度 {state.BatteryTemperatureC:F1} °C（電池驅動實測）")
                    : (en
                        ? "Voltage read from the battery driver; this pack reports no temperature"
                        : "電壓讀自電池驅動；此電池未回報溫度");

                // Energy in the pack, and its full-charge capacity against the
                // factory design figure — the live version of the health score.
                if (state.IsEnergyMeasured)
                {
                    RowHwEnergy.Visibility = Visibility.Visible;
                    TxtHwEnergyVal.Text = state.BatteryEnergyText;
                    TxtHwEnergyRight.Text = en
                        ? $"SoC {state.TrueSocPercent:F1}%"
                        : $"真實 SoC {state.TrueSocPercent:F1}%";

                    TxtHwEnergySub.Text = state.DriverHealthPercent > 0
                        ? (en
                            ? $"Full charge {state.BatteryCapacityHealthText} — health {state.DriverHealthPercent:F1}%"
                            : $"滿電 {state.BatteryCapacityHealthText}，健康度 {state.DriverHealthPercent:F1}%")
                        : (en ? "Measured by the battery driver" : "電池驅動實測容量");
                }
                else
                {
                    RowHwEnergy.Visibility = Visibility.Collapsed;
                }

                // The discrete GPU is the only component with a real power
                // sensor, so it is the only component row left on this page.
                TxtDgpuName.Text = $"🎮 {state.DgpuName}";
                PbDgpuUsage.Value = state.DgpuUsagePercent;
                TxtDgpuUsageVal.Text = state.DgpuStatusText;
                TxtDgpuPowerW.Text = state.IsDgpuPowerMeasured
                    ? $"{state.DgpuPowerW:F1} W"
                    : "-- W";

                // The load bar follows the same rule as everything else on this
                // page: green while the card is idling and saving power, red
                // once it is genuinely drawing. Where the card reports real
                // watts those are the better signal — an idle-but-hot dGPU can
                // sit at 0% utilisation and still burn 10 W.
                SolidColorBrush dgpuBrush = state.IsDgpuPowerMeasured
                    ? LoadBrush(state.DgpuPowerW, 5.0, 15.0)
                    : LoadBrush(state.DgpuUsagePercent, 5.0, 40.0);
                PbDgpuUsage.Foreground = dgpuBrush;
                TxtDgpuUsageVal.Foreground = dgpuBrush;

                // Live tips. Every wattage quoted here is a real reading; when
                // nothing real is available the tip says so rather than
                // inventing a figure.
                string dgpuPart = state.IsDgpuPowerMeasured
                    ? (en ? $" Discrete GPU {state.DgpuPowerW:F1} W." : $" 獨顯實測 {state.DgpuPowerW:F1} W。")
                    : string.Empty;

                if (state.IsChargerDeficit)
                {
                    TxtLivePowerTip.Text = en
                        ? $"🔌⚠️ Plugged in but still discharging: the charger is short by at least {state.DischargeRateW:F1} W, which the battery is covering.{dgpuPart} Typical of charging over USB-C — a PD charger rated below this machine's draw cannot hold it up. Use a higher-wattage charger, or reduce load, to charge while working."
                        : $"🔌⚠️ 已接上外接電源，但電池仍在放電：充電器至少差 {state.DischargeRateW:F1} W，缺口由電池補上。{dgpuPart}這是 USB-C 充電最常見的情況 — PD 充電器的瓦數低於本機耗電就撐不住。請改用瓦數更高的充電器，或降低負載才能邊用邊充。";
                }
                else if (state.IsAcOnline)
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
                    // heavy — keep it off the UI thread. The live pack info is
                    // merged in during parsing: the report's capacities are a
                    // snapshot Windows logged earlier, while the driver's are
                    // current, and the driver also carries fields powercfg has
                    // no column for.
                    var parsed = await Task.Run(() =>
                        BatteryReportParser.Parse(result.HtmlContent, BatteryTelemetryService.GetPackInfo()));
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
                    // Parsed on its own, with no live driver data merged in: a
                    // hand-picked report may well come from another machine,
                    // and this machine's pack would then be describing someone
                    // else's battery.
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

            // Grade the health card by colour. The ring used to be painted a
            // fixed green and, with a full-circle dash offset, never drew at
            // all — so a pack at 73.6% looked exactly like one at 100%.
            if (metrics.HasBattery)
            {
                var healthBrush = HealthBrush(metrics.HealthPercent);
                RingHealthProgress.Stroke = healthBrush;
                TxtHealthPercent.Foreground = healthBrush;
                TxtHealthPercentSign.Foreground = healthBrush;
                BadgeStatus.Background = HealthBadgeBrush(metrics.HealthPercent);
                BadgeStatus.BorderBrush = healthBrush;
                TxtStatusLabel.Foreground = healthBrush;

                // Draw the arc as well as colour it. Dash lengths are multiples
                // of the stroke thickness, so the 84px circle measures
                // (π × 84) / 8 ≈ 33 dash units; a dash of that times the health
                // fraction followed by a gap too long to ever repeat leaves
                // exactly one arc. The ellipse is rotated -90° in XAML so the
                // arc starts at the top.
                const double ringUnits = Math.PI * 84.0 / 8.0;
                double fraction = Math.Clamp(metrics.HealthPercent / 100.0, 0.0, 1.0);
                RingHealthProgress.StrokeDashArray = new DoubleCollection { ringUnits * fraction, 1000 };
                RingHealthProgress.Visibility = Visibility.Visible;
            }
            else
            {
                // No battery: no grade to show, so the ring goes away rather
                // than sitting there in a colour that means something.
                RingHealthProgress.Visibility = Visibility.Collapsed;
                TxtHealthPercent.Foreground = BrushSlate;
                TxtHealthPercentSign.Foreground = BrushSlate;
                BadgeStatus.Background = BrushSlateBadge;
                BadgeStatus.BorderBrush = BrushSlate;
                TxtStatusLabel.Foreground = BrushSlate;
            }

            // Specs Grid
            TxtSpecName.Text = specs.Name;
            TxtSpecMfg.Text = specs.Manufacturer;
            TxtSpecChem.Text = specs.Chemistry;
            TxtSpecDesign.Text = specs.DesignCapacity > 0 ? $"{specs.DesignCapacity:N0} {specs.Unit}" : dash;
            TxtSpecFull.Text = specs.FullChargeCapacity > 0 ? $"{specs.FullChargeCapacity:N0} {specs.Unit}" : dash;
            TxtSpecLoss.Text = metrics.HasBattery ? $"{metrics.CapacityLoss:N0} {specs.Unit} ({metrics.WearPercent}%)" : dash;
            TxtSpecCycles.Text = specs.CycleCount.HasValue
                ? (isEn ? $"{specs.CycleCount.Value} cycles" : $"{specs.CycleCount.Value} 次")
                : naText;

            // The manufacture date exists only when the battery driver supplied
            // one, so the row appears and disappears with it rather than
            // occupying the card with a permanent "N/A".
            if (specs.ManufactureDate.HasValue)
            {
                string age = specs.AgeYears.HasValue
                    ? (isEn ? $"  ({specs.AgeYears.Value:F1} yrs old)" : $"（約 {specs.AgeYears.Value:F1} 年）")
                    : string.Empty;

                TxtSpecMade.Text = $"{specs.ManufactureDate.Value:yyyy-MM-dd}{age}";
                RowSpecMade.Visibility = Visibility.Visible;
                SepSpecMade.Visibility = Visibility.Visible;
            }
            else
            {
                RowSpecMade.Visibility = Visibility.Collapsed;
                SepSpecMade.Visibility = Visibility.Collapsed;
            }

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
