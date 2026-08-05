using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace WinBatLens.Services
{
    /// <summary>
    /// 提供 Windows 登錄檔 (HKCU Run 鍵值) 開機自動啟動與背景模式參數選單管理服務。
    /// </summary>
    public class StartupService
    {
        private const string REG_RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string APP_NAME = "WinBatLens";

        /// <summary>開機自啟動時傳遞之背景常駐參數旗標（最小化至系統工作列托盤）。</summary>
        public const string BackgroundArgument = "--background";

        /// <summary>
        /// 檢查當前使用者是否已啟用開機自動啟動。
        /// </summary>
        /// <returns>若登錄檔中已設定啟動項目則傳回 true，否則傳回 false。</returns>
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

        /// <summary>
        /// 設定或取消開機自動啟動（包含 <c>--background</c> 參數）。
        /// </summary>
        /// <param name="enable">true 為啟用開機自啟動，false 為取消。</param>
        /// <returns>設定成功傳回 true，失敗傳回 false。</returns>
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
                            key.SetValue(APP_NAME, $"\"{exePath}\" {BackgroundArgument}");
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
