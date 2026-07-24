using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinBatLens.Services
{
    /// <summary>
    /// Enumerates physical display adapters via DXGI so that each GPU Engine
    /// performance-counter instance (identified by its LUID) can be mapped back
    /// to the correct physical GPU (discrete vs. integrated). This is required
    /// because on many laptops every counter instance reports "phys_0" and only
    /// the LUID distinguishes the adapters.
    /// </summary>
    public static class DxgiAdapterService
    {
        public class DxgiAdapter
        {
            /// <summary>Lower-cased LUID token as it appears inside a GPU Engine
            /// counter instance name, e.g. "luid_0x00000000_0x00010666".</summary>
            public string LuidKey { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public uint VendorId { get; set; }
            public ulong DedicatedVideoMemoryBytes { get; set; }
            public bool IsSoftware { get; set; }
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
        /// Returns all physical adapters known to DXGI. Never throws; returns an
        /// empty list if DXGI is unavailable so callers can fall back gracefully.
        /// </summary>
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

                        // NVIDIA is always discrete on these laptops; otherwise treat
                        // anything with >= 1 GB dedicated VRAM as discrete. Integrated
                        // GPUs (AMD/Intel iGPU) carve out far less dedicated memory.
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
