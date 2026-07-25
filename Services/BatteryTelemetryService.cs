using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WinBatLens.Services
{
    /// <summary>
    /// Live charge/discharge rate and pack voltage read straight from the
    /// battery class driver with <c>IOCTL_BATTERY_QUERY_STATUS</c>.
    /// </summary>
    /// <remarks>
    /// This replaces <c>Win32_Battery.DischargeRate</c>, which is blank on a
    /// great many laptops (including the development machine) and caused the
    /// app to fall back to a utilisation-based estimate even though a real
    /// measurement was available. Measured on an ASUS ROG laptop: on battery
    /// the rate tracks load from -5.5 W idle to -39.9 W loaded.
    ///
    /// A rate of exactly 0 is a legitimate reading, not a failure — a pack that
    /// is full and on AC passes no current. Genuine unavailability is signalled
    /// by the driver as BATTERY_UNKNOWN_RATE, which is reported separately.
    ///
    /// Being a direct device IOCTL this needs no elevation and no WMI/COM, so
    /// unlike the WMI path it is cheap enough to poll every second.
    /// </remarks>
    public static class BatteryTelemetryService
    {
        public readonly struct Reading
        {
            public bool IsOnAc { get; init; }
            public bool IsCharging { get; init; }
            public bool IsDischarging { get; init; }

            /// <summary>False when the driver reported BATTERY_UNKNOWN_RATE.</summary>
            public bool IsRateKnown { get; init; }

            /// <summary>Watts flowing out of the pack; 0 when not discharging.</summary>
            public double DischargeW { get; init; }

            /// <summary>Watts flowing into the pack; 0 when not charging.</summary>
            public double ChargeW { get; init; }

            /// <summary>Pack voltage, or 0 when the driver does not report it.</summary>
            public double VoltageV { get; init; }
        }

        private const uint BATTERY_POWER_ON_LINE = 0x00000001;
        private const uint BATTERY_DISCHARGING = 0x00000002;
        private const uint BATTERY_CHARGING = 0x00000004;
        private const int BATTERY_UNKNOWN_RATE = unchecked((int)0x80000000);
        private const uint BATTERY_UNKNOWN_VOLTAGE = 0xFFFFFFFF;

        private const uint IOCTL_BATTERY_QUERY_TAG = 0x294040;
        private const uint IOCTL_BATTERY_QUERY_STATUS = 0x29404C;

        private static Guid _batteryClassGuid = new("72631e54-78a4-11d0-bcf7-00aa00b7b32a");

        private static readonly object _sync = new object();
        private static IntPtr _handle = InvalidHandle;
        private static uint _tag;
        private static bool _initialized;

        private static IntPtr InvalidHandle => new IntPtr(-1);

        /// <summary>True once a battery device has been opened successfully.</summary>
        public static bool IsAvailable { get; private set; }

        public static void Initialize()
        {
            lock (_sync)
            {
                if (_initialized) return;
                _initialized = true;
                OpenFirstBattery();
            }
        }

        /// <summary>
        /// Reads the battery. Returns false on desktops, or if the device
        /// vanished (an external pack being removed, say).
        /// </summary>
        public static bool TryRead(out Reading reading)
        {
            reading = default;

            lock (_sync)
            {
                if (!_initialized) { _initialized = true; OpenFirstBattery(); }
                if (_handle == InvalidHandle) return false;

                if (!TryQueryStatus(out BATTERY_STATUS st))
                {
                    // The tag is invalidated whenever the pack changes; re-open
                    // once and retry before giving up.
                    CloseCurrent();
                    OpenFirstBattery();
                    if (_handle == InvalidHandle || !TryQueryStatus(out st)) return false;
                }

                bool charging = (st.PowerState & BATTERY_CHARGING) != 0;
                bool discharging = (st.PowerState & BATTERY_DISCHARGING) != 0;
                bool rateKnown = st.Rate != BATTERY_UNKNOWN_RATE;

                // Sign convention is negative-for-discharge, but not every
                // driver honours it — trust the state flags for direction and
                // take the magnitude.
                double watts = rateKnown ? Math.Abs(st.Rate) / 1000.0 : 0.0;

                reading = new Reading
                {
                    IsOnAc = (st.PowerState & BATTERY_POWER_ON_LINE) != 0,
                    IsCharging = charging,
                    IsDischarging = discharging,
                    IsRateKnown = rateKnown,
                    DischargeW = (rateKnown && !charging) ? Math.Round(watts, 1) : 0.0,
                    ChargeW = (rateKnown && charging) ? Math.Round(watts, 1) : 0.0,
                    VoltageV = st.Voltage == BATTERY_UNKNOWN_VOLTAGE || st.Voltage == 0
                        ? 0.0
                        : Math.Round(st.Voltage / 1000.0, 2),
                };
                return true;
            }
        }

        public static void Shutdown()
        {
            lock (_sync)
            {
                CloseCurrent();
                _initialized = false;
                IsAvailable = false;
            }
        }

        private static void CloseCurrent()
        {
            if (_handle != InvalidHandle)
            {
                try { CloseHandle(_handle); } catch { }
                _handle = InvalidHandle;
            }
            _tag = 0;
        }

        private static bool TryQueryStatus(out BATTERY_STATUS status)
        {
            status = default;

            var wait = new BATTERY_WAIT_STATUS { BatteryTag = _tag };
            int inSize = Marshal.SizeOf<BATTERY_WAIT_STATUS>();
            int outSize = Marshal.SizeOf<BATTERY_STATUS>();

            IntPtr inBuf = Marshal.AllocHGlobal(inSize);
            IntPtr outBuf = Marshal.AllocHGlobal(outSize);
            try
            {
                Marshal.StructureToPtr(wait, inBuf, false);
                if (!DeviceIoControl(_handle, IOCTL_BATTERY_QUERY_STATUS, inBuf, (uint)inSize,
                                     outBuf, (uint)outSize, out _, IntPtr.Zero))
                    return false;

                status = Marshal.PtrToStructure<BATTERY_STATUS>(outBuf);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(inBuf);
                Marshal.FreeHGlobal(outBuf);
            }
        }

        private static void OpenFirstBattery()
        {
            foreach (string path in EnumerateBatteryPaths())
            {
                IntPtr h = CreateFile(path, GENERIC_READ | GENERIC_WRITE,
                                      FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero,
                                      OPEN_EXISTING, 0, IntPtr.Zero);
                if (h == InvalidHandle) continue;

                if (TryQueryTag(h, out uint tag))
                {
                    _handle = h;
                    _tag = tag;
                    IsAvailable = true;
                    return;
                }

                try { CloseHandle(h); } catch { }
            }

            IsAvailable = false;
        }

        private static bool TryQueryTag(IntPtr h, out uint tag)
        {
            tag = 0;
            IntPtr inBuf = Marshal.AllocHGlobal(4);
            IntPtr outBuf = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(inBuf, 0);   // zero timeout: do not wait
                if (!DeviceIoControl(h, IOCTL_BATTERY_QUERY_TAG, inBuf, 4, outBuf, 4, out _, IntPtr.Zero))
                    return false;

                tag = (uint)Marshal.ReadInt32(outBuf);
                return tag != 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(inBuf);
                Marshal.FreeHGlobal(outBuf);
            }
        }

        private static IEnumerable<string> EnumerateBatteryPaths()
        {
            var paths = new List<string>();

            IntPtr set = SetupDiGetClassDevs(ref _batteryClassGuid, null, IntPtr.Zero,
                                             DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == InvalidHandle) return paths;

            try
            {
                for (uint i = 0; ; i++)
                {
                    var did = new SP_DEVICE_INTERFACE_DATA
                    { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };

                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref _batteryClassGuid, i, ref did))
                        break;

                    SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out uint needed, IntPtr.Zero);
                    if (needed == 0) continue;

                    IntPtr buf = Marshal.AllocHGlobal((int)needed);
                    try
                    {
                        // cbSize covers only the fixed part of the struct: 8 on
                        // 64-bit (4-byte size + WCHAR[1] padded), 6 on 32-bit.
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);

                        if (SetupDiGetDeviceInterfaceDetail(set, ref did, buf, needed, out _, IntPtr.Zero))
                        {
                            string? path = Marshal.PtrToStringUni(buf + 4);
                            if (!string.IsNullOrEmpty(path)) paths.Add(path);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buf);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"EnumerateBatteryPaths: {ex.Message}");
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }

            return paths;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BATTERY_WAIT_STATUS
        {
            public uint BatteryTag;
            public uint Timeout;
            public uint PowerState;
            public uint LowCapacity;
            public uint HighCapacity;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BATTERY_STATUS
        {
            public uint PowerState;
            public uint Capacity;
            public uint Voltage;
            public int Rate;        // signed milliwatts
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public uint cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public IntPtr Reserved;
        }

        private const uint DIGCF_PRESENT = 0x02;
        private const uint DIGCF_DEVICEINTERFACE = 0x10;
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 1;
        private const uint FILE_SHARE_WRITE = 2;
        private const uint OPEN_EXISTING = 3;

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator,
                                                         IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
                                                               ref Guid interfaceClassGuid, uint memberIndex,
                                                               ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
                                                                   ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
                                                                   IntPtr deviceInterfaceDetailData,
                                                                   uint detailSize, out uint required,
                                                                   IntPtr deviceInfoData);

        [DllImport("setupapi.dll")]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(string fileName, uint access, uint shareMode,
                                                IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr device, uint controlCode,
                                                   IntPtr inBuffer, uint inSize,
                                                   IntPtr outBuffer, uint outSize,
                                                   out uint bytesReturned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
