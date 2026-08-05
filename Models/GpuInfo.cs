namespace WinBatLens.Models
{
    /// <summary>
    /// 表示顯示轉接卡（GPU）的基本描述與分類資訊。
    /// </summary>
    /// <remarks>
    /// 本類別僅保留必要的 GPU 名稱、是否為獨立顯示卡（dGPU）標記與 VRAM 容量。
    /// VramBytes 用於獨立顯示卡判定邏輯。
    /// </remarks>
    public class GpuInfo
    {
        /// <summary>顯示轉接卡名稱。</summary>
        public string Name { get; set; } = "Unknown GPU";

        /// <summary>是否為獨立顯示卡（dGPU）。</summary>
        public bool IsDiscrete { get; set; }

        /// <summary>專用視訊記憶體容量（位元組 Bytes）。</summary>
        public ulong VramBytes { get; set; }
    }
}
