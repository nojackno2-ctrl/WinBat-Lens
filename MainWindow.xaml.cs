using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using WinBatLens.Models;
using WinBatLens.Services;

namespace WinBatLens
{
    public partial class MainWindow : Window
    {
        private BatteryReportData? _currentReport;
        private DispatcherTimer? _livePowerTimer;

        public MainWindow()
        {
            InitializeComponent();
            TxtSystemModel.Text = $"{Environment.MachineName} ({Environment.OSVersion}) - C# WPF";
            Loaded += MainWindow_Loaded;
            Unloaded += MainWindow_Unloaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Start live power monitoring timer (1s interval)
            StartLivePowerMonitoring();

            // Run initial battery report scan
            await RunBatteryCheckAsync();
        }

        private void MainWindow_Unloaded(object sender, RoutedEventArgs e)
        {
            _livePowerTimer?.Stop();
        }

        private void StartLivePowerMonitoring()
        {
            _livePowerTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _livePowerTimer.Tick += LivePowerTimer_Tick;
            _livePowerTimer.Start();

            // Initial immediate update
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
                    BadgeLiveAcState.Background = new SolidColorBrush(Color.FromArgb(0x20, 0x38, 0xBD, 0xF8));
                    BadgeLiveAcState.BorderBrush = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));
                    TxtLiveAcState.Foreground = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));
                }
                else
                {
                    BadgeLiveAcState.Background = new SolidColorBrush(Color.FromArgb(0x20, 0xF5, 0x9E, 0x0B));
                    BadgeLiveAcState.BorderBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
                    TxtLiveAcState.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
                }

                // Battery Remaining Time & Level
                TxtLiveRemainingTime.Text = state.EstimatedTimeRemainingText;
                TxtLiveBatteryPercent.Text = $"目前電池剩餘電量: {state.BatteryPercent}%";

                // CPU & RAM Load
                PbCpuUsage.Value = state.CpuUsagePercent;
                TxtCpuUsageVal.Text = $"{state.CpuUsagePercent:F1}%";

                PbRamUsage.Value = state.RamUsagePercent;
                TxtRamUsageVal.Text = $"{state.RamUsageGB:F1} GB / {state.TotalRamGB:F1} GB ({state.RamUsagePercent:F1}%)";

                // Power Load Rating
                TxtPowerLoadStatus.Text = state.SystemPowerLoadStatus;

                // Live Dynamic Tips
                if (state.IsAcOnline)
                {
                    TxtLivePowerTip.Text = "💡 目前連接 AC 市電供電中。電池未處於放電磨耗狀態，效能已發揮至極限。";
                }
                else
                {
                    if (state.DischargeRateW > 20.0)
                    {
                        TxtLivePowerTip.Text = $"⚠️ 注意：目前電池放電速率高達 {state.DischargeRateW:F1} W (高耗電)，建議調低螢幕亮度或暫停高 CPU 占用軟體。";
                    }
                    else
                    {
                        TxtLivePowerTip.Text = $"💡 目前正使用電池放電中，放電功率約 {state.DischargeRateW:F1} W，耗電控制良好。";
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
