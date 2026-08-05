using System;
using System.Runtime.InteropServices;

namespace WinBatLens.Services
{
    /// <summary>
    /// 表示外接充電器/變壓器供電能力評估狀況（透過 Windows.System.Power.PowerManager）。
    /// </summary>
    public enum PowerSupplyCapability
    {
        /// <summary>無法取得 API 數據或無法推論。</summary>
        Unknown,

        /// <summary>目前使用電池運作，未連接外接電源。</summary>
        NotPresent,

        /// <summary>外接充電器供電不足（例如用 65W PD 充電器推高負載筆電，電池仍持續放電）。</summary>
        Inadequate,

        /// <summary>外接充電器供電充足，能完全涵蓋系統運作需求。</summary>
        Adequate,
    }

    /// <summary>
    /// 提供讀取 Windows 原生 WinRT <c>PowerManager.PowerSupplyStatus</c> 之服務。
    /// 無需最高管理權限即可精確偵測外接充電器是否供電不足（Inadequate PD Supply）。
    /// </summary>
    public static class PowerSupplyService
    {
        private const string PowerManagerClassName = "Windows.System.Power.PowerManager";

        private static Guid IID_IPowerManagerStatics = new("1394825D-62CE-4364-98D5-AA28C7FBD15B");

        private const int VtblSlotGetPowerSupplyStatus = 12;

        private const int RO_INIT_MULTITHREADED = 1;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetEnumProperty(IntPtr thisPtr, out int value);

        private static readonly object _sync = new object();
        private static IntPtr _factory = IntPtr.Zero;
        private static GetEnumProperty? _getPowerSupplyStatus;
        private static bool _attempted;

        /// <summary>
        /// 初始化 WinRT PowerManager 啟動處理器（建議於背景 ThreadPool 執行緒執行）。
        /// </summary>
        public static void Initialize()
        {
            lock (_sync)
            {
                if (_factory == IntPtr.Zero) _attempted = false;
                EnsureFactory();
            }
        }

        /// <summary>
        /// 取得外接充電器供電能力評估狀況（Adequate、Inadequate 或 NotPresent）。
        /// </summary>
        /// <returns><see cref="PowerSupplyCapability"/> 枚舉。</returns>
        public static PowerSupplyCapability GetStatus()
        {
            GetEnumProperty? getter;
            IntPtr factory;

            lock (_sync)
            {
                EnsureFactory();
                getter = _getPowerSupplyStatus;
                factory = _factory;
            }

            if (getter == null || factory == IntPtr.Zero) return PowerSupplyCapability.Unknown;

            try
            {
                if (getter(factory, out int value) != 0) return PowerSupplyCapability.Unknown;

                return value switch
                {
                    0 => PowerSupplyCapability.NotPresent,
                    1 => PowerSupplyCapability.Inadequate,
                    2 => PowerSupplyCapability.Adequate,
                    _ => PowerSupplyCapability.Unknown,
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"PowerSupplyService.GetStatus: {ex.Message}");
                return PowerSupplyCapability.Unknown;
            }
        }

        /// <summary>
        /// 關閉並釋放 COM / WinRT 工廠物件。
        /// </summary>
        public static void Shutdown()
        {
            lock (_sync)
            {
                if (_factory != IntPtr.Zero)
                {
                    try { Marshal.Release(_factory); } catch { }
                    _factory = IntPtr.Zero;
                }
                _getPowerSupplyStatus = null;
                _attempted = true;
            }
        }

        /// <summary>
        /// 初始化 WinRT Activation Factory COM 介面指標。
        /// </summary>
        private static void EnsureFactory()
        {
            if (_attempted) return;
            _attempted = true;

            IntPtr classId = IntPtr.Zero;
            try
            {
                RoInitialize(RO_INIT_MULTITHREADED);

                if (WindowsCreateString(PowerManagerClassName, PowerManagerClassName.Length, out classId) != 0)
                    return;

                if (RoGetActivationFactory(classId, ref IID_IPowerManagerStatics, out IntPtr factory) != 0
                    || factory == IntPtr.Zero)
                    return;

                IntPtr vtbl = Marshal.ReadIntPtr(factory);
                IntPtr slot = Marshal.ReadIntPtr(vtbl, VtblSlotGetPowerSupplyStatus * IntPtr.Size);
                if (slot == IntPtr.Zero)
                {
                    Marshal.Release(factory);
                    return;
                }

                _factory = factory;
                _getPowerSupplyStatus = Marshal.GetDelegateForFunctionPointer<GetEnumProperty>(slot);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"PowerSupplyService.EnsureFactory: {ex.Message}");
            }
            finally
            {
                if (classId != IntPtr.Zero)
                {
                    try { WindowsDeleteString(classId); } catch { }
                }
            }
        }

        #region WinRT P/Invoke
        [DllImport("combase.dll")]
        private static extern int RoInitialize(int initType);

        [DllImport("combase.dll", CharSet = CharSet.Unicode)]
        private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

        [DllImport("combase.dll")]
        private static extern int WindowsDeleteString(IntPtr hstring);

        [DllImport("combase.dll")]
        private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);
        #endregion
    }
}
