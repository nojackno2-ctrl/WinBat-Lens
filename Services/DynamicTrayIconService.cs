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
                        // Green text for charging wattage digits only
                        int wattVal = (int)Math.Round(state.ChargingRateW);
                        textToDraw = wattVal > 99 ? "99+" : wattVal.ToString();
                        textColor = Color.FromArgb(255, 16, 185, 129); // #10B981 Emerald Green
                    }
                    else
                    {
                        // 100% Fully charged / AC Pass Through -> 0 W
                        textToDraw = "0";
                        textColor = Color.FromArgb(255, 16, 185, 129); // Green
                    }
                }
                else
                {
                    // Red text for discharging wattage digits only
                    int wattVal = (int)Math.Round(state.DischargeRateW);
                    textToDraw = wattVal > 99 ? "99+" : wattVal.ToString();
                    textColor = Color.FromArgb(255, 239, 68, 68); // #EF4444 Crimson Red
                }

                // Generate 32x32 dynamic bitmap icon with large crisp numbers
                using (var bitmap = new Bitmap(32, 32))
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                    // Draw dark badge background for contrast
                    using (var bgBrush = new SolidBrush(Color.FromArgb(235, 9, 13, 22)))
                    using (var borderPen = new Pen(textColor, 1.5f))
                    {
                        g.FillRectangle(bgBrush, 0, 0, 32, 32);
                        g.DrawRectangle(borderPen, 1, 1, 30, 30);
                    }

                    // Choose large bold font for digits
                    float fontSize = textToDraw.Length >= 3 ? 10.0f : (textToDraw.Length == 2 ? 13.0f : 15.0f);
                    using (var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Point))
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
