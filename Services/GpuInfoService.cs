using System;
using System.Collections.Generic;
using System.Management;
using WinBatLens.Models;

namespace WinBatLens.Services
{
    public class GpuInfoService
    {
        /// <summary>
        /// Enumerates display adapters. Only the name and whether each is the
        /// discrete GPU are used now, so the query asks for nothing else beyond
        /// AdapterRAM, which the discrete-GPU heuristic needs.
        /// </summary>
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

                        string upperName = name.ToUpper();
                        gpu.IsDiscrete = upperName.Contains("NVIDIA") ||
                                         upperName.Contains("GEFORCE") ||
                                         upperName.Contains("RTX") ||
                                         upperName.Contains("GTX") ||
                                         upperName.Contains("QUADRO") ||
                                         (upperName.Contains("RADEON") && !upperName.Contains("GRAPHICS")) ||
                                         (upperName.Contains("AMD") && !upperName.Contains("TM") && !upperName.Contains("GRAPHICS")) ||
                                         gpu.VramBytes >= 1073741824; // >= 1 GB dedicated

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
