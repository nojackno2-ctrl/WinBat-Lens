using System;
using System.Drawing;
using System.IO;

namespace WinBatLens.Services
{
    public class IconHelper
    {
        public static Icon GetAppIcon(string pngPath)
        {
            try
            {
                if (File.Exists(pngPath))
                {
                    using (var bitmap = new Bitmap(pngPath))
                    {
                        IntPtr hIcon = bitmap.GetHicon();
                        return Icon.FromHandle(hIcon);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"IconHelper error: {ex.Message}");
            }

            return SystemIcons.Application;
        }

        public static void EnsureIcoFile(string pngPath, string icoPath)
        {
            try
            {
                if (File.Exists(pngPath) && !File.Exists(icoPath))
                {
                    using (var bitmap = new Bitmap(pngPath))
                    {
                        IntPtr hIcon = bitmap.GetHicon();
                        using (var icon = Icon.FromHandle(hIcon))
                        {
                            using (var stream = new FileStream(icoPath, FileMode.Create))
                            {
                                icon.Save(stream);
                            }
                        }
                    }
                }
            }
            catch { }
        }
    }
}
