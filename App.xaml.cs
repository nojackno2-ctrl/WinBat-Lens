using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace WinBatLens
{
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
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
