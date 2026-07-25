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
        private static bool _lastDrawnIsAc;

        public static void UpdateTrayIcon(NotifyIcon notifyIcon, RealTimePowerState state)
        {
            if (notifyIcon == null) return;

            try
            {
                string textToDraw;
                Color textColor;

                if (state.IsAcOnline)
                {
                    // Render AC Total Input Wattage (AcTotalInputW) e.g. 28.9W -> 29 in GREEN
                    int wattVal = (int)Math.Round(state.AcTotalInputW);
                    textToDraw = wattVal > 99 ? "99+" : wattVal.ToString();
                    textColor = Color.FromArgb(255, 16, 185, 129); // #10B981 Emerald Green
                }
                else
                {
                    // Render Battery Discharge Wattage (DischargeRateW) e.g. 15.7W -> 16 in RED
                    int wattVal = (int)Math.Round(state.DischargeRateW);
                    textToDraw = wattVal > 99 ? "99+" : wattVal.ToString();
                    textColor = Color.FromArgb(255, 239, 68, 68); // #EF4444 Crimson Red
                }

                // The rounded wattage usually repeats between ticks; skip the
                // whole bitmap/font/icon regeneration when nothing changed.
                if (_currentCreatedIcon != null &&
                    textToDraw == _lastDrawnText &&
                    state.IsAcOnline == _lastDrawnIsAc)
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
                    _lastDrawnIsAc = state.IsAcOnline;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UpdateTrayIcon error: {ex.Message}");
            }
        }
    }
}
