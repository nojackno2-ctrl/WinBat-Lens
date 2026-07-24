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

        public static void UpdateTrayIcon(NotifyIcon notifyIcon, RealTimePowerState state)
        {
            if (notifyIcon == null) return;

            try
            {
                string textToDraw;
                Color textColor;

                if (state.IsAcOnline)
                {
                    if (state.IsCharging && state.ChargingRateW > 0)
                    {
                        // Green text for full charging wattage with decimal (e.g. 38.5)
                        textToDraw = state.ChargingRateW.ToString("F1");
                        textColor = Color.FromArgb(255, 16, 185, 129); // #10B981 Emerald Green
                    }
                    else
                    {
                        // 100% Fully charged / AC Pass Through -> 0.0 W
                        textToDraw = "0.0";
                        textColor = Color.FromArgb(255, 16, 185, 129); // Green
                    }
                }
                else
                {
                    // Red text for full discharging wattage with decimal (e.g. 15.7)
                    textToDraw = state.DischargeRateW.ToString("F1");
                    textColor = Color.FromArgb(255, 239, 68, 68); // #EF4444 Crimson Red
                }

                // Generate 32x32 transparent bitmap with auto-scaled full precision text
                using (var bitmap = new Bitmap(32, 32))
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                    g.Clear(Color.Transparent);

                    // Dynamic font auto-scaling using MeasureString to ensure 100% fit without clipping
                    float fontSize = 16.0f;
                    Font font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Point);
                    
                    while (fontSize > 5.5f)
                    {
                        SizeF measuredSize = g.MeasureString(textToDraw, font);
                        if (measuredSize.Width <= 31.0f && measuredSize.Height <= 31.0f)
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
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UpdateTrayIcon error: {ex.Message}");
            }
        }
    }
}
