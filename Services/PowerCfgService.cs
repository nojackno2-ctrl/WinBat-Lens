using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace WinBatLens.Services
{
    public class PowerCfgService
    {
        public static async Task<(bool Success, string HtmlContent, string ErrorMessage)> GenerateReportAsync()
        {
            return await Task.Run(() =>
            {
                string tempFile = Path.Combine(Path.GetTempPath(), $"winbat_report_{Guid.NewGuid():N}.html");
                string command = $"/batteryreport /output \"{tempFile}\"";

                try
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "powercfg",
                        Arguments = command,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    using (var process = Process.Start(startInfo))
                    {
                        if (process == null)
                        {
                            return (false, string.Empty, "無法啟動 powercfg 行程。");
                        }

                        if (!process.WaitForExit(10000)) // 10s timeout
                        {
                            try { process.Kill(); } catch { }
                            return (false, string.Empty, "powercfg 執行逾時（超過 10 秒），已強制結束。");
                        }

                        if (File.Exists(tempFile))
                        {
                            string content = File.ReadAllText(tempFile);
                            try { File.Delete(tempFile); } catch { }
                            return (true, content, string.Empty);
                        }
                        else
                        {
                            string err = process.StandardError.ReadToEnd();
                            return (false, string.Empty, string.IsNullOrWhiteSpace(err) ? "產生的報告檔案不存在。" : err);
                        }
                    }
                }
                catch (Exception ex)
                {
                    return (false, string.Empty, $"執行 powercfg 失敗: {ex.Message}");
                }
            });
        }
    }
}
