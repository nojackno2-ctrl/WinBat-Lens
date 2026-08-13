using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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
    /// <summary>
    /// WinBat Lens 主儀表板視窗，負責電池報告解析展示、1Hz 即時功耗圖表渲染、系統托盤常駐與多國語言切換。
    /// </summary>
    public partial class MainWindow : Window
    {
        private BatteryReportData? _currentReport;
        private CancellationTokenSource? _livePowerCts;
        private Task? _livePowerTask;
        private RealTimePowerState? _latestPowerState;
        private bool _isTrayMode;
        private readonly bool _startInTray = Environment.GetCommandLineArgs()
            .Any(argument => string.Equals(argument, StartupService.BackgroundArgument, StringComparison.OrdinalIgnoreCase));

        // Re-trimming the working set is not free: it is a blocking gen2
        // collection followed by a page-out, so doing it on a fixed timer meant
        // a CPU spike every minute for the rest of the session whether or not
        // anything had accumulated. It is now driven by actual heap growth, and
        // the counter only rate-limits how often that growth is checked.
        private const int TrimCheckIntervalSamples = 12;   // ~60 s at the 5 s tray tick
        private const long TrimGrowthBytes = 8L * 1024 * 1024;
        private int _hiddenTickCount;
        private long _managedBytesAtLastTrim;
        private NotifyIcon? _notifyIcon;
        private bool _isExitRequested = false;
        private bool _trayBalloonShown = false;
        private string? _lastTrayTooltip;

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

        // Written in place on every redraw and handed to the Polylines exactly
        // once — see EnsureChartPointCapacity.
        private readonly PointCollection _dischargePoints = new PointCollection(MAX_CHART_POINTS);
        private readonly PointCollection _chargePoints = new PointCollection(MAX_CHART_POINTS);
        private readonly PointCollection _gpuPoints = new PointCollection(MAX_CHART_POINTS);
        private double _lastAxisScaleW = -1.0;
        private static readonly SolidColorBrush BrushVoltage = CreateFrozen(MediaColor.FromRgb(0x38, 0xBD, 0xF8));

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
            TxtAppVersion.Text = AppInfo.DisplayVersion;

            // Answer duplicate launches. This is deliberately in the constructor
            // rather than in Loaded: a second copy can put its version prompt on
            // screen within a few hundred ms of this process starting, and if the
            // listener is not up by the time the user answers it, the handover
            // degrades to Process.Kill() and strands our tray icon. Both callbacks
            // arrive on a thread-pool thread, so they hop onto the dispatcher
            // before touching the window.
            SingleInstanceService.StartListening(
                onActivate: () => Dispatcher.BeginInvoke(new Action(RestoreFromTray)),
                onExitRequested: () => Dispatcher.BeginInvoke(new Action(ExitApplication)));

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
            BatteryVoltageHistoryService.Load();
            LvBatteryVoltageHistory.ItemsSource = BatteryVoltageHistoryService.Points;

            // Initialize System Tray Icon & AutoStart State
            InitSystemTrayIcon();
            InitAutoStartState();

            // Windows startup passes --background so the monitor can begin in
            // the tray without briefly showing the dashboard.
            if (_startInTray)
            {
                HideToTray(showBalloon: false);
            }

            // Draw Background Gridlines
            DrawChartGridlines();
            RedrawBatteryVoltageChart();

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
            if (_latestPowerState != null) UpdateLivePowerUI(_latestPowerState, recordSample: false);
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
            TabVoltageHistory.Header = LocalizationService.Get("TabVoltageHistory");
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

            TxtVoltageHistoryTitle.Text = LocalizationService.Get("VoltageHistoryTitle");
            TxtVoltageHistoryHint.Text = LocalizationService.Get("VoltageHistoryHint");
            BtnClearVoltageHistory.Content = LocalizationService.Get("BtnClearVoltageHistory");
            VoltagePercentColumn.Header = LocalizationService.Get("VoltagePercentColumn");
            VoltageAverageColumn.Header = LocalizationService.Get("VoltageAverageColumn");
            VoltageRangeColumn.Header = LocalizationService.Get("VoltageRangeColumn");
            VoltageSamplesColumn.Header = LocalizationService.Get("VoltageEventsColumn");
            VoltageLastColumn.Header = LocalizationService.Get("VoltageLastColumn");

            TxtHardwareTitle.Text = LocalizationService.Get("HardwareTitle");
            LblHwBatteryTelemetry.Text = LocalizationService.Get("HwBatteryTelemetry");
            LblHwEnergy.Text = LocalizationService.Get("HwEnergy");
            LblHwPowerPlan.Text = LocalizationService.Get("HwPowerPlan");
            TxtHwTelemetrySource.Text = LocalizationService.Get("HwTelemetrySource");
            TxtHwPowerPlanState.Text = LocalizationService.Get("HwPowerPlanState");

            TxtHistoryLogHeader.Text = LocalizationService.Get("HistoryLogHeader");
            BtnExportPowerCsv.Content = LocalizationService.Get("BtnExportCsv");
            BtnClearPowerHistory.Content = LocalizationService.Get("BtnClearHistory");
            TxtPowerHistoryEmpty.Text = LocalizationService.Get("PowerHistoryEmpty");
            TxtVoltageHistoryEmpty.Text = LocalizationService.Get("VoltageHistoryEmpty");
            TxtDiagnosticsEmpty.Text = LocalizationService.Get("DiagnosticsEmpty");
            TxtLifeEstimatesEmpty.Text = LocalizationService.Get("LifeEstimatesEmpty");
            TxtRecentUsageEmpty.Text = LocalizationService.Get("RecentUsageEmpty");
            TxtLoadingTitle.Text = LocalizationService.Get("LoadingTitle");
            TxtLoadingDetail.Text = LocalizationService.Get("LoadingDetail");

            // System tray menu follows the same language as the window.
            if (_trayItemShow != null) _trayItemShow.Text = LocalizationService.Get("TrayShow");
            if (_trayItemCheck != null) _trayItemCheck.Text = LocalizationService.Get("TrayCheck");
            if (_trayItemAutoStart != null) _trayItemAutoStart.Text = LocalizationService.Get("TrayAutoStart");
            if (_trayItemExit != null) _trayItemExit.Text = LocalizationService.Get("TrayExit");
            RedrawBatteryVoltageChart();
        }

        private void GridVoltageChartContainer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            RedrawBatteryVoltageChart();
        }

        /// <summary>
        /// Draws the voltage curve against a fixed 0–100% horizontal axis.
        /// Missing percentages are left as gaps rather than being interpolated
        /// into values that the battery driver never reported.
        /// </summary>
        private void RedrawBatteryVoltageChart()
        {
            try
            {
                int pointCount = BatteryVoltageHistoryService.RecordedPercentCount;
                int eventCount = BatteryVoltageHistoryService.TotalSampleCount;
                TxtVoltageHistorySummary.Text = string.Format(
                    LocalizationService.Get("VoltageHistorySummary"),
                    pointCount,
                    eventCount);

                CanvasVoltageGridlines.Children.Clear();
                CanvasVoltageLines.Children.Clear();
                CanvasVoltageMarkers.Children.Clear();

                double width = GridVoltageChartContainer.ActualWidth;
                double height = GridVoltageChartContainer.ActualHeight;
                if (width <= 0 || height <= 0 || pointCount == 0)
                {
                    SetVoltageAxisLabels(null, null);
                    return;
                }

                var points = BatteryVoltageHistoryService.Points;
                double measuredMin = points.Min(point => point.AverageVoltageV);
                double measuredMax = points.Max(point => point.AverageVoltageV);
                double voltageSpan = Math.Max(1.0, measuredMax - measuredMin);
                double padding = Math.Max(0.2, voltageSpan * 0.1);
                double axisMin = Math.Floor((measuredMin - padding) * 10.0) / 10.0;
                double axisMax = Math.Ceiling((measuredMax + padding) * 10.0) / 10.0;

                if (axisMax - axisMin < 1.0)
                {
                    double center = (measuredMin + measuredMax) / 2.0;
                    axisMin = Math.Floor((center - 0.5) * 10.0) / 10.0;
                    axisMax = axisMin + 1.0;
                }

                const double plotLeft = 44.0;
                const double plotRight = 8.0;
                double plotWidth = Math.Max(1.0, width - plotLeft - plotRight);

                DrawVoltageChartGridlines(plotLeft, plotWidth, width, height);
                SetVoltageAxisLabels(axisMin, axisMax);

                double axisRange = axisMax - axisMin;
                Polyline? currentSegment = null;
                int previousPercent = -2;

                foreach (var item in points)
                {
                    double x = plotLeft + (item.BatteryPercent / 100.0 * plotWidth);
                    double normalized = (item.AverageVoltageV - axisMin) / axisRange;
                    double y = height - Math.Clamp(normalized, 0.0, 1.0) * height;

                    // Only adjacent percentage events share a segment. A gap
                    // in the curve therefore remains visibly unmeasured.
                    if (item.BatteryPercent != previousPercent + 1)
                    {
                        currentSegment = new Polyline
                        {
                            Stroke = BrushVoltage,
                            StrokeThickness = 2.5,
                        };
                        currentSegment.StrokeLineJoin = PenLineJoin.Round;
                        CanvasVoltageLines.Children.Add(currentSegment);
                    }

                    currentSegment!.Points.Add(new WpfPoint(x, y));

                    var marker = new System.Windows.Shapes.Ellipse
                    {
                        Width = 6,
                        Height = 6,
                        Fill = BrushVoltage,
                        Stroke = BrushVoltage,
                        StrokeThickness = 1
                    };
                    System.Windows.Controls.Canvas.SetLeft(marker, x - 3);
                    System.Windows.Controls.Canvas.SetTop(marker, y - 3);
                    CanvasVoltageMarkers.Children.Add(marker);
                    previousPercent = item.BatteryPercent;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Voltage chart redraw error: {ex.Message}");
            }
        }

        /// <summary>
        /// Draws the voltage chart's background grid. The vertical divisions are
        /// placed across the plot area rather than the whole container, so a
        /// gridline sits exactly under the percentage it labels.
        /// </summary>
        private void DrawVoltageChartGridlines(double plotLeft, double plotWidth, double width, double height)
        {
            for (int i = 1; i <= 3; i++)
            {
                double y = (height / 4.0) * i;
                CanvasVoltageGridlines.Children.Add(new Line
                {
                    X1 = 0,
                    Y1 = y,
                    X2 = width,
                    Y2 = y,
                    Stroke = BrushGridStrong,
                    StrokeThickness = 1,
                    StrokeDashArray = DashStrong
                });
            }

            // 0% and 100% included: the plot area is inset from the container,
            // so its own edges are not otherwise visible.
            for (int i = 0; i <= 10; i++)
            {
                double x = plotLeft + (plotWidth / 10.0) * i;
                CanvasVoltageGridlines.Children.Add(new Line
                {
                    X1 = x,
                    Y1 = 0,
                    X2 = x,
                    Y2 = height,
                    Stroke = BrushGridFaint,
                    StrokeThickness = 1,
                    StrokeDashArray = DashFaint
                });
            }
        }

        private void SetVoltageAxisLabels(double? axisMin, double? axisMax)
        {
            if (!axisMin.HasValue || !axisMax.HasValue)
            {
                TxtVoltageYAxis100.Text = "-- V";
                TxtVoltageYAxis75.Text = "-- V";
                TxtVoltageYAxis50.Text = "-- V";
                TxtVoltageYAxis25.Text = "-- V";
                TxtVoltageYAxis0.Text = "-- V";
                return;
            }

            double range = axisMax.Value - axisMin.Value;
            TxtVoltageYAxis100.Text = $"{axisMax.Value:F2} V";
            TxtVoltageYAxis75.Text = $"{(axisMin.Value + range * 0.75):F2} V";
            TxtVoltageYAxis50.Text = $"{(axisMin.Value + range * 0.50):F2} V";
            TxtVoltageYAxis25.Text = $"{(axisMin.Value + range * 0.25):F2} V";
            TxtVoltageYAxis0.Text = $"{axisMin.Value:F2} V";
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

                // The three collections are built once and then written in
                // place. Handing the Polylines three brand-new PointCollections
                // a second meant re-allocating 180 points and three collection
                // objects every tick, and made WPF drop and re-adopt the old
                // ones each time; the redraw itself costs the same either way.
                EnsureChartPointCapacity(_chartHistory.Count);

                // Iterate the queue directly (oldest→newest) instead of
                // materialising a List and running a LINQ Max each tick.
                //
                // All three wattage traces share one scale so their heights can
                // be compared directly. For example, 20 W of dGPU power is one
                // quarter of an 80 W battery discharge, not half the chart.
                double powerPeak = 0.0;
                foreach (var item in _chartHistory)
                {
                    double samplePeak = Math.Max(
                        Math.Max(item.DischargeW, item.ChargeW),
                        item.GpuW);
                    if (samplePeak > powerPeak) powerPeak = samplePeak;
                }

                double maxPowerW = Math.Max(35.0, powerPeak * 1.15);

                // The axis only ever shows whole watts, so it is rewritten when
                // the rounded scale changes rather than once a second. Each of
                // these assignments is a text change that WPF answers with a
                // measure and arrange pass.
                double roundedScale = Math.Round(maxPowerW);
                if (roundedScale != _lastAxisScaleW)
                {
                    _lastAxisScaleW = roundedScale;
                    TxtYAxis100.Text = $"{maxPowerW:F0} W (100%)";
                    TxtYAxis75.Text = $"{(maxPowerW * 0.75):F0} W (75%)";
                    TxtYAxis50.Text = $"{(maxPowerW * 0.50):F0} W (50%)";
                    TxtYAxis25.Text = $"{(maxPowerW * 0.25):F0} W (25%)";
                    TxtYAxis0.Text = "0 W (0%)";
                }

                int i = 0;
                foreach (var item in _chartHistory)
                {
                    double x = (i / (double)(MAX_CHART_POINTS - 1)) * w;

                    // Y values (0 at bottom, Height at top). Every series uses
                    // maxPowerW so equal wattages always land at equal heights.
                    double yDischarge = h - Math.Min(h, Math.Max(0, (item.DischargeW / maxPowerW) * h));
                    double yCharge = h - Math.Min(h, Math.Max(0, (item.ChargeW / maxPowerW) * h));
                    double yGpu = h - Math.Min(h, Math.Max(0, (item.GpuW / maxPowerW) * h));

                    _dischargePoints[i] = new WpfPoint(x, yDischarge);
                    _chargePoints[i] = new WpfPoint(x, yCharge);
                    _gpuPoints[i] = new WpfPoint(x, yGpu);
                    i++;
                }
            }
            catch { }
        }

        /// <summary>
        /// Grows the three shared PointCollections to <paramref name="count"/>
        /// entries and attaches them to their Polylines the first time round.
        /// The history only ever grows to <see cref="MAX_CHART_POINTS"/> and
        /// never shrinks, so after the first minute this does nothing at all.
        /// </summary>
        private void EnsureChartPointCapacity(int count)
        {
            if (_dischargePoints.Count == count) return;

            bool firstTime = _dischargePoints.Count == 0;

            while (_dischargePoints.Count < count)
            {
                _dischargePoints.Add(default);
                _chargePoints.Add(default);
                _gpuPoints.Add(default);
            }

            // A cleared history (or a shorter one) must not leave stale points
            // trailing off the right-hand edge.
            while (_dischargePoints.Count > count)
            {
                int last = _dischargePoints.Count - 1;
                _dischargePoints.RemoveAt(last);
                _chargePoints.RemoveAt(last);
                _gpuPoints.RemoveAt(last);
            }

            if (firstTime)
            {
                PolylineDischarge.Points = _dischargePoints;
                PolylineCharge.Points = _chargePoints;
                PolylineGpu.Points = _gpuPoints;
            }
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
                                try
                                {
                                    using var temporaryIcon = System.Drawing.Icon.FromHandle(hIcon);
                                    _notifyIcon.Icon = (System.Drawing.Icon)temporaryIcon.Clone();
                                }
                                finally
                                {
                                    DestroyIcon(hIcon);
                                }
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

                _trayItemCheck = new ToolStripMenuItem(LocalizationService.Get("TrayCheck"), null, async (s, args) =>
                {
                    RestoreFromTray();
                    await RunBatteryCheckAsync();
                });

                _trayItemAutoStart = new ToolStripMenuItem(LocalizationService.Get("TrayAutoStart"));
                _trayItemAutoStart.Checked = StartupService.IsAutoStartEnabled();
                _trayItemAutoStart.Click += (s, args) =>
                {
                    bool newState = !_trayItemAutoStart!.Checked;
                    if (StartupService.SetAutoStart(newState))
                    {
                        _trayItemAutoStart.Checked = newState;
                        ChkAutoStart.IsChecked = newState;
                    }
                };

                _trayItemExit = new ToolStripMenuItem(LocalizationService.Get("TrayExit"), null, (s, args) => ExitApplication());

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
            // Window, so stop the polling loop and release sensors/tray here.
            _livePowerCts?.Cancel();
            BatteryVoltageHistoryService.Flush();
            try { HardwareSensorService.Shutdown(); } catch { }
            try { BatteryTelemetryService.Shutdown(); } catch { }
            try { PowerSupplyService.Shutdown(); } catch { }
            try
            {
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                    _notifyIcon = null;
                }
            }
            catch { }
            DynamicTrayIconService.Dispose();
        }

        [DllImport("psapi.dll")]
        private static extern int EmptyWorkingSet(IntPtr hProcess);

        // The pseudo-handle for the current process: no allocation and nothing
        // to release. Process.GetCurrentProcess() was allocating a Process
        // object and an OS handle on every trim, and never disposing either.
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr handle);

        private void HideToTray(bool showBalloon = true)
        {
            Volatile.Write(ref _isTrayMode, true);
            this.Hide();

            // Nothing on screen consumes the sensor sweep any more, and the
            // monitoring loop itself drops to 5 s, so the sensors follow.
            HardwareSensorService.SetIdleMode(true);

            // Only explain the tray behaviour the first time; after that the
            // balloon is just noise on every minimize.
            if (showBalloon && _notifyIcon != null && !_trayBalloonShown)
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

        private void TrimWorkingSet()
        {
            try
            {
                // Compacting is worth the extra time here specifically: the
                // window's visual tree has just been torn down, so this is the
                // one moment when the heap has large holes to close up before
                // the pages are handed back.
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                EmptyWorkingSet(GetCurrentProcess());
                _managedBytesAtLastTrim = GC.GetTotalMemory(false);
                _hiddenTickCount = 0;
            }
            catch { }
        }

        /// <summary>
        /// The one real exit path, shared by the tray menu and by a newer
        /// instance asking this one to stand down. Dropping the tray icon here
        /// matters: if the process dies without it, the icon lingers in the
        /// notification area until the user happens to hover over it.
        /// </summary>
        private void ExitApplication()
        {
            _isExitRequested = true;

            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
            DynamicTrayIconService.Dispose();

            Application.Current.Shutdown();
        }

        private void RestoreFromTray()
        {
            Volatile.Write(ref _isTrayMode, false);
            HardwareSensorService.SetIdleMode(false);
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();

            // Activate() alone is at the mercy of the foreground lock: when the
            // request comes from a duplicate launch that was not itself the
            // foreground process, Windows refuses the focus change and only
            // flashes the taskbar button. Flicking Topmost brings the window out
            // on top anyway, without leaving it pinned above everything else.
            this.Topmost = true;
            this.Topmost = false;
            this.Focus();

            // Visual updates are skipped while hidden; render the latest cached
            // snapshot immediately, then the background loop resumes at 1 Hz.
            if (_latestPowerState != null) UpdateLivePowerUI(_latestPowerState, recordSample: false);
        }

        private void MainWindow_Unloaded(object sender, RoutedEventArgs e)
        {
            _livePowerCts?.Cancel();
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
            DynamicTrayIconService.Dispose();
        }

        private void StartLivePowerMonitoring()
        {
            if (_livePowerTask is { IsCompleted: false }) return;

            _livePowerCts = new CancellationTokenSource();
            _livePowerTask = Task.Run(() => PollLivePowerAsync(_livePowerCts.Token));
        }

        private async Task PollLivePowerAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // All hardware, WMI and performance-counter reads happen
                    // here, off the WPF dispatcher. The UI receives only the
                    // completed immutable-by-convention snapshot.
                    var state = RealTimePowerService.GetCurrentPowerState();
                    await Dispatcher.InvokeAsync(
                        () => UpdateLivePowerUI(state),
                        DispatcherPriority.Background,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Live power polling error: {ex.Message}");
                }

                int intervalMs = Volatile.Read(ref _isTrayMode) ? 5_000 : 1_000;
                try
                {
                    await Task.Delay(intervalMs, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
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

        private void UpdateLivePowerUI(RealTimePowerState state, bool recordSample = true)
        {
            try
            {
                _latestPowerState = state;

                if (recordSample)
                {
                    // Add to Power & Battery Event History Service
                    RealTimePowerHistoryService.AddRecordFromPowerState(state);

                    // Voltage history is event-based: a second-by-second poll
                    // at the same battery percentage is intentionally ignored.
                    bool voltageEventRecorded = false;
                    if (state.IsVoltageMeasured)
                    {
                        voltageEventRecorded = BatteryVoltageHistoryService.RecordOnPercentChange(
                            state.BatteryPercent,
                            state.BatteryVoltageV,
                            DateTime.Now);
                    }
                    if (voltageEventRecorded)
                        RedrawBatteryVoltageChart();

                    // Update 60-Second Waveform Chart
                    UpdateWaveformChart(state);
                }

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

                    // Assigning NotifyIcon.Text is a Shell_NotifyIcon call into
                    // the shell, not a field write, so it is only made when the
                    // tooltip would actually read differently. The wattage is
                    // shown to one decimal and often repeats between ticks.
                    string tooltip = $"WinBat Lens - {state.PowerStatusText}\nLevel: {state.BatteryPercent}% | {powerStatusStr}";
                    if (!string.Equals(tooltip, _lastTrayTooltip, StringComparison.Ordinal))
                    {
                        _lastTrayTooltip = tooltip;
                        _notifyIcon.Text = tooltip;
                    }
                }

                // While hidden in the tray only the icon, tooltip and history
                // need refreshing — skip every visual control update so the
                // background working set and per-tick allocations stay minimal.
                if (!IsVisible)
                {
                    if (++_hiddenTickCount >= TrimCheckIntervalSamples)
                    {
                        _hiddenTickCount = 0;

                        // Only pay for a trim when there is something to
                        // reclaim. A hidden tick allocates very little now, so
                        // most of these checks find nothing and cost one cheap
                        // read instead of a full blocking collection.
                        if (GC.GetTotalMemory(false) - _managedBytesAtLastTrim >= TrimGrowthBytes)
                        {
                            TrimWorkingSet();
                        }
                    }
                    return;
                }

                _hiddenTickCount = 0;

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
                        ? $"Charger cannot keep up. Battery covering -{state.DischargeRateW:F1}W (measured at the pack)"
                        : $"外接電源供電不足。電池補上 -{state.DischargeRateW:F1}W（電池實測）";
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
                            ? $"Charging the battery at +{state.ChargingRateW:F1}W (measured at the pack)"
                            : $"電池充電中 +{state.ChargingRateW:F1}W（電池實測）";
                    }
                    else if (state.IsCharging)
                    {
                        TxtLiveAcState.Text = en
                            ? "Charging. This battery does not report a charge rate"
                            : "電池充電中。此電池未回報充電功率";
                    }
                    else
                    {
                        TxtLiveAcState.Text = en
                            ? "On AC, battery idle. No current in or out, so there is nothing to measure"
                            : "市電直供中，電池無充放電電流。此狀態下沒有可量測的功率";
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
                            ? $"Battery Discharging (-{state.DischargeRateW:F1}W measured at the pack, whole system)"
                            : $"電池放電中 (-{state.DischargeRateW:F1}W 電池實測，為全系統真實耗電)";
                    }
                    else
                    {
                        TxtLiveAcState.Text = en
                            ? $"Battery Discharging (~-{state.DischargeRateW:F1}W estimated)"
                            : $"電池放電中 (~-{state.DischargeRateW:F1}W 推估)";
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
                            ? "Windows reports the external supply as inadequate for this system"
                            : "Windows 判定：外接電源供電能力不足以支撐目前的系統負載";
                        TxtLiveChargerSupply.Foreground = BrushAmber;
                        TxtLiveChargerSupply.Visibility = Visibility.Visible;
                        break;

                    case PowerSupplyCapability.Adequate:
                        TxtLiveChargerSupply.Text = en
                            ? "Windows reports the external supply as adequate. The adapter's own wattage is not exposed by Windows."
                            : "Windows 判定：外接電源供電充足（變壓器 / USB-C 充電器本身的瓦數，Windows 並未提供）";
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

                SolidColorBrush flowBrush = state.IsChargerDeficit ? BrushRose
                    : state.IsCharging ? BrushEmerald
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
                            ? $"Full charge {state.BatteryCapacityHealthText}, health {state.DriverHealthPercent:F1}%"
                            : $"滿電 {state.BatteryCapacityHealthText}，健康度 {state.DriverHealthPercent:F1}%")
                        : (en ? "Measured by the battery driver" : "電池驅動實測容量");
                }
                else
                {
                    RowHwEnergy.Visibility = Visibility.Collapsed;
                }

                // The discrete GPU is the only component with a real power
                // sensor, so it is the only component row left on this page.
                TxtDgpuName.Text = state.DgpuName;
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
                        ? $"Plugged in but still discharging: the charger is short by at least {state.DischargeRateW:F1} W, which the battery is covering.{dgpuPart} This is common with USB-C charging. A PD charger rated below this machine's draw cannot sustain it. Use a higher-wattage charger or reduce load to charge while working."
                        : $"已接上外接電源，但電池仍在放電：充電器至少差 {state.DischargeRateW:F1} W，缺口由電池補上。{dgpuPart}這是 USB-C 充電常見的情況。PD 充電器的瓦數低於本機耗電就無法維持供電。請改用瓦數更高的充電器，或降低負載才能邊用邊充。";
                }
                else if (state.IsAcOnline)
                {
                    if (state.IsCharging && state.IsChargeRateMeasured)
                    {
                        TxtLivePowerTip.Text = en
                            ? $"On AC. Battery charging at {state.ChargingRateW:F1} W (measured at the pack). {state.EstimatedTimeRemainingText}.{dgpuPart}"
                            : $"市電供電中，電池充電功率 {state.ChargingRateW:F1} W（電池實測）。{state.EstimatedTimeRemainingText}。{dgpuPart}";
                    }
                    else
                    {
                        TxtLivePowerTip.Text = en
                            ? $"On AC, no current is flowing to or from the battery. There is no measurable system wattage in this state. Adapter input is not exposed by Windows.{dgpuPart}"
                            : $"市電直供中，電池無充放電電流，此狀態下沒有可量測的系統功率（變壓器輸入功率 Windows 並未提供）。{dgpuPart}";
                    }
                }
                else if (state.IsDischargeRateMeasured)
                {
                    if (state.DischargeRateW > 20.0 || state.DgpuUsagePercent > 40.0)
                    {
                        TxtLivePowerTip.Text = en
                            ? $"High draw: {state.DischargeRateW:F1} W measured at the battery for the whole machine.{dgpuPart} Lowering screen brightness (now {state.ScreenBrightnessPercent}%) extends runtime."
                            : $"目前放電 {state.DischargeRateW:F1} W（電池實測，為整機真實耗電）。{dgpuPart}建議調低螢幕亮度（目前 {state.ScreenBrightnessPercent}%）以延長續航。";
                    }
                    else
                    {
                        TxtLivePowerTip.Text = en
                            ? $"On battery: {state.DischargeRateW:F1} W measured at the pack, the whole machine's real draw.{dgpuPart}"
                            : $"電池供電中，實測放電 {state.DischargeRateW:F1} W（量測自電池，即整機真實耗電）。{dgpuPart}";
                    }
                }
                else
                {
                    TxtLivePowerTip.Text = en
                        ? $"On battery. This machine's battery does not report a discharge rate, so no wattage can be shown.{dgpuPart}"
                        : $"電池供電中。此裝置的電池未回報放電功率，因此無法顯示瓦數。{dgpuPart}";
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

        private void BtnClearVoltageHistory_Click(object sender, RoutedEventArgs e)
        {
            bool en = LocalizationService.CurrentLanguage == AppLanguage.English;
            var result = MessageBox.Show(
                en
                    ? "Clear all battery voltage-by-percentage history?"
                    : "要清除所有電量百分比與電壓紀錄嗎？",
                en ? "Confirm Clear" : "確認清除",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                BatteryVoltageHistoryService.Clear();
                RedrawBatteryVoltageChart();
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
            string dash = "-";

            // Score & Badge
            bool healthMeasured = metrics.HasBattery && metrics.IsHealthMeasured;
            TxtHealthPercent.Text = healthMeasured ? metrics.HealthPercent.ToString("F1") : dash;
            TxtHealthPercentSign.Visibility = healthMeasured ? Visibility.Visible : Visibility.Collapsed;
            TxtStatusLabel.Text = metrics.StatusLabel;
            TxtSummary.Text = metrics.SummaryText;

            // Grade the health card by colour. The ring used to be painted a
            // fixed green and, with a full-circle dash offset, never drew at
            // all — so a pack at 73.6% looked exactly like one at 100%.
            if (healthMeasured)
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
            TxtSpecLoss.Text = healthMeasured ? $"{metrics.CapacityLoss:N0} {specs.Unit} ({metrics.WearPercent}%)" : dash;
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
