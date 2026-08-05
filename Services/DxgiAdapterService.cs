using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinBatLens.Services
{
    /// <summary>
    /// 提供 DXGI (DirectX Graphics Infrastructure) 顯示轉接卡枚舉服務。
    /// 用於將 GPU Engine 效能計數器實例（依 LUID 識別）精確對映至實體顯示卡（獨顯 dGPU 或內顯 iGPU）。
    /// </summary>
    public static class DxgiAdapterService
    {
        /// <summary>
        /// 表示單一 DXGI 顯示轉接卡的規格與 LUID 識別資訊。
        /// </summary>
        public class DxgiAdapter
        {
            /// <summary>小寫格式之 LUID 鍵值（例如："luid_0x00000000_0x00010666"），用於對映效能計數器。</summary>
            public string LuidKey { get; set; } = string.Empty;

            /// <summary>顯示卡名稱描述。</summary>
            public string Description { get; set; } = string.Empty;

            /// <summary>製造商 Vendor ID（例如 NVIDIA: 0x10DE, AMD: 0x1002, Intel: 0x8086）。</summary>
            public uint VendorId { get; set; }

            /// <summary>專用視訊記憶體容量（位元組 Bytes）。</summary>
            public ulong DedicatedVideoMemoryBytes { get; set; }

            /// <summary>是否為軟體模擬顯示轉接卡（如 WARP）。</summary>
            public bool IsSoftware { get; set; }

            /// <summary>是否判斷為獨立顯示卡 (dGPU)。</summary>
            public bool IsDiscrete { get; set; }
}
        private const uint VendorNvidia = 0x10DE;
        private const uint VendorAmd = 0x1002;
        private const uint VendorIntel = 0x8086;
        private const uint DxgiAdapterFlagSoftware = 2;
        private const long OneGigabyte = 1073741824L;

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DXGI_ADAPTER_DESC1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;
            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;
            public UIntPtr DedicatedVideoMemory;
            public UIntPtr DedicatedSystemMemory;
            public UIntPtr SharedSystemMemory;
            public LUID AdapterLuid;
            public uint Flags;
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("770aae78-f26f-4dba-a829-253c83d1b387")]
        private interface IDXGIFactory1
        {
            [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
            [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
            [PreserveSig] int EnumAdapters(uint adapter, out IntPtr adapterPtr);
            [PreserveSig] int MakeWindowAssociation(IntPtr windowHandle, uint flags);
            [PreserveSig] int GetWindowAssociation(out IntPtr windowHandle);
            [PreserveSig] int CreateSwapChain(IntPtr device, IntPtr desc, out IntPtr swapChain);
            [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IntPtr adapter);
            [PreserveSig] int EnumAdapters1(uint adapter, out IntPtr adapterPtr);
            [PreserveSig] int IsCurrent();
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("29038f61-3839-4626-91fd-086879011a05")]
        private interface IDXGIAdapter1
        {
            [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
            [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
            [PreserveSig] int EnumOutputs(uint output, out IntPtr outputPtr);
            [PreserveSig] int GetDesc(IntPtr desc);
            [PreserveSig] int CheckInterfaceSupport(ref Guid interfaceName, out long umdVersion);
            [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
        }

        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

        /// <summary>
        /// 枚舉系統中所有由 DXGI 識別之實體顯示轉接卡。
        /// 即使失敗亦傳回空清單，不拋出例外。
        /// </summary>
        /// <returns><see cref="DxgiAdapter"/> 清單。</returns>
        public static List<DxgiAdapter> GetAdapters()
        {
            var result = new List<DxgiAdapter>();
            IntPtr pFactory = IntPtr.Zero;
            IDXGIFactory1? factory = null;

            try
            {
                Guid factoryGuid = typeof(IDXGIFactory1).GUID;
                if (CreateDXGIFactory1(ref factoryGuid, out pFactory) != 0 || pFactory == IntPtr.Zero)
                    return result;

                factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(pFactory);

                for (uint i = 0; ; i++)
                {
                    IntPtr pAdapter;
                    if (factory.EnumAdapters1(i, out pAdapter) != 0 || pAdapter == IntPtr.Zero)
                        break;

                    IDXGIAdapter1? adapter = null;
                    try
                    {
                        adapter = (IDXGIAdapter1)Marshal.GetObjectForIUnknown(pAdapter);
                        if (adapter.GetDesc1(out DXGI_ADAPTER_DESC1 desc) != 0)
                            continue;

                        ulong dedicatedVram = (ulong)desc.DedicatedVideoMemory.ToUInt64();
                        bool isSoftware = (desc.Flags & DxgiAdapterFlagSoftware) != 0;

                        // NVIDIA 視為獨顯；其餘廠商若專用 VRAM >= 1GB 亦判定為獨顯
                        bool isDiscrete = !isSoftware &&
                            (desc.VendorId == VendorNvidia || dedicatedVram >= (ulong)OneGigabyte);

                        result.Add(new DxgiAdapter
                        {
                            LuidKey = $"luid_0x{desc.AdapterLuid.HighPart:X8}_0x{desc.AdapterLuid.LowPart:X8}".ToLowerInvariant(),
                            Description = desc.Description ?? string.Empty,
                            VendorId = desc.VendorId,
                            DedicatedVideoMemoryBytes = dedicatedVram,
                            IsSoftware = isSoftware,
                            IsDiscrete = isDiscrete
                        });
                    }
                    finally
                    {
                        if (adapter != null) Marshal.ReleaseComObject(adapter);
                        Marshal.Release(pAdapter);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DxgiAdapterService error: {ex.Message}");
            }
            finally
            {
                if (factory != null) Marshal.ReleaseComObject(factory);
                if (pFactory != IntPtr.Zero) Marshal.Release(pFactory);
            }

            return result;
        }
    }
}
