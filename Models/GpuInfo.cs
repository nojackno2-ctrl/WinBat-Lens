namespace WinBatLens.Models
{
    public class GpuInfo
    {
        public string Name { get; set; } = "Unknown GPU";
        public bool IsDiscrete { get; set; }
        public string GpuTypeTag => IsDiscrete ? "獨立顯示卡 (dGPU)" : "內建顯示晶片 (iGPU)";
        public string VramText { get; set; } = "共享視訊記憶體";
        public ulong VramBytes { get; set; }
        public string DriverVersion { get; set; } = "N/A";
        public string DriverDate { get; set; } = "N/A";
        public string Status { get; set; } = "正常運作中";
        public string StatusClass => IsDiscrete ? "Discrete" : "Integrated";
    }
}
