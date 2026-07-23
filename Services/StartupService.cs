using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace WinBatLens.Services
{
    public class StartupService
    {
        private const string REG_RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string APP_NAME = "WinBatLens";

        public static bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(REG_RUN_KEY, false))
                {
                    if (key != null)
                    {
                        var val = key.GetValue(APP_NAME);
                        return val != null;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"IsAutoStartEnabled error: {ex.Message}");
            }
            return false;
        }

        public static bool SetAutoStart(bool enable)
        {
            try
            {
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(REG_RUN_KEY, true))
                {
                    if (key == null) return false;

                    if (enable)
                    {
                        string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
                        if (!string.IsNullOrEmpty(exePath))
                        {
                            key.SetValue(APP_NAME, $"\"{exePath}\"");
                            return true;
                        }
                    }
                    else
                    {
                        key.DeleteValue(APP_NAME, false);
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetAutoStart error: {ex.Message}");
            }
            return false;
        }
    }
}
