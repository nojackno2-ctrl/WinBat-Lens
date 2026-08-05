using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace WinBatLens.Models
{
    /// <summary>
    /// 一個電量百分比的電池端實測電壓彙總。
    /// </summary>
    public sealed class BatteryVoltageHistoryPoint : INotifyPropertyChanged
    {
        private double _averageVoltageV;
        private double _minimumVoltageV;
        private double _maximumVoltageV;
        private int _sampleCount;
        private DateTime _lastRecordedAt;

        /// <summary>電池電量百分比，範圍為 0–100。</summary>
        public int BatteryPercent { get; set; }

        /// <summary>此電量區間的平均實測電壓。</summary>
        public double AverageVoltageV
        {
            get => _averageVoltageV;
            set => SetField(ref _averageVoltageV, value);
        }

        /// <summary>此電量區間的最低實測電壓。</summary>
        public double MinimumVoltageV
        {
            get => _minimumVoltageV;
            set => SetField(ref _minimumVoltageV, value);
        }

        /// <summary>此電量區間的最高實測電壓。</summary>
        public double MaximumVoltageV
        {
            get => _maximumVoltageV;
            set => SetField(ref _maximumVoltageV, value);
        }

        /// <summary>此電量百分比累積的實測樣本數。</summary>
        public int SampleCount
        {
            get => _sampleCount;
            set => SetField(ref _sampleCount, value);
        }

        /// <summary>最後一次取得此電量區間電壓的時間。</summary>
        public DateTime LastRecordedAt
        {
            get => _lastRecordedAt;
            set => SetField(ref _lastRecordedAt, value);
        }

        [JsonIgnore]
        public string BatteryPercentText => $"{BatteryPercent}%";

        [JsonIgnore]
        public string AverageVoltageText => $"{AverageVoltageV:F2} V";

        [JsonIgnore]
        public string VoltageRangeText => SampleCount > 1
            ? $"{MinimumVoltageV:F2}–{MaximumVoltageV:F2} V"
            : AverageVoltageText;

        /// <summary>最後紀錄時間文字（格式 yyyy-MM-dd HH:mm:ss），與其他歷史清單一致。</summary>
        [JsonIgnore]
        public string LastRecordedAtText => LastRecordedAt.ToString("yyyy-MM-dd HH:mm:ss");

        /// <summary>
        /// 將一筆新的實測電壓加入此電量區間，並以累進平均避免保留大量原始樣本。
        /// </summary>
        public void AddSample(double voltageV, DateTime recordedAt)
        {
            if (!double.IsFinite(voltageV) || voltageV <= 0)
                throw new ArgumentOutOfRangeException(nameof(voltageV));

            if (SampleCount <= 0)
            {
                AverageVoltageV = voltageV;
                MinimumVoltageV = voltageV;
                MaximumVoltageV = voltageV;
                SampleCount = 1;
            }
            else
            {
                AverageVoltageV += (voltageV - AverageVoltageV) / (SampleCount + 1);
                MinimumVoltageV = Math.Min(MinimumVoltageV, voltageV);
                MaximumVoltageV = Math.Max(MaximumVoltageV, voltageV);
                SampleCount++;
            }

            LastRecordedAt = recordedAt;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AverageVoltageText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VoltageRangeText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastRecordedAtText)));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
