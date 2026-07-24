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
                // Determine text & colors
                bool isChargingSuccess = state.IsAcOnline && state.IsCharging && state.ChargingRateW > 0;
                
                string textToDraw;
                Color textColor;

                if (isChargingSuccess)
                {
                    // Green text for charging (+W)
                    textToDraw = state.ChargingRateW >= 100 
                        ? $"+{Math.Round(state.ChargingRateW)}" 
                        : $"+{state.ChargingRateW:F0}W";
                    textColor = Color.FromArgb(255, 16, 185, 129); // #10B981 Emerald Green
                }
                else if (state.IsAcOnline && !state.IsCharging)
                {
                    // Full charge (0W / AC pass through) -> Green AC
                    textToDraw = "AC";
                    textColor = Color.FromArgb(255, 16, 185, 129);
                }
                else
                {
                    // Red text for discharging (-W)
                    double disW = state.DischargeRateW;
                    textToDraw = disW >= 100 
                        ? $"{Math.Round(disW)}" 
                        : $"{disW:F0}W";
                    textColor = Color.FromArgb(255, 239, 68, 68); // #EF4444 Crimson Red
                }

                // Generate 32x32 dynamic bitmap icon
                using (var bitmap = new Bitmap(32, 32))
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                    // Draw dark badge background for high visibility
                    using (var bgBrush = new SolidBrush(Color.FromArgb(230, 9, 13, 22)))
                    using (var borderPen = new Pen(textColor, 1.5f))
                    {
                        g.FillRectangle(bgBrush, 0, 0, 32, 32);
                        g.DrawRectangle(borderPen, 1, 1, 30, 30);
                    }

                    // Choose font size based on text length
                    float fontSize = textToDraw.Length >= 4 ? 8.5f : (textToDraw.Length == 3 ? 10.0f : 11.5f);
                    using (var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Point))
                    using (var textBrush = new SolidBrush(textColor))
                    using (var sf = new StringFormat
                    {
                        Alignment = StringAlignment.Center,
                        LineAlignment = StringAlignment.Center
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
