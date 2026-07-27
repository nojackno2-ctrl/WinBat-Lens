using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WinBatLens.Models;

namespace WinBatLens.Services
{
    public static class DynamicTrayIconService
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        private static Icon? _currentCreatedIcon = null;
        private static string? _lastDrawnText;
        private static Color _lastDrawnColor;

        public static void UpdateTrayIcon(NotifyIcon notifyIcon, RealTimePowerState state)
        {
            if (notifyIcon == null) return;

            try
            {
                string textToDraw;
                Color textColor;

                // Only ever draw a measured rate. With the pack idle on AC there
                // is no real wattage, so the icon shows a dash rather than a
                // number that would look like a reading.
                if (state.IsCharging && state.IsChargeRateMeasured)
                {
                    // Charging into the battery, e.g. 56.1W -> 56 in GREEN
                    int wattVal = (int)Math.Round(state.ChargingRateW);
                    textToDraw = wattVal > 99 ? "99+" : wattVal.ToString();
                    textColor = Color.FromArgb(255, 16, 185, 129); // #10B981 Emerald Green
                }
                else if (!state.IsAcOnline && state.IsDischargeRateMeasured)
                {
                    // Discharging, e.g. 48.9W -> 49 in RED. Discharge is red
                    // everywhere else in the app and the colours have to agree.
                    int wattVal = (int)Math.Round(state.DischargeRateW);
                    textToDraw = wattVal > 99 ? "99+" : wattVal.ToString();
                    textColor = Color.FromArgb(255, 244, 63, 94); // #F43F5E Red
                }
                else
                {
                    textToDraw = "–";
                    textColor = Color.FromArgb(255, 148, 163, 184); // slate
                }

                // The rounded wattage usually repeats between ticks; skip the
                // whole bitmap/font/icon regeneration when nothing changed.
                // Key on exactly what is drawn — text and colour — so a state
                // change that keeps the digits but changes the colour still
                // repaints.
                if (_currentCreatedIcon != null &&
                    textToDraw == _lastDrawnText &&
                    textColor == _lastDrawnColor)
                {
                    return;
                }

                // Generate 32x32 transparent bitmap with EXTRA LARGE rounded integer digits
                using (var bitmap = new Bitmap(32, 32))
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                    g.Clear(Color.Transparent);

                    // Start from extra large font size for 1-2 digits
                    float fontSize = 20.0f;
                    Font font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Point);
                    
                    while (fontSize > 6.0f)
                    {
                        SizeF measuredSize = g.MeasureString(textToDraw, font);
                        if (measuredSize.Width <= 31.5f && measuredSize.Height <= 31.5f)
                        {
                            break;
                        }
                        font.Dispose();
                        fontSize -= 0.5f;
                        font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Point);
                    }

                    using (font)
                    using (var textBrush = new SolidBrush(textColor))
                    using (var sf = new StringFormat
                    {
                        Alignment = StringAlignment.Center,
                        LineAlignment = StringAlignment.Center,
                        FormatFlags = StringFormatFlags.NoWrap
                    })
                    {
                        g.DrawString(textToDraw, font, textBrush, new RectangleF(0, 0, 32, 32), sf);
                    }

                    // Create HICON
                    IntPtr hIcon = bitmap.GetHicon();
                    Icon newIcon = Icon.FromHandle(hIcon);

                    // Set to NotifyIcon
                    notifyIcon.Icon = newIcon;

                    // Destroy old icon handle to prevent GDI leak
                    if (_currentCreatedIcon != null)
                    {
                        DestroyIcon(_currentCreatedIcon.Handle);
                        _currentCreatedIcon.Dispose();
                    }

                    _currentCreatedIcon = newIcon;
                    _lastDrawnText = textToDraw;
                    _lastDrawnColor = textColor;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UpdateTrayIcon error: {ex.Message}");
            }
        }
    }
}
