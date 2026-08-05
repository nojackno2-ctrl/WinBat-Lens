using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace WinBatLens.Services
{
    /// <summary>
    /// 提供呼叫 Windows 系統指令 `powercfg /batteryreport` 產生 HTML 電池報告之服務。
    /// 包含非同步行程控制、10 秒逾時保護與暫存檔自動清理。
    /// </summary>
    public static class PowerCfgService
    {
        /// <summary>
        /// 非同步執行 powercfg 指令產生電池報告 HTML 內容。
        /// </summary>
        /// <returns>包含執行成功與否、HTML 內文與錯誤訊息之元組。</returns>
        public static async Task<(bool Success, string HtmlContent, string ErrorMessage)> GenerateReportAsync()
        {
            return await Task.Run(() =>
            {
                string tempFile = Path.Combine(Path.GetTempPath(), $"winbat_report_{Guid.NewGuid():N}.html");
                try
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "powercfg",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true
                    };
                    startInfo.ArgumentList.Add("/batteryreport");
                    startInfo.ArgumentList.Add("/output");
                    startInfo.ArgumentList.Add(tempFile);

                    using (var process = Process.Start(startInfo))
                    {
                        if (process == null)
                        {
                            return (false, string.Empty, "無法啟動 powercfg 行程。");
                        }

                        if (!process.WaitForExit(10000)) // 10s 逾時控制
                        {
                            try { process.Kill(entireProcessTree: true); } catch { }
                            try { process.WaitForExit(1000); } catch { }
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
                finally
                {
                    // 自動清理未處理完畢或失敗之暫存 HTML 檔案
                    try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                }
            });
        }
    }
}
