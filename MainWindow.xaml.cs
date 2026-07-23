using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

namespace WinBatLens
{
    public partial class MainWindow : Window
    {
        private BatteryReportData? _currentReport;
        private DispatcherTimer? _livePowerTimer;
        private NotifyIcon? _notifyIcon;
        private bool _isExitRequested = false;

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
            // Initialize System Tray Icon & AutoStart State
            InitSystemTrayIcon();
            InitAutoStartState();

            // Bind GPU Specs List
            LoadGpuSpecs();

            // Start live power monitoring timer (1s interval)
            StartLivePowerMonitoring();

            // Run initial battery report scan
            await RunBatteryCheckAsync();
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

                // Discharge Rate & Status
                TxtLiveDischargeRate.Text = state.DischargeRateText;
                TxtLiveAcState.Text = state.PowerStatusText;

                // Status Badge Color
                if (state.IsAcOnline)
                {
                    BadgeLiveAcState.Background = new SolidColorBrush(MediaColor.FromArgb(0x20, 0x38, 0xBD, 0xF8));
                    BadgeLiveAcState.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x38, 0xBD, 0xF8));
                    TxtLiveAcState.Foreground = new SolidColorBrush(MediaColor.FromRgb(0x38, 0xBD, 0xF8));
                }
                else
                {
                    BadgeLiveAcState.Background = new SolidColorBrush(MediaColor.FromArgb(0x20, 0xF5, 0x9E, 0x0B));
                    BadgeLiveAcState.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0xF5, 0x9E, 0x0B));
                    TxtLiveAcState.Foreground = new SolidColorBrush(MediaColor.FromRgb(0xF5, 0x9E, 0x0B));
                }

                // Battery Remaining Time & Level
                TxtLiveRemainingTime.Text = state.EstimatedTimeRemainingText;
                TxtLiveBatteryPercent.Text = $"目前電池剩餘電量: {state.BatteryPercent}%";

                // Update System Tray Tooltip
                if (_notifyIcon != null)
                {
                    _notifyIcon.Text = $"WinBat Lens - {state.PowerStatusText}\n電量: {state.BatteryPercent}% | 放電: {state.DischargeRateW}W";
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
                TxtScreenBrightnessVal.Text = $"{state.ScreenBrightnessPercent}% 亮度";
                TxtScreenPowerW.Text = $"~{state.ScreenPowerW:F1} W";

                // Wi-Fi Wireless Power
                TxtWifiSpeedVal.Text = $"即時網絡流量: {state.WifiThroughputKbps:N0} KB/s";
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
                    TxtLivePowerTip.Text = "💡 目前連接 AC 市電供電中。獨立顯卡與 CPU 效能已完全解鎖，電池無放電損耗。";
                }
                else
                {
                    if (state.DischargeRateW > 20.0 || state.DgpuUsagePercent > 40.0)
                    {
                        TxtLivePowerTip.Text = $"⚠️ 注意：目前放電速率高達 {state.DischargeRateW:F1} W。獨立顯卡正進行高負載渲染 (功耗 ~{state.DgpuPowerW:F1}W)，建議調低螢幕亮度 (目前 {state.ScreenBrightnessPercent}%) 以延長續航。";
                    }
                    else
                    {
                        TxtLivePowerTip.Text = $"💡 目前正使用電池放電中，系統總功率約 {state.DischargeRateW:F1} W (CPU ~{state.CpuPowerW:F1}W | 獨顯 ~{state.DgpuPowerW:F1}W | 螢幕 ~{state.ScreenPowerW:F1}W)，功耗控制良好。";
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Live monitor tick error: {ex.Message}");
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
                MessageBox.Show("目前尚無可供匯出的電池資料。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var saveDialog = new SaveFileDialog
            {
                Title = "匯出電池健康度摘要報告",
                FileName = $"WinBat_Lens_Summary_{DateTime.Now:yyyyMMdd}.json",
                Filter = "JSON Data (*.json)|*.json|All Files (*.*)|*.*"
            };

            if (saveDialog.ShowDialog() == true)
            {
                try
                {
                    string json = JsonSerializer.Serialize(_currentReport, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(saveDialog.FileName, json);
                    MessageBox.Show($"已成功匯出至:\n{saveDialog.FileName}", "匯出成功", MessageBoxButton.OK, MessageBoxImage.Information);
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
            TxtSpecCycles.Text = specs.CycleCount.HasValue ? $"{specs.CycleCount.Value} 次" : "未提供";

            TxtReportTime.Text = string.IsNullOrWhiteSpace(report.SystemInfo.ReportTime) 
                ? $"檢測時間: {DateTime.Now:yyyy-MM-dd HH:mm}" 
                : $"報告時間: {report.SystemInfo.ReportTime}";

            // Bind Lists
            LvCapacityHistory.ItemsSource = report.CapacityHistory;
            IcDiagnostics.ItemsSource = report.Diagnostics;
            LvLifeEstimates.ItemsSource = report.BatteryLifeEstimates;
            LvRecentUsage.ItemsSource = report.RecentUsage;
        }
    }
}
