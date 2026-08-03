using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WinBatLens.Services
{
    /// <summary>
    /// Everything the battery class driver will tell us about the pack, read
    /// with <c>IOCTL_BATTERY_QUERY_STATUS</c> (instantaneous state) and
    /// <c>IOCTL_BATTERY_QUERY_INFORMATION</c> (the pack's own identity, its
    /// capacities, its temperature and its runtime estimate).
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
    ///
    /// Not every firmware implements every information level. Levels that fail
    /// repeatedly are given up on (see <see cref="MaxLevelFailures"/>) so an
    /// unsupported one costs nothing after the first few ticks, and the caller
    /// is told the value is absent rather than handed a substitute.
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

            /// <summary>
            /// Energy still in the pack, in mWh. Unlike the integer percentage
            /// from GetSystemPowerStatus this has real resolution, and paired
            /// with <see cref="PackInfo.FullChargedCapacityMWh"/> it gives a
            /// time-to-full that needs no assumed pack size.
            /// </summary>
            public int RemainingCapacityMWh { get; init; }
            public bool IsCapacityKnown { get; init; }

            /// <summary>
            /// The pack's own temperature in °C — the battery's, not the CPU
            /// thermal zone that an earlier version mislabelled as the
            /// battery's. Hardware-dependent: plenty of ACPI batteries do not
            /// implement the level at all, hence the companion flag.
            /// </summary>
            public double TemperatureC { get; init; }
            public bool IsTemperatureKnown { get; init; }

            /// <summary>
            /// The driver's own estimate of remaining runtime at the present
            /// rate, in seconds. Unknown while charging or on AC.
            /// </summary>
            public int EstimatedRuntimeSeconds { get; init; }
            public bool IsEstimatedRuntimeKnown { get; init; }
        }

        /// <summary>
        /// What the driver knows about the pack rather than its momentary
        /// state. Capacities and cycle count do drift, so this is re-read on a
        /// slow cadence rather than cached forever.
        /// </summary>
        public sealed class PackInfo
        {
            public static readonly PackInfo None = new PackInfo();

            /// <summary>False on a desktop, or if the driver refused the query.</summary>
            public bool IsValid { get; init; }

            /// <summary>
            /// True when the driver reports capacities in its own arbitrary
            /// units instead of mWh (BATTERY_CAPACITY_RELATIVE). Health as a
            /// ratio is still meaningful; energy in Wh is not, and must not be
            /// shown as though it were.
            /// </summary>
            public bool IsCapacityRelative { get; init; }

            /// <summary>Driver advertises BATTERY_SET_CHARGE_SUPPORTED.</summary>
            public bool SupportsChargeControl { get; init; }

            /// <summary>Four ASCII characters from the pack, e.g. "LION".</summary>
            public string Chemistry { get; init; } = string.Empty;

            public int DesignedCapacityMWh { get; init; }
            public int FullChargedCapacityMWh { get; init; }

            /// <summary>Null when the pack does not count cycles (0 is reported as absent).</summary>
            public int? CycleCount { get; init; }

            public string DeviceName { get; init; } = string.Empty;
            public string ManufactureName { get; init; } = string.Empty;

            /// <summary>
            /// Date the cells were made. powercfg's report does not carry this
            /// at all, and it is the only way to tell a pack that has aged
            /// badly in two years from one that has aged well in six.
            /// </summary>
            public DateTime? ManufactureDate { get; init; }

            /// <summary>Health as full-charge over design capacity, capped at 100%.</summary>
            public double? HealthPercent => DesignedCapacityMWh > 0 && FullChargedCapacityMWh > 0
                ? Math.Min(100.0, Math.Round(FullChargedCapacityMWh / (double)DesignedCapacityMWh * 100.0, 1))
                : null;

            public double? AgeYears => ManufactureDate.HasValue
                ? Math.Round((DateTime.Now - ManufactureDate.Value).TotalDays / 365.25, 1)
                : null;
        }

        private const uint BATTERY_POWER_ON_LINE = 0x00000001;
        private const uint BATTERY_DISCHARGING = 0x00000002;
        private const uint BATTERY_CHARGING = 0x00000004;
        private const int BATTERY_UNKNOWN_RATE = unchecked((int)0x80000000);
        private const uint BATTERY_UNKNOWN_VOLTAGE = 0xFFFFFFFF;
        private const uint BATTERY_UNKNOWN_CAPACITY = 0xFFFFFFFF;
        private const uint BATTERY_UNKNOWN_TIME = 0xFFFFFFFF;

        private const uint BATTERY_CAPACITY_RELATIVE = 0x40000000;
        private const uint BATTERY_SET_CHARGE_SUPPORTED = 0x00000001;

        private const uint IOCTL_BATTERY_QUERY_TAG = 0x294040;
        private const uint IOCTL_BATTERY_QUERY_INFORMATION = 0x294044;
        private const uint IOCTL_BATTERY_QUERY_STATUS = 0x29404C;

        // BATTERY_QUERY_INFORMATION_LEVEL
        private const uint LevelBatteryInformation = 0;
        private const uint LevelTemperature = 2;
        private const uint LevelEstimatedTime = 3;
        private const uint LevelDeviceName = 4;
        private const uint LevelManufactureDate = 5;
        private const uint LevelManufactureName = 6;
        // BatteryUniqueID (level 7) is deliberately not queried: on the pack
        // this was developed against the driver answers with its manufacturer
        // and device name concatenated rather than a serial number, and nothing
        // in the UI has a use for it.

        /// <summary>
        /// How many consecutive IOCTL failures mark an information level as
        /// unimplemented by this firmware. A handful of retries rides out a
        /// transient failure during start-up; after that we stop asking.
        /// </summary>
        private const int MaxLevelFailures = 3;

        /// <summary>
        /// Capacities and cycle count change over hours, not seconds, so the
        /// pack information is re-read once a minute rather than every tick.
        /// </summary>
        private const long PackInfoRefreshMs = 60_000;

        private static Guid _batteryClassGuid = new("72631e54-78a4-11d0-bcf7-00aa00b7b32a");

        // Fixed struct sizes, resolved once. Marshal.SizeOf<T>() is not free
        // enough to call four times a second forever.
        private static readonly int WaitStatusSize = Marshal.SizeOf<BATTERY_WAIT_STATUS>();
        private static readonly int StatusSize = Marshal.SizeOf<BATTERY_STATUS>();
        private static readonly int QueryInfoSize = Marshal.SizeOf<BATTERY_QUERY_INFORMATION>();
        private static readonly int BatteryInfoSize = Marshal.SizeOf<BATTERY_INFORMATION>();
        private static readonly int ManufactureDateSize = Marshal.SizeOf<BATTERY_MANUFACTURE_DATE>();

        private static readonly object _sync = new object();
        private static IntPtr _handle = InvalidHandle;
        private static uint _tag;
        private static bool _initialized;

        private static PackInfo _packInfo = PackInfo.None;
        private static long _lastPackInfoTick;
        private static int _temperatureFailures;
        private static int _estimatedTimeFailures;

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
                RefreshPackInfo();
            }
        }

        /// <summary>
        /// The pack's identity, capacities and cycle count, re-read at most
        /// once a minute. Returns <see cref="PackInfo.None"/> on a machine with
        /// no battery, so callers can test <c>IsValid</c> instead of null.
        /// </summary>
        public static PackInfo GetPackInfo()
        {
            lock (_sync)
            {
                if (!_initialized) { _initialized = true; OpenFirstBattery(); }

                long now = Environment.TickCount64;
                if (!_packInfo.IsValid || now - _lastPackInfoTick >= PackInfoRefreshMs)
                {
                    RefreshPackInfo();
                }

                return _packInfo;
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

                bool capacityKnown = st.Capacity != BATTERY_UNKNOWN_CAPACITY;

                bool tempKnown = TryQueryTemperature(out double tempC);
                bool etaKnown = TryQueryEstimatedRuntime(out int etaSeconds);

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
                    RemainingCapacityMWh = capacityKnown ? (int)st.Capacity : 0,
                    IsCapacityKnown = capacityKnown,
                    TemperatureC = tempKnown ? tempC : 0.0,
                    IsTemperatureKnown = tempKnown,
                    EstimatedRuntimeSeconds = etaKnown ? etaSeconds : 0,
                    IsEstimatedRuntimeKnown = etaKnown,
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

            // Everything below was learned about the device behind that handle:
            // a different pack may implement a different set of levels, so both
            // the cached information and the give-up counters start over.
            _packInfo = PackInfo.None;
            _lastPackInfoTick = 0;
            _temperatureFailures = 0;
            _estimatedTimeFailures = 0;
        }

        private static bool TryQueryStatus(out BATTERY_STATUS status)
        {
            status = default;

            // Every struct here is blittable, so the P/Invoke marshaller can
            // pin the locals and hand the driver a pointer straight to the
            // stack. The previous AllocHGlobal / StructureToPtr / PtrToStructure
            // round-trip put two native allocations and two struct copies on
            // the once-a-second path for no benefit.
            var wait = new BATTERY_WAIT_STATUS { BatteryTag = _tag };
            try
            {
                return DeviceIoControl(_handle, IOCTL_BATTERY_QUERY_STATUS,
                                       ref wait, (uint)WaitStatusSize,
                                       out status, (uint)StatusSize, out _, IntPtr.Zero);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Pack temperature, converted from the driver's tenths of a degree
        /// Kelvin. Values outside -40..80 °C are rejected: firmware that does
        /// not really implement the level tends to answer 0, which would
        /// otherwise render as a plausible-looking -273 °C.
        /// </summary>
        private static bool TryQueryTemperature(out double celsius)
        {
            celsius = 0.0;
            if (_temperatureFailures >= MaxLevelFailures) return false;

            if (!TryQueryInfoUInt32(LevelTemperature, out uint tenthsKelvin))
            {
                _temperatureFailures++;
                return false;
            }

            _temperatureFailures = 0;

            double c = tenthsKelvin / 10.0 - 273.15;
            if (c < -40.0 || c > 80.0) return false;

            celsius = Math.Round(c, 1);
            return true;
        }

        /// <summary>
        /// The driver's own runtime estimate at the present rate. Reported as
        /// BATTERY_UNKNOWN_TIME whenever the pack is not discharging, which is
        /// an ordinary answer rather than a failed level — so it does not count
        /// towards giving up on the query.
        /// </summary>
        private static bool TryQueryEstimatedRuntime(out int seconds)
        {
            seconds = 0;
            if (_estimatedTimeFailures >= MaxLevelFailures) return false;

            // AtRate 0 means "at the rate the pack is draining right now".
            if (!TryQueryInfoUInt32(LevelEstimatedTime, out uint value, atRate: 0))
            {
                _estimatedTimeFailures++;
                return false;
            }

            _estimatedTimeFailures = 0;

            if (value == BATTERY_UNKNOWN_TIME || value == 0) return false;

            // A day or more is the firmware saying "no idea", not a forecast.
            if (value >= 86400) return false;

            seconds = (int)value;
            return true;
        }

        private static void RefreshPackInfo()
        {
            _lastPackInfoTick = Environment.TickCount64;

            if (_handle == InvalidHandle)
            {
                _packInfo = PackInfo.None;
                return;
            }

            if (!TryQueryBatteryInformation(out BATTERY_INFORMATION bi))
            {
                _packInfo = PackInfo.None;
                return;
            }

            bool relative = (bi.Capabilities & BATTERY_CAPACITY_RELATIVE) != 0;

            // Strings and the manufacture date never change for a given pack,
            // so they are carried over instead of re-read every refresh.
            bool reuseIdentity = _packInfo.IsValid;

            _packInfo = new PackInfo
            {
                IsValid = true,
                IsCapacityRelative = relative,
                SupportsChargeControl = (bi.Capabilities & BATTERY_SET_CHARGE_SUPPORTED) != 0,
                Chemistry = ReadChemistry(bi),
                DesignedCapacityMWh = bi.DesignedCapacity == BATTERY_UNKNOWN_CAPACITY ? 0 : (int)bi.DesignedCapacity,
                FullChargedCapacityMWh = bi.FullChargedCapacity == BATTERY_UNKNOWN_CAPACITY ? 0 : (int)bi.FullChargedCapacity,
                CycleCount = bi.CycleCount > 0 ? (int)bi.CycleCount : null,
                DeviceName = reuseIdentity ? _packInfo.DeviceName : QueryString(LevelDeviceName),
                ManufactureName = reuseIdentity ? _packInfo.ManufactureName : QueryString(LevelManufactureName),
                ManufactureDate = reuseIdentity ? _packInfo.ManufactureDate : QueryManufactureDate(),
            };
        }

        private static string ReadChemistry(BATTERY_INFORMATION bi)
        {
            // Four ASCII bytes, not null-terminated: "LION", "LiP", "NiMH"…
            var chars = new[] { bi.Chemistry0, bi.Chemistry1, bi.Chemistry2, bi.Chemistry3 };
            var sb = new System.Text.StringBuilder(4);
            foreach (byte b in chars)
            {
                if (b == 0) break;
                if (b < 0x20 || b > 0x7E) continue;
                sb.Append((char)b);
            }
            return sb.ToString().Trim();
        }

        private static bool TryQueryBatteryInformation(out BATTERY_INFORMATION info)
        {
            info = default;
            if (_handle == InvalidHandle) return false;

            var query = NewQuery(LevelBatteryInformation, 0);
            try
            {
                return DeviceIoControl(_handle, IOCTL_BATTERY_QUERY_INFORMATION,
                                       ref query, (uint)QueryInfoSize,
                                       out info, (uint)BatteryInfoSize, out _, IntPtr.Zero);
            }
            catch
            {
                return false;
            }
        }

        private static DateTime? QueryManufactureDate()
        {
            if (_handle == InvalidHandle) return null;

            var query = NewQuery(LevelManufactureDate, 0);
            try
            {
                if (!DeviceIoControl(_handle, IOCTL_BATTERY_QUERY_INFORMATION,
                                     ref query, (uint)QueryInfoSize,
                                     out BATTERY_MANUFACTURE_DATE date, (uint)ManufactureDateSize,
                                     out _, IntPtr.Zero))
                    return null;

                if (date.Year < 1990 || date.Year > 2100) return null;
                if (date.Month < 1 || date.Month > 12) return null;
                if (date.Day < 1 || date.Day > 31) return null;

                return new DateTime(date.Year, date.Month, date.Day);
            }
            catch
            {
                return null;
            }
        }

        private static string QueryString(uint level)
        {
            // 256 wide characters is far more than any of these fields uses,
            // and one allocation is cheaper than probing for the exact size.
            const int outSize = 512;
            IntPtr outBuf = Marshal.AllocHGlobal(outSize);
            try
            {
                if (!QueryInfoLevel(level, 0, outBuf, outSize, out uint returned)) return string.Empty;
                if (returned == 0) return string.Empty;

                return (Marshal.PtrToStringUni(outBuf) ?? string.Empty).Trim();
            }
            catch
            {
                return string.Empty;
            }
            finally
            {
                Marshal.FreeHGlobal(outBuf);
            }
        }

        /// <summary>
        /// The temperature and estimated-runtime levels, both of which answer a
        /// single 32-bit value. Called on every tick, so like the status query
        /// it marshals through pinned locals rather than the heap.
        /// </summary>
        private static bool TryQueryInfoUInt32(uint level, out uint value, int atRate = 0)
        {
            value = 0;
            if (_handle == InvalidHandle) return false;

            var query = NewQuery(level, atRate);
            try
            {
                return DeviceIoControl(_handle, IOCTL_BATTERY_QUERY_INFORMATION,
                                       ref query, (uint)QueryInfoSize,
                                       out value, sizeof(uint), out _, IntPtr.Zero);
            }
            catch
            {
                return false;
            }
        }

        private static BATTERY_QUERY_INFORMATION NewQuery(uint level, int atRate) =>
            new BATTERY_QUERY_INFORMATION
            {
                BatteryTag = _tag,
                InformationLevel = level,
                AtRate = atRate,
            };

        /// <summary>
        /// One IOCTL_BATTERY_QUERY_INFORMATION call into a caller-owned native
        /// buffer, for the variable-length string levels. Those are read once
        /// per pack rather than per tick, so the buffer stays on the heap.
        /// Callers hold _sync and have already checked that the handle is open.
        /// </summary>
        private static bool QueryInfoLevel(uint level, int atRate, IntPtr outBuf, int outSize, out uint bytesReturned)
        {
            bytesReturned = 0;
            if (_handle == InvalidHandle) return false;

            var query = NewQuery(level, atRate);
            try
            {
                return DeviceIoControl(_handle, IOCTL_BATTERY_QUERY_INFORMATION,
                                       ref query, (uint)QueryInfoSize,
                                       outBuf, (uint)outSize, out bytesReturned, IntPtr.Zero);
            }
            catch
            {
                return false;
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
            uint timeout = 0;    // zero timeout: do not wait
            try
            {
                if (!DeviceIoControl(h, IOCTL_BATTERY_QUERY_TAG, ref timeout, sizeof(uint),
                                     out uint value, sizeof(uint), out _, IntPtr.Zero))
                    return false;

                tag = value;
                return tag != 0;
            }
            catch
            {
                return false;
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
        private struct BATTERY_QUERY_INFORMATION
        {
            public uint BatteryTag;
            public uint InformationLevel;   // BATTERY_QUERY_INFORMATION_LEVEL
            public int AtRate;              // mW to assume; 0 means the present rate
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BATTERY_INFORMATION
        {
            public uint Capabilities;
            public byte Technology;
            // UCHAR Reserved[3] and UCHAR Chemistry[4], spelled out one byte at
            // a time so the struct marshals without a fixed-size-array attribute.
            public byte Reserved0;
            public byte Reserved1;
            public byte Reserved2;
            public byte Chemistry0;
            public byte Chemistry1;
            public byte Chemistry2;
            public byte Chemistry3;
            public uint DesignedCapacity;
            public uint FullChargedCapacity;
            public uint DefaultAlert1;
            public uint DefaultAlert2;
            public uint CriticalBias;
            public uint CycleCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BATTERY_MANUFACTURE_DATE
        {
            public byte Day;
            public byte Month;
            public ushort Year;
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

        // One overload per struct pair actually used. All of these types are
        // blittable, so each call marshals as a pinned pointer to a stack local
        // with no copy, no native allocation and no reflection.

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr device, uint controlCode,
                                                   ref BATTERY_WAIT_STATUS inBuffer, uint inSize,
                                                   out BATTERY_STATUS outBuffer, uint outSize,
                                                   out uint bytesReturned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr device, uint controlCode,
                                                   ref BATTERY_QUERY_INFORMATION inBuffer, uint inSize,
                                                   out BATTERY_INFORMATION outBuffer, uint outSize,
                                                   out uint bytesReturned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr device, uint controlCode,
                                                   ref BATTERY_QUERY_INFORMATION inBuffer, uint inSize,
                                                   out BATTERY_MANUFACTURE_DATE outBuffer, uint outSize,
                                                   out uint bytesReturned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr device, uint controlCode,
                                                   ref BATTERY_QUERY_INFORMATION inBuffer, uint inSize,
                                                   out uint outBuffer, uint outSize,
                                                   out uint bytesReturned, IntPtr overlapped);

        /// <summary>The variable-length string levels, into a native buffer.</summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr device, uint controlCode,
                                                   ref BATTERY_QUERY_INFORMATION inBuffer, uint inSize,
                                                   IntPtr outBuffer, uint outSize,
                                                   out uint bytesReturned, IntPtr overlapped);

        /// <summary>IOCTL_BATTERY_QUERY_TAG: a timeout in, a tag out.</summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr device, uint controlCode,
                                                   ref uint inBuffer, uint inSize,
                                                   out uint outBuffer, uint outSize,
                                                   out uint bytesReturned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
