using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using WinBatLens.Models;

namespace WinBatLens.Services
{
    public class RealTimePowerHistoryService
    {
        private static readonly ObservableCollection<PowerHistoryRecord> _records = new ObservableCollection<PowerHistoryRecord>();
        private static bool? _lastAcStatus = null;
        private static DateTime _lastSampleTime = DateTime.MinValue;
        private const int MAX_RECORDS = 500;

        public static ObservableCollection<PowerHistoryRecord> Records => _records;

        public static void AddRecordFromPowerState(RealTimePowerState state)
        {
            DateTime now = DateTime.Now;
            bool isAc = state.IsAcOnline;

            // 1. Detect AC Plug in / Plug out transition event
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

            // 2. Periodic sample (every 5 seconds)
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
                    // Being plugged in is not the same as being powered. When
                    // the charger is out-run the pack drains anyway, and the
                    // log has to say so — "AC 供電 / 市電正常" alongside a real
                    // non-zero discharge figure would contradict itself.
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

        private static void AddRecord(PowerHistoryRecord record)
        {
            // Callers are already on the UI thread (the monitoring loop hands
            // its snapshot over before touching anything). Dispatcher.Invoke
            // still builds a DispatcherOperation and walks the queue in that
            // case, so the same-thread path is taken directly.
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

        public static void ClearHistory()
        {
            var dispatcher = App.Current?.Dispatcher;
            if (dispatcher == null) return;

            if (dispatcher.CheckAccess()) _records.Clear();
            else dispatcher.Invoke(() => _records.Clear());
        }

        // Quotes a CSV field, doubling any embedded quote per RFC 4180.
        private static string Csv(string? value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
        }

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
