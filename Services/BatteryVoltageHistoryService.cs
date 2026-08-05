using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using WinBatLens.Models;

namespace WinBatLens.Services
{
    /// <summary>
    /// 累積並保存「電量百分比 → 電池端電壓」的實測紀錄。
    /// </summary>
    public static class BatteryVoltageHistoryService
    {
        private const int CurrentFileVersion = 1;
        private static readonly ObservableCollection<BatteryVoltageHistoryPoint> _points = new();
        private static readonly string _historyDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinBatLens");
        private static readonly string _historyFilePath = Path.Combine(
            _historyDirectory,
            "battery-voltage-history.json");

        private static bool _loaded;
        private static int? _lastObservedPercent;

        /// <summary>依電量百分比遞增排序的歷史紀錄。</summary>
        public static ReadOnlyObservableCollection<BatteryVoltageHistoryPoint> Points { get; } =
            new ReadOnlyObservableCollection<BatteryVoltageHistoryPoint>(_points);

        /// <summary>目前已記錄的電量百分比區間數。</summary>
        public static int RecordedPercentCount => _points.Count;

        /// <summary>目前累積的硬體實測樣本總數。</summary>
        public static int TotalSampleCount => _points.Sum(point => Math.Max(0, point.SampleCount));

        /// <summary>載入安裝版使用者的持久化電壓紀錄；損毀檔案會被忽略。</summary>
        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;

            try
            {
                if (!File.Exists(_historyFilePath)) return;

                var file = JsonSerializer.Deserialize<HistoryFile>(
                    File.ReadAllText(_historyFilePath));

                if (file?.Version != CurrentFileVersion || file.Points == null) return;

                foreach (var point in file.Points
                    .Where(IsValidPoint)
                    .OrderBy(point => point.BatteryPercent))
                {
                    _points.Add(point);
                }

            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Battery voltage history load failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 只在電量百分比變化時記錄一筆已確認為硬體實測的電壓。相同百分比
        /// 的每秒輪詢會被忽略；不同充放電循環再次經過同一百分比時，才會
        /// 將該次變化加入同一個百分比的彙總資料。
        /// </summary>
        /// <returns>若這次百分比變化已加入紀錄則回傳 true。</returns>
        public static bool RecordOnPercentChange(int batteryPercent, double voltageV, DateTime? recordedAt = null)
        {
            if (batteryPercent is < 0 or > 100 || !double.IsFinite(voltageV) || voltageV <= 0)
                return false;

            Load();
            bool isFirstObservation = !_lastObservedPercent.HasValue;
            if (!HasPercentChanged(_lastObservedPercent, batteryPercent))
                return false;

            DateTime timestamp = recordedAt ?? DateTime.Now;
            var point = _points.FirstOrDefault(item => item.BatteryPercent == batteryPercent);
            _lastObservedPercent = batteryPercent;

            // An existing point already represents this percentage from an
            // earlier run. The first observation after a restart is not a
            // percentage change, so do not create a duplicate event. Once the
            // percentage moves and comes back, the event is recorded.
            if (isFirstObservation && point != null)
                return false;

            if (point == null)
            {
                point = new BatteryVoltageHistoryPoint { BatteryPercent = batteryPercent };
                int insertAt = 0;
                while (insertAt < _points.Count && _points[insertAt].BatteryPercent < batteryPercent)
                    insertAt++;
                _points.Insert(insertAt, point);
            }

            point.AddSample(voltageV, timestamp);

            // Percentage changes are infrequent compared with the one-second
            // monitor loop, so each accepted event is durable immediately.
            Persist();

            return true;
        }

        /// <summary>判斷目前百分比是否與上一筆觀察不同。</summary>
        internal static bool HasPercentChanged(int? lastObservedPercent, int currentPercent) =>
            currentPercent is >= 0 and <= 100 &&
            (!lastObservedPercent.HasValue || lastObservedPercent.Value != currentPercent);

        /// <summary>清除畫面與磁碟上的所有電壓紀錄。</summary>
        public static void Clear()
        {
            Load();
            _points.Clear();
            _lastObservedPercent = null;
            Persist();
        }

        /// <summary>確保程式結束前把最近的彙總寫入磁碟。</summary>
        public static void Flush()
        {
            Load();
            Persist();
        }

        private static bool IsValidPoint(BatteryVoltageHistoryPoint point) =>
            point != null &&
            point.BatteryPercent is >= 0 and <= 100 &&
            point.SampleCount > 0 &&
            double.IsFinite(point.AverageVoltageV) && point.AverageVoltageV > 0 &&
            double.IsFinite(point.MinimumVoltageV) && point.MinimumVoltageV > 0 &&
            double.IsFinite(point.MaximumVoltageV) && point.MaximumVoltageV > 0 &&
            point.MinimumVoltageV <= point.MaximumVoltageV;

        private static void Persist()
        {
            try
            {
                Directory.CreateDirectory(_historyDirectory);
                var file = new HistoryFile
                {
                    Version = CurrentFileVersion,
                    SavedAt = DateTime.Now,
                    Points = _points.ToList()
                };
                string json = JsonSerializer.Serialize(file, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                string tempPath = _historyFilePath + ".tmp";
                File.WriteAllText(tempPath, json);

                if (File.Exists(_historyFilePath))
                    File.Replace(tempPath, _historyFilePath, null);
                else
                    File.Move(tempPath, _historyFilePath);

            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Battery voltage history save failed: {ex.Message}");
            }
        }

        private sealed class HistoryFile
        {
            public int Version { get; set; }
            public DateTime SavedAt { get; set; }
            public List<BatteryVoltageHistoryPoint> Points { get; set; } = new();
        }
    }
}
