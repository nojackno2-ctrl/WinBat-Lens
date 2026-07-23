using System;
using System.Collections.Generic;
using System.Management;
using WinBatLens.Models;

namespace WinBatLens.Services
{
    public class GpuInfoService
    {
        public static List<GpuInfo> GetInstalledGpus()
        {
            var list = new List<GpuInfo>();

            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM, DriverVersion, DriverDate, Status, Availability FROM Win32_VideoController"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string name = obj["Name"]?.ToString() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        var gpu = new GpuInfo
                        {
                            Name = name,
                            DriverVersion = obj["DriverVersion"]?.ToString() ?? "N/A"
                        };

                        // Parse Driver Date
                        string dDate = obj["DriverDate"]?.ToString() ?? "";
                        if (dDate.Length >= 8)
                        {
                            gpu.DriverDate = $"{dDate.Substring(0, 4)}-{dDate.Substring(4, 2)}-{dDate.Substring(6, 2)}";
                        }

                        // Parse VRAM Size
                        if (obj["AdapterRAM"] != null && ulong.TryParse(obj["AdapterRAM"].ToString(), out ulong ramBytes) && ramBytes > 0)
                        {
                            gpu.VramBytes = ramBytes;
                            double vramGb = Math.Round((double)ramBytes / (1024.0 * 1024.0 * 1024.0), 1);
                            if (vramGb >= 0.5)
                            {
                                gpu.VramText = $"{vramGb:F1} GB 專屬顯存";
                            }
                            else
                            {
                                double vramMb = Math.Round((double)ramBytes / (1024.0 * 1024.0), 0);
                                gpu.VramText = $"{vramMb} MB";
                            }
                        }
                        else
                        {
                            gpu.VramText = "共享系統記憶體 (Dynamic VRAM)";
                        }

                        // Discrete GPU Detection Logic
                        string upperName = name.ToUpper();
                        bool isDiscrete = upperName.Contains("NVIDIA") || 
                                         upperName.Contains("GEFORCE") || 
                                         upperName.Contains("RTX") || 
                                         upperName.Contains("GTX") || 
                                         upperName.Contains("QUADRO") ||
                                         (upperName.Contains("RADEON") && !upperName.Contains("GRAPHICS")) ||
                                         (upperName.Contains("AMD") && !upperName.Contains("TM") && !upperName.Contains("GRAPHICS")) ||
                                         gpu.VramBytes >= 1073741824; // >= 1GB dedicated RAM

                        gpu.IsDiscrete = isDiscrete;

                        // GPU Power Status
                        if (isDiscrete)
                        {
                            gpu.Status = "獨立顯示卡 (高效能渲染就緒)";
                        }
                        else
                        {
                            gpu.Status = "整合顯示晶片 (省電模式)";
                        }

                        list.Add(gpu);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GpuInfoService WMI error: {ex.Message}");
            }

            // Fallback if WMI query returns empty
            if (list.Count == 0)
            {
                list.Add(new GpuInfo
                {
                    Name = "Standard Display Adapter",
                    IsDiscrete = false,
                    VramText = "共享記憶體",
                    Status = "系統預設顯示卡"
                });
            }

            return list;
        }
    }
}
