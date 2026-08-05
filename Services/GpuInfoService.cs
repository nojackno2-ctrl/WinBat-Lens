using System;
using System.Collections.Generic;
using System.Management;
using WinBatLens.Models;

namespace WinBatLens.Services
{
    /// <summary>
    /// 提供透過 WMI (Win32_VideoController) 查詢系統已安裝顯示卡之服務。
    /// 包含顯示卡名稱讀取與獨顯 (dGPU) 關鍵字與 VRAM 容量啟發式判讀。
    /// </summary>
    public class GpuInfoService
    {
        /// <summary>
        /// 枚舉系統中已安裝之顯示轉接卡清單。
        /// </summary>
        /// <returns><see cref="GpuInfo"/> 清單。</returns>
        public static List<GpuInfo> GetInstalledGpus()
        {
            var list = new List<GpuInfo>();

            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string name = obj["Name"]?.ToString() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        var gpu = new GpuInfo { Name = name };

                        if (obj["AdapterRAM"] != null &&
                            ulong.TryParse(obj["AdapterRAM"].ToString(), out ulong ramBytes) && ramBytes > 0)
                        {
                            gpu.VramBytes = ramBytes;
                        }

                        // 依廠牌名稱關鍵字與專用 VRAM 判斷是否為獨立顯示卡
                        string upperName = name.ToUpper();
                        gpu.IsDiscrete = upperName.Contains("NVIDIA") ||
                                         upperName.Contains("GEFORCE") ||
                                         upperName.Contains("RTX") ||
                                         upperName.Contains("GTX") ||
                                         upperName.Contains("QUADRO") ||
                                         (upperName.Contains("RADEON") && !upperName.Contains("GRAPHICS")) ||
                                         (upperName.Contains("AMD") && !upperName.Contains("TM") && !upperName.Contains("GRAPHICS")) ||
                                         gpu.VramBytes >= 1073741824; // >= 1 GB 專用記憶體

                        list.Add(gpu);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GpuInfoService WMI error: {ex.Message}");
            }

            if (list.Count == 0)
            {
                list.Add(new GpuInfo { Name = "Standard Display Adapter", IsDiscrete = false });
            }

            return list;
        }
    }
}
