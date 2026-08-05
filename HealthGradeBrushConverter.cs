using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

// 顯式指定 WPF 的 System.Windows.Media.Color
using MediaColor = System.Windows.Media.Color;

namespace WinBatLens
{
    /// <summary>
    /// 提供將電池健康度百分比 (0-100%) 轉換為對應評級顏色 (綠/黃/紅 SolidColorBrush) 之 WPF 值轉換器 (IValueConverter)。
    /// 80% 與 60% 門檻與 <c>BatteryReportParser</c> 判定邏輯保持同步。
    /// 若 ConverterParameter 設為 "Badge"，則傳回半透明背景填滿 Brush。
    /// </summary>
    public sealed class HealthGradeBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush Good = Frozen(0x10, 0xB9, 0x81);
        private static readonly SolidColorBrush Warn = Frozen(0xF5, 0x9E, 0x0B);
        private static readonly SolidColorBrush Danger = Frozen(0xF4, 0x3F, 0x5E);

        private static readonly SolidColorBrush GoodBadge = Frozen(0x10, 0xB9, 0x81, 0x20);
        private static readonly SolidColorBrush WarnBadge = Frozen(0xF5, 0x9E, 0x0B, 0x20);
        private static readonly SolidColorBrush DangerBadge = Frozen(0xF4, 0x3F, 0x5E, 0x20);

        private static SolidColorBrush Frozen(byte r, byte g, byte b, byte a = 0xFF)
        {
            var brush = new SolidColorBrush(MediaColor.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
}
        /// <summary>
        /// 將健康度百分比數值轉換為對應之 SolidColorBrush。
        /// </summary>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double percent = value switch
            {
                double d => d,
                float f => f,
                int i => i,
                _ => 0.0
            };

            bool badge = parameter as string == "Badge";

            if (percent < 60.0) return badge ? DangerBadge : Danger;
            if (percent < 80.0) return badge ? WarnBadge : Warn;
            return badge ? GoodBadge : Good;
        }

        /// <summary>
        /// 不支援反向轉換。
        /// </summary>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
