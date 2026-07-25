namespace WinBatLens.Models
{
    /// <summary>
    /// The little that is still needed about a display adapter: its name, and
    /// whether it is the discrete one.
    /// </summary>
    /// <remarks>
    /// This used to carry VRAM text, driver version, driver date and a status
    /// string for the "系統顯示卡清單" card. That card is gone and none of that
    /// data fed anything else, so it is no longer queried or stored.
    /// VramBytes stays because the discrete-GPU heuristic uses it.
    /// </remarks>
    public class GpuInfo
    {
        public string Name { get; set; } = "Unknown GPU";
        public bool IsDiscrete { get; set; }
        public ulong VramBytes { get; set; }
    }
}
