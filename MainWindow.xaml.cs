using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using WinBatLens.Models;
using WinBatLens.Services;

namespace WinBatLens
{
    public partial class MainWindow : Window
    {
        private BatteryReportData? _currentReport;

        public MainWindow()
        {
            InitializeComponent();
            TxtSystemModel.Text = $"{Environment.MachineName} ({Environment.OSVersion}) - C# WPF";
            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await RunBatteryCheckAsync();
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
