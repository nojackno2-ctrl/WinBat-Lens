using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        private NotifyIcon? _notifyIcon;
        private bool _isExitRequested = false;

        private readonly Queue<(double DischargeW, double ChargeW, double CpuPct, double GpuPct)> _chartHistory = 
            new Queue<(double, double, double, double)>();
        private const int MAX_CHART_POINTS = 60;

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
            // Apply Initial Language
            ApplyLanguage();

            // Bind Power History List
            LvPowerHistory.ItemsSource = RealTimePowerHistoryService.Records;

            // Initialize System Tray Icon & AutoStart State
            InitSystemTrayIcon();
            InitAutoStartState();

            // Bind GPU Specs List
            LoadGpuSpecs();

            // Draw Background Gridlines
            DrawChartGridlines();

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
            GvcColPeriod.Header = LocalizationService.Get("ColPeriod");
            GvcColFullCap.Header = LocalizationService.Get("ColFullCap");
            GvcColDesignCap.Header = LocalizationService.Get("ColDesignCap");
            GvcColHealthPct.Header = LocalizationService.Get("ColHealthPct");

            // Tabs
            TabRealTime.Header = LocalizationService.Get("TabRealTime");
            TabHistoryLogs.Header = LocalizationService.Get("TabHistoryLogs");
            TabDiagnostics.Header = LocalizationService.Get("TabDiagnostics");
            TabLifeEstimates.Header = LocalizationService.Get("TabLifeEstimates");
            TabRecentUsage.Header = LocalizationService.Get("TabRecentUsage");

            TxtCardTotalPower.Text = LocalizationService.Get("CardTotalPower");
            TxtCardEstTime.Text = LocalizationService.Get("CardEstTime");
            TxtWaveformTitle.Text = LocalizationService.Get("WaveformTitle");
            TxtLegendDischarge.Text = LocalizationService.Get("LegendDischarge");
            TxtLegendCharge.Text = LocalizationService.Get("LegendCharge");
            TxtLegendCpu.Text = LocalizationService.Get("LegendCpu");
            TxtLegendGpu.Text = LocalizationService.Get("LegendGpu");

            TxtHardwareTitle.Text = LocalizationService.Get("HardwareTitle");
            LblHwCpu.Text = LocalizationService.Get("HwCpu");
            LblHwScreen.Text = LocalizationService.Get("HwScreen");
            LblHwWifi.Text = LocalizationService.Get("HwWifi");
            LblHwDisk.Text = LocalizationService.Get("HwDisk");
            LblHwRam.Text = LocalizationService.Get("HwRam");
            LblHwMotherboard.Text = LocalizationService.Get("HwMotherboard");
            TxtGpuListTitle.Text = LocalizationService.Get("GpuListTitle");

            TxtHistoryLogHeader.Text = LocalizationService.Get("HistoryLogHeader");
            BtnExportPowerCsv.Content = LocalizationService.Get("BtnExportCsv");
            BtnClearPowerHistory.Content = LocalizationService.Get("BtnClearHistory");
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
                        Stroke = new SolidColorBrush(MediaColor.FromArgb(0x20, 0x94, 0xA3, 0xB8)),
                        StrokeThickness = 1,
                        StrokeDashArray = new DoubleCollection { 4, 4 }
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
                        Stroke = new SolidColorBrush(MediaColor.FromArgb(0x15, 0x94, 0xA3, 0xB8)),
                        StrokeThickness = 1,
                        StrokeDashArray = new DoubleCollection { 2, 4 }
                    };
                    CanvasGridlines.Children.Add(line);
                }
            }
            catch { }
        }

        private void UpdateWaveformChart(RealTimePowerState state)
        {
            double disW = state.IsAcOnline ? 0.0 : state.DischargeRateW;
            double chgW = state.IsCharging ? state.ChargingRateW : 0.0;

            _chartHistory.Enqueue((disW, chgW, state.CpuUsagePercent, state.DgpuUsagePercent));
            while (_chartHistory.Count > MAX_CHART_POINTS)
            {
                _chartHistory.Dequeue();
            }

            RedrawWaveformChart();
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

                var list = _chartHistory.ToList();
                double maxPowerW = Math.Max(35.0, list.Max(x => Math.Max(x.DischargeW, x.ChargeW)) * 1.15);

                // Update Y-Axis Scale Coordinates Text
                TxtYAxis100.Text = $"{maxPowerW:F0} W (100%)";
                TxtYAxis75.Text = $"{(maxPowerW * 0.75):F0} W (75%)";
                TxtYAxis50.Text = $"{(maxPowerW * 0.50):F0} W (50%)";
                TxtYAxis25.Text = $"{(maxPowerW * 0.25):F0} W (25%)";
                TxtYAxis0.Text = "0 W (0%)";

                for (int i = 0; i < list.Count; i++)
                {
                    double x = (i / (double)(MAX_CHART_POINTS - 1)) * w;
                    var item = list[i];

                    // Y values (0 at bottom, Height at top)
                    double yDischarge = h - Math.Min(h, Math.Max(0, (item.DischargeW / maxPowerW) * h));
                    double yCharge = h - Math.Min(h, Math.Max(0, (item.ChargeW / maxPowerW) * h));
                    double yCpu = h - Math.Min(h, Math.Max(0, (item.CpuPct / 100.0) * h));
                    double yGpu = h - Math.Min(h, Math.Max(0, (item.GpuPct / 100.0) * h));

                    dischargePoints.Add(new WpfPoint(x, yDischarge));
                    chargePoints.Add(new WpfPoint(x, yCharge));
                    cpuPoints.Add(new WpfPoint(x, yCpu));
                    gpuPoints.Add(new WpfPoint(x, yGpu));
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
                    string pngPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_icon.png");
                    string icoPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_icon.ico");
                    IconHelper.EnsureIcoFile(pngPath, icoPath);
                    _notifyIcon.Icon = IconHelper.GetAppIcon(System.IO.File.Exists(icoPath) ? icoPath : pngPath);
                }

                _notifyIcon.Text = "WinBat Lens - 電池健康度與即時耗電監測";
                _notifyIcon.Visible = true;

                _notifyIcon.DoubleClick += (s, args) => RestoreFromTray();

                // Context Menu for System Tray
                var contextMenu = new ContextMenuStrip();
                
                var itemShow = new ToolStripMenuItem("⚡ 開啟 WinBat Lens 主畫面", null, (s, args) => RestoreFromTray());
                itemShow.Font = new System.Drawing.Font(itemShow.Font, System.Drawing.FontStyle.Bold);

                var itemCheck = new ToolStripMenuItem("🔄 執行電池健康檢測", null, async (s, args) => {
                    RestoreFromTray();
                    await RunBatteryCheckAsync();
                });

                var itemAutoStart = new ToolStripMenuItem("🚀 開機自動啟動");
                itemAutoStart.Checked = StartupService.IsAutoStartEnabled();
                itemAutoStart.Click += (s, args) => {
                    bool newState = !itemAutoStart.Checked;
                    if (StartupService.SetAutoStart(newState))
                    {
                        itemAutoStart.Checked = newState;
                        ChkAutoStart.IsChecked = newState;
                    }
                };

                var itemExit = new ToolStripMenuItem("❌ 結束程式", null, (s, args) => {
                    _isExitRequested = true;
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                    Application.Current.Shutdown();
                });

                contextMenu.Items.Add(itemShow);
                contextMenu.Items.Add(itemCheck);
                contextMenu.Items.Add(new ToolStripSeparator());
                contextMenu.Items.Add(itemAutoStart);
                contextMenu.Items.Add(new ToolStripSeparator());
                contextMenu.Items.Add(itemExit);

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
            }
        }

        private void HideToTray()
        {
            this.Hide();
            if (_notifyIcon != null)
            {
                _notifyIcon.ShowBalloonTip(2000, "WinBat Lens 已縮小至托盤", "程式將在背景持續為您進行即時耗電與電池狀態監測。", ToolTipIcon.Info);
            }
        }

        private void RestoreFromTray()
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
        }

        private void LoadGpuSpecs()
        {
            try
            {
                var gpus = GpuInfoService.GetInstalledGpus();
                IcGpuList.ItemsSource = gpus;
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

                // Charge / Discharge Wattage & Status Display
                if (state.IsAcOnline)
                {
                    TxtLiveDischargeRate.Text = $"{state.AcTotalInputW:F1} W";
                    TxtLiveDischargeRate.Foreground = new SolidColorBrush(MediaColor.FromRgb(0x10, 0xB9, 0x81)); // Emerald Green

                    BadgeLiveAcState.Background = new SolidColorBrush(MediaColor.FromArgb(0x20, 0x10, 0xB9, 0x81));
                    BadgeLiveAcState.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x10, 0xB9, 0x81));
                    TxtLiveAcState.Foreground = new SolidColorBrush(MediaColor.FromRgb(0x10, 0xB9, 0x81));

                    if (state.IsCharging)
                    {
                        TxtLiveAcState.Text = LocalizationService.CurrentLanguage == AppLanguage.English
                            ? $"🔌 AC Adapter Input {state.AcTotalInputW:F1}W (Charging +{state.ChargingRateW:F1}W | Hardware {state.TotalSystemHardwareW:F1}W)"
                            : $"🔌 AC 變壓器總供電 {state.AcTotalInputW:F1}W (電池充電 +{state.ChargingRateW:F1}W | 硬體耗電 {state.TotalSystemHardwareW:F1}W)";
                    }
                    else
                    {
                        TxtLiveAcState.Text = LocalizationService.CurrentLanguage == AppLanguage.English
                            ? $"🔌 AC Adapter Input {state.AcTotalInputW:F1}W (Direct Pass-Through | 100% Fully Charged Protection)"
                            : $"🔌 AC 變壓器總供電 {state.AcTotalInputW:F1}W (市電直供硬體 | 電池 100% 滿電保護中)";
                    }
                }
                else
                {
                    TxtLiveDischargeRate.Text = $"-{state.DischargeRateW:F1} W";
                    TxtLiveDischargeRate.Foreground = new SolidColorBrush(MediaColor.FromRgb(0xF5, 0x9E, 0x0B)); // Amber

                    BadgeLiveAcState.Background = new SolidColorBrush(MediaColor.FromArgb(0x20, 0xF5, 0x9E, 0x0B));
                    BadgeLiveAcState.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0xF5, 0x9E, 0x0B));
                    TxtLiveAcState.Foreground = new SolidColorBrush(MediaColor.FromRgb(0xF5, 0x9E, 0x0B));
                    TxtLiveAcState.Text = LocalizationService.CurrentLanguage == AppLanguage.English
                        ? $"🔋 Battery Discharging (-{state.DischargeRateW:F1}W)"
                        : $"🔋 電池放電中 (-{state.DischargeRateW:F1}W)";
                }

                // Battery Remaining Time & Level
                TxtLiveRemainingTime.Text = state.EstimatedTimeRemainingText;
                TxtLiveBatteryPercent.Text = LocalizationService.CurrentLanguage == AppLanguage.English
                    ? $"Current Battery Level: {state.BatteryPercent}%"
                    : $"目前電池剩餘電量: {state.BatteryPercent}%";

                // Update System Tray Tooltip
                if (_notifyIcon != null)
                {
                    string powerStatusStr = state.IsAcOnline ? $"AC Input: {state.AcTotalInputW:F1}W" : $"-{state.DischargeRateW:F1}W Discharging";
                    _notifyIcon.Text = $"WinBat Lens - {state.PowerStatusText}\nLevel: {state.BatteryPercent}% | {powerStatusStr}";
                }

                // CPU Load & Power
                PbCpuUsage.Value = state.CpuUsagePercent;
                TxtCpuUsageVal.Text = $"{state.CpuUsagePercent:F1}%";
                TxtCpuPowerW.Text = $"~{state.CpuPowerW:F1} W";

                // Discrete GPU (dGPU) Load & Power
                TxtDgpuName.Text = $"🎮 {state.DgpuName}";
                PbDgpuUsage.Value = state.DgpuUsagePercent;
                TxtDgpuUsageVal.Text = state.DgpuStatusText;
                TxtDgpuPowerW.Text = $"~{state.DgpuPowerW:F1} W";

                // Integrated GPU (iGPU) Load & Power
                TxtIgpuName.Text = $"🖼️ {state.IgpuName}";
                PbIgpuUsage.Value = state.IgpuUsagePercent;
                TxtIgpuUsageVal.Text = $"{state.IgpuUsagePercent:F1}%";
                TxtIgpuPowerW.Text = $"~{state.IgpuPowerW:F1} W";

                // Screen Display & Backlight Power
                PbScreenBrightness.Value = state.ScreenBrightnessPercent;
                TxtScreenBrightnessVal.Text = LocalizationService.CurrentLanguage == AppLanguage.English
                    ? $"{state.ScreenBrightnessPercent}% Brightness"
                    : $"{state.ScreenBrightnessPercent}% 亮度";
                TxtScreenPowerW.Text = $"~{state.ScreenPowerW:F1} W";

                // Wi-Fi Wireless Power
                TxtWifiSpeedVal.Text = LocalizationService.CurrentLanguage == AppLanguage.English
                    ? $"Network Traffic: {state.WifiThroughputKbps:N0} KB/s"
                    : $"即時傳輸流量: {state.WifiThroughputKbps:N0} KB/s";
                TxtWifiPowerW.Text = $"~{state.WifiPowerW:F1} W";

                // Disk Load & Power
                PbDiskUsage.Value = state.DiskUsagePercent;
                TxtDiskUsageVal.Text = $"{state.DiskUsagePercent:F1}%";
                TxtDiskStatusText.Text = $"{state.DiskReadWriteMbps:F1} MB/s";
                TxtDiskPowerW.Text = $"~{state.DiskPowerW:F1} W";

                // RAM Usage & Bus Power
                PbRamUsage.Value = state.RamUsagePercent;
                TxtRamUsageVal.Text = $"{state.RamUsageGB:F1} GB / {state.TotalRamGB:F1} GB ({state.RamUsagePercent:F1}%)";
                TxtRamPowerW.Text = $"~{state.RamPowerW:F1} W";

                // Motherboard Base Power
                TxtMotherboardPowerW.Text = $"~{state.MotherboardPowerW:F1} W";

                // Power Load Rating
                TxtPowerLoadStatus.Text = state.SystemPowerLoadStatus;

                // Live Dynamic Tips
                if (state.IsAcOnline)
                {
                    if (state.IsCharging)
                    {
                        TxtLivePowerTip.Text = LocalizationService.CurrentLanguage == AppLanguage.English
                            ? $"🔌 AC Adapter Input: {state.AcTotalInputW:F1} W (Battery Charging +{state.ChargingRateW:F1} W, Hardware Power {state.TotalSystemHardwareW:F1} W). {state.EstimatedTimeRemainingText}."
                            : $"🔌 AC 變壓器目前總供電 {state.AcTotalInputW:F1} W（包含電池充電 +{state.ChargingRateW:F1} W 與全系統硬體運作耗電 {state.TotalSystemHardwareW:F1} W）。{state.EstimatedTimeRemainingText}。";
                    }
                    else
                    {
                        TxtLivePowerTip.Text = LocalizationService.CurrentLanguage == AppLanguage.English
                            ? $"💡 Battery fully charged (100%). AC Adapter input: {state.AcTotalInputW:F1} W directly powering hardware."
                            : $"💡 電池已充滿 (100%)。目前 AC 變壓器總供電 {state.AcTotalInputW:F1} W (市電直接供給全系統硬體運做)，已自動啟用滿電過充保護。";
                    }
                }
                else
                {
                    if (state.DischargeRateW > 20.0 || state.DgpuUsagePercent > 40.0)
                    {
                        TxtLivePowerTip.Text = LocalizationService.CurrentLanguage == AppLanguage.English
                            ? $"⚠️ High discharge rate: {state.DischargeRateW:F1} W. Discrete GPU is active (~{state.DgpuPowerW:F1}W)."
                            : $"⚠️ 注意：目前放電速率高達 {state.DischargeRateW:F1} W。獨立顯卡正進行高負載渲染 (功耗 ~{state.DgpuPowerW:F1}W)，建議調低螢幕亮度 (目前 {state.ScreenBrightnessPercent}%) 以延長續航。";
                    }
                    else
                    {
                        TxtLivePowerTip.Text = LocalizationService.CurrentLanguage == AppLanguage.English
                            ? $"💡 Discharging on battery power (~{state.DischargeRateW:F1} W total)."
                            : $"💡 目前正使用電池放電中，系統總功率約 {state.DischargeRateW:F1} W (CPU ~{state.CpuPowerW:F1}W | 獨顯 ~{state.DgpuPowerW:F1}W | 螢幕 ~{state.ScreenPowerW:F1}W)，功耗控制良好。";
                    }
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
                    var parsed = BatteryReportParser.Parse(result.HtmlContent);
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

            // Score & Badge
            TxtHealthPercent.Text = metrics.HealthPercent.ToString("F1");
            TxtStatusLabel.Text = metrics.StatusLabel;
            TxtSummary.Text = metrics.SummaryText;

            // Specs Grid
            TxtSpecName.Text = specs.Name;
            TxtSpecMfg.Text = specs.Manufacturer;
            TxtSpecChem.Text = specs.Chemistry;
            TxtSpecDesign.Text = $"{specs.DesignCapacity:N0} {specs.Unit}";
            TxtSpecFull.Text = $"{specs.FullChargeCapacity:N0} {specs.Unit}";
            TxtSpecLoss.Text = $"{metrics.CapacityLoss:N0} {specs.Unit} ({metrics.WearPercent}%)";
            TxtSpecCycles.Text = specs.CycleCount.HasValue ? $"{specs.CycleCount.Value} 次" : (LocalizationService.CurrentLanguage == AppLanguage.English ? "N/A" : "未提供");

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
