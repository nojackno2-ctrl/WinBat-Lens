using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WinBatLens.Models;

namespace WinBatLens.Services
{
    /// <summary>
    /// 提供 Windows 系統工作列托盤（Tray Icon）動態圖示繪製服務。
    /// 根據即時功率（瓦特 W）動態渲染清晰數字與色彩（充電綠色、放電紅色），並妥善管理 GDI HICON 資源避免記憶體洩漏。
    /// </summary>
    public static class DynamicTrayIconService
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr handle);

        private static Icon? _currentCreatedIcon = null;
        private static string? _lastDrawnText;
        private static Color _lastDrawnColor;

        /// <summary>
        /// 根據即時電源與功率狀態，動態繪製繪圖字型並更新系統托盤圖示。
        /// </summary>
        /// <param name="notifyIcon">WPF/WinForms NotifyIcon 控制項。</param>
        /// <param name="state">1Hz 即時遙測電源狀態。</param>
        public static void UpdateTrayIcon(NotifyIcon notifyIcon, RealTimePowerState state)
        {
            if (notifyIcon == null) return;

            try
            {
                string textToDraw;
                Color textColor;

                if (state.IsCharging && state.IsChargeRateMeasured)
                {
                    // 充電中：綠色顯示充電功率（如 56W）
                    int wattVal = (int)Math.Round(state.ChargingRateW);
                    textToDraw = wattVal > 99 ? "99+" : wattVal.ToString();
                    textColor = Color.FromArgb(255, 16, 185, 129); // #10B981 翡翠綠
                }
                else if (state.IsDischargeRateMeasured)
                {
                    // 放電中：紅色顯示放電功率（如 49W）
                    int wattVal = (int)Math.Round(state.DischargeRateW);
                    textToDraw = wattVal > 99 ? "99+" : wattVal.ToString();
                    textColor = Color.FromArgb(255, 244, 63, 94); // #F43F5E 玫瑰紅
                }
                else
                {
                    // 未放電/滿電待機
                    textToDraw = "–";
                    textColor = Color.FromArgb(255, 148, 163, 184); // 板岩灰
                }

                // 數值與顏色未改變時跳過重複繪製，節省 GPU/CPU 資源
                if (_currentCreatedIcon != null &&
                    textToDraw == _lastDrawnText &&
                    textColor == _lastDrawnColor)
                {
                    return;
                }

                // 產生 32x32 透明 Bitmap 圖元
                using (var bitmap = new Bitmap(32, 32))
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                    g.Clear(Color.Transparent);

                    // 自訂字型大小調整
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

                    // 取得 Native HICON 並複製至 Managed Icon，隨後立即銷毀原生 HICON 以防止 GDI 洩漏
                    IntPtr hIcon = bitmap.GetHicon();
                    Icon newIcon;
                    try
                    {
                        using var temporaryIcon = Icon.FromHandle(hIcon);
                        newIcon = (Icon)temporaryIcon.Clone();
                    }
                    finally
                    {
                        DestroyIcon(hIcon);
                    }

                    notifyIcon.Icon = newIcon;

                    // 釋放舊 Icon
                    if (_currentCreatedIcon != null)
                    {
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

        /// <summary>
        /// 應用程式關閉時釋放最後建立之動態 Icon 資源。
        /// </summary>
        public static void Dispose()
        {
            try { _currentCreatedIcon?.Dispose(); } catch { }
            _currentCreatedIcon = null;
            _lastDrawnText = null;
            _lastDrawnColor = default;
        }
    }
}
