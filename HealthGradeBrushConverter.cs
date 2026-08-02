using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

// Implicit usings pull in both System.Drawing and System.Windows.Media, and
// each has a Color; this file means the WPF one.
using MediaColor = System.Windows.Media.Color;

namespace WinBatLens
{
    /// <summary>
    /// Turns a health percentage into the colour for its grade, so a degraded
    /// pack never gets painted the same green as a healthy one. The 80% / 60%
    /// cuts are the ones <c>BatteryReportParser</c> uses for the status label —
    /// they must stay in step, or the colour and the wording disagree.
    /// Pass "Badge" as the converter parameter for the translucent pill fill.
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

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
