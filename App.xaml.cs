using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using WinBatLens.Services;

namespace WinBatLens
{
    /// <summary>
    /// WinBat Lens WPF 應用程式入口點與全域例外狀況處理器。
    /// 包含單一執行個體鎖定控制與崩潰日誌記錄。
    /// </summary>
    public partial class App : System.Windows.Application
    {
        /// <summary>
        /// 應用程式啟動入口邏輯。
        /// </summary>
        /// <param name="e">啟動參數事件。</param>
        protected override void OnStartup(StartupEventArgs e)
        {
            // 若已知有其他同版本執行個體運作中，即交由其喚醒，本行程自動結束。
            if (!SingleInstanceService.TryClaimOwnership())
            {
                Shutdown();
                return;
}
            base.OnStartup(e);

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                LogException(args.ExceptionObject as Exception, "AppDomain.CurrentDomain.UnhandledException");
            };

            DispatcherUnhandledException += (s, args) =>
            {
                LogException(args.Exception, "DispatcherUnhandledException");
                args.Handled = true;
            };
        }

        /// <summary>
        /// 應用程式結束清理邏輯。
        /// </summary>
        /// <param name="e">結束參數事件。</param>
        protected override void OnExit(ExitEventArgs e)
        {
            SingleInstanceService.Release();
            base.OnExit(e);
        }

        /// <summary>
        /// 記錄未擷取的非預期例外狀況並彈出錯誤對話盒與日誌檔。
        /// </summary>
        private void LogException(Exception? ex, string source)
        {
            if (ex == null) return;

            string errorMsg = $"[WinBat Lens Crash Report]\nSource: {source}\nTime: {DateTime.Now}\nError: {ex.Message}\n\nStack Trace:\n{ex.StackTrace}";
            System.Diagnostics.Debug.WriteLine(errorMsg);

            try
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "winbat_crash.log");
                File.AppendAllText(logPath, errorMsg + "\n----------------------------------------\n");
            }
            catch { }

            System.Windows.MessageBox.Show($"WinBat Lens 啟動或執行時發生例外狀況:\n\n{ex.Message}\n\n詳細紀錄已寫入 winbat_crash.log", "WinBat Lens - 執行階段錯誤", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }
}
