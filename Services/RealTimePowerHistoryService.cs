using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using WinBatLens.Models;

namespace WinBatLens.Services
{
    /// <summary>
    /// 提供即時功耗與插拔電事件歷史紀錄之管理、UI 綁定集合 (ObservableCollection) 與 CSV 匯出服務。
    /// </summary>
    public class RealTimePowerHistoryService
    {
        private static readonly ObservableCollection<PowerHistoryRecord> _records = new ObservableCollection<PowerHistoryRecord>();
        private static bool? _lastAcStatus = null;
        private static DateTime _lastSampleTime = DateTime.MinValue;

        /// <summary>最多保留之歷史事件紀錄筆數上限。</summary>
        private const int MAX_RECORDS = 500;

        /// <summary>可供 UI DataGrid 綁定之歷史紀錄集合。</summary>
        public static ObservableCollection<PowerHistoryRecord> Records => _records;

        /// <summary>
        /// 根據 1Hz 即時電源狀態，判定插拔電事件或定時（5 秒）記錄歷史採樣。
        /// </summary>
        /// <param name="state">目前即時電源狀態。</param>
        public static void AddRecordFromPowerState(RealTimePowerState state)
        {
            DateTime now = DateTime.Now;
            bool isAc = state.IsAcOnline;

            // 1. 偵測 AC 市電插拔狀態切換事件
            if (_lastAcStatus.HasValue && _lastAcStatus.Value != isAc)
            {
                string eventTitle = isAc ? "🔌 連接 AC 市電 (開始充電)" : "🔋 拔除電源 (開始電池放電)";
                string summary = isAc 
                    ? $"連接 AC 電源 | 目前電量: {state.BatteryPercent}%" 
                    : $"切換至電池供電 | 初始放電功率: {state.DischargeRateW:F1} W | 目前電量: {state.BatteryPercent}%";

                AddRecord(new PowerHistoryRecord
                {
                    TimestampText = now.ToString("yyyy-MM-dd HH:mm:ss"),
                    EventType = isAc ? "AC市電" : "電池放電",
                    EventBadgeClass = isAc ? "Success" : "Warning",
                    DischargeRateW = state.DischargeRateW,
                    BatteryPercent = state.BatteryPercent,
                    CpuUsagePercent = state.CpuUsagePercent,
                    DgpuUsagePercent = state.DgpuUsagePercent,
                    DgpuPowerW = state.IsDgpuPowerMeasured ? state.DgpuPowerW : 0.0,
                    BatteryVoltageV = state.BatteryVoltageV,
                    BatteryCurrentA = state.BatteryCurrentA,
                    SummaryText = summary
                });
            }

            _lastAcStatus = isAc;

            // 2. 定時週期採樣（每 5 秒一次）
            if ((now - _lastSampleTime).TotalSeconds >= 5.0)
            {
                _lastSampleTime = now;

                string eventType = isAc ? "AC 供電" : "電池放電";
                string badgeClass = isAc ? "Success" : "Info";

                if (!isAc && state.DischargeRateW > 25.0)
                {
                    eventType = "⚠️ 高耗電警示";
                    badgeClass = "Danger";
                }
                else if (state.IsChargerDeficit)
                {
                    eventType = "⚠️ 外接電源不足";
                    badgeClass = "Danger";
                }
                else if (state.DgpuUsagePercent > 30.0)
                {
                    eventType = "🎮 獨顯運算";
                    badgeClass = "Warning";
                }

                string summary = state.IsChargerDeficit
                    ? $"外接電源供電不足，電池補上 {state.DischargeRateW:F1}W | {state.BatteryTelemetryText} | CPU: {state.CpuUsagePercent:F0}% | 獨顯: {state.DgpuStatusText}"
                    : isAc
                        ? $"市電正常 | 電壓: {state.BatteryVoltageV:F2}V | CPU: {state.CpuUsagePercent:F0}% | 螢幕亮度: {state.ScreenBrightnessPercent}%"
                        : $"放電中 ({state.DischargeRateW:F1}W) | {state.BatteryTelemetryText} | CPU: {state.CpuUsagePercent:F0}% | 獨顯: {state.DgpuStatusText}";

                AddRecord(new PowerHistoryRecord
                {
                    TimestampText = now.ToString("yyyy-MM-dd HH:mm:ss"),
                    EventType = eventType,
                    EventBadgeClass = badgeClass,
                    DischargeRateW = state.DischargeRateW,
                    BatteryPercent = state.BatteryPercent,
                    CpuUsagePercent = state.CpuUsagePercent,
                    DgpuUsagePercent = state.DgpuUsagePercent,
                    DgpuPowerW = state.IsDgpuPowerMeasured ? state.DgpuPowerW : 0.0,
                    BatteryVoltageV = state.BatteryVoltageV,
                    BatteryCurrentA = state.BatteryCurrentA,
                    SummaryText = summary
                });
            }
        }

        /// <summary>
        /// 跨執行緒安全插入紀錄至 ObservableCollection。
        /// </summary>
        private static void AddRecord(PowerHistoryRecord record)
        {
            var dispatcher = App.Current?.Dispatcher;
            if (dispatcher == null) return;

            if (dispatcher.CheckAccess()) Insert(record);
            else dispatcher.Invoke(() => Insert(record));
        }

        private static void Insert(PowerHistoryRecord record)
        {
            _records.Insert(0, record);
            while (_records.Count > MAX_RECORDS)
            {
                _records.RemoveAt(_records.Count - 1);
            }
        }

        /// <summary>
        /// 清空所有歷史紀錄。
        /// </summary>
        public static void ClearHistory()
        {
            var dispatcher = App.Current?.Dispatcher;
            if (dispatcher == null) return;

            if (dispatcher.CheckAccess()) _records.Clear();
            else dispatcher.Invoke(() => _records.Clear());
        }

        /// <summary>
        /// 符合 RFC 4180 標準之 CSV 欄位跳脫轉義。
        /// </summary>
        private static string Csv(string? value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
        }

        /// <summary>
        /// 將目前歷史紀錄匯出為 UTF-8 CSV 檔案。
        /// </summary>
        /// <param name="filePath">匯出檔案路徑。</param>
        /// <returns>匯出成功傳回 true，失敗傳回 false。</returns>
        public static bool ExportToCsv(string filePath)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("時間戳記,事件類型,電池放電功率(W),目前電量(%),電壓(V),電流(A),CPU負載(%),獨顯負載(%),獨顯功耗(W),詳細狀態");

                foreach (var r in _records)
                {
                    sb.AppendLine($"{Csv(r.TimestampText)},{Csv(r.EventType)},{r.DischargeRateW},{r.BatteryPercent},{r.BatteryVoltageV},{r.BatteryCurrentA},{r.CpuUsagePercent},{r.DgpuUsagePercent},{r.DgpuPowerW},{Csv(r.SummaryText)}");
                }

                File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExportToCsv error: {ex.Message}");
                return false;
            }
        }
    }
}
