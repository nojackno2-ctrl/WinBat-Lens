using System;
using System.Runtime.InteropServices;

namespace WinBatLens.Services
{
    /// <summary>
    /// Windows' own verdict on whether the attached charger can actually run
    /// this machine, from <c>Windows.System.Power.PowerManager</c>.
    /// </summary>
    public enum PowerSupplyCapability
    {
        /// <summary>The API could not be reached; nothing may be inferred.</summary>
        Unknown,

        /// <summary>Running on battery — no external supply attached.</summary>
        NotPresent,

        /// <summary>
        /// A supply is attached but cannot meet the system's demand. On a
        /// USB-C laptop this is the classic under-powered PD charger.
        /// </summary>
        Inadequate,

        /// <summary>The attached supply covers the system's demand.</summary>
        Adequate,
    }

    /// <summary>
    /// Reads <c>PowerManager.PowerSupplyStatus</c>, the only documented,
    /// unelevated signal Windows gives about the charger itself rather than
    /// about the battery.
    /// </summary>
    /// <remarks>
    /// WHY THIS IS THE BEST AVAILABLE CHARGER SIGNAL
    /// <para>
    /// The obvious thing to want is the USB-C Power Delivery contract — the
    /// negotiated volts and amps that give a real adapter wattage. It is not
    /// obtainable from a normal user-mode process on Windows, which was
    /// verified against this machine (ASUS ROG Zephyrus G14, charging over
    /// USB-C) rather than assumed:
    /// </para>
    /// <list type="bullet">
    /// <item>The UCM-UCSI ACPI device (<c>ACPI\USBC000</c>) is present, but the
    /// only device interfaces it registers are absent from the public SDK —
    /// they are driver-to-driver, with no documented user-mode IOCTL.</item>
    /// <item><c>BATTERY_USB_CHARGER_STATUS</c> in poclass.h does carry the PD
    /// contract flag, the port's mA and its mV — but it travels in the
    /// <c>IOCTL_BATTERY_SET_INFORMATION</c> direction, pushed in by a Charging
    /// Arbitration Driver that desktop laptops do not have. There is no
    /// matching query level.</item>
    /// <item><c>POWER_ADAPTER_STATUS.MaxOutputPower</c> is the adapter's rated
    /// wattage, but batclass.h only exposes it through a kernel-mode adapter
    /// miniclass callback. The generic Microsoft AC Adapter driver on
    /// <c>ACPI\ACPI0003</c> does not surface it.</item>
    /// <item>The battery's Customized I/O levels, the escape hatch for OEM
    /// values, answer <c>SupportedInputs = 0</c> here — nothing exposed.</item>
    /// <item>ASUS' own ATK WMI knows the charge source, but every query to it
    /// is access-denied unelevated, and this app deliberately runs
    /// unelevated.</item>
    /// </list>
    /// <para>
    /// So no adapter wattage is reported anywhere in this app: consistent with
    /// the rest of the dashboard, an unobtainable number is left out rather
    /// than estimated. What Windows will answer is whether the supply is
    /// keeping up, and that pairs with the pack's own measured rate to tell the
    /// whole story — see <see cref="Models.RealTimePowerState.IsChargerDeficit"/>.
    /// </para>
    /// <para>
    /// Reached through raw WinRT activation instead of the C# projection on
    /// purpose. Using the projection would mean moving the project from
    /// <c>net8.0-windows</c> to a <c>net8.0-windows10.0.x</c> target, which
    /// drags the whole Windows SDK projection assembly into a single-file
    /// bundle whose size and cold-start time this project measures and tunes.
    /// One IID and one vtable slot cost nothing by comparison. Measured on this
    /// machine: 0.022 us per read, so it is taken fresh every tick with no
    /// caching.
    /// </para>
    /// </remarks>
    public static class PowerSupplyService
    {
        private const string PowerManagerClassName = "Windows.System.Power.PowerManager";

        private static Guid IID_IPowerManagerStatics = new("1394825D-62CE-4364-98D5-AA28C7FBD15B");

        // IInspectable takes vtable slots 0-5. IPowerManagerStatics then
        // declares EnergySaverStatus (get/add/remove) at 6-8, BatteryStatus at
        // 9-11, and PowerSupplyStatus' getter at 12.
        private const int VtblSlotGetPowerSupplyStatus = 12;

        private const int RO_INIT_MULTITHREADED = 1;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetEnumProperty(IntPtr thisPtr, out int value);

        private static readonly object _sync = new object();
        private static IntPtr _factory = IntPtr.Zero;
        private static GetEnumProperty? _getPowerSupplyStatus;
        private static bool _attempted;

        /// <summary>True once the WinRT factory has been obtained.</summary>
        public static bool IsAvailable => _getPowerSupplyStatus != null;

        /// <summary>
        /// Obtains the activation factory. Slow enough (COM/WinRT activation)
        /// to belong on the background warmup thread rather than the first UI
        /// tick, like the rest of the sensor stack.
        /// </summary>
        public static void Initialize()
        {
            lock (_sync)
            {
                // Nothing is held, so either this is the first call or a
                // previous Shutdown() parked the service; both want a fresh
                // activation. When a factory is already held this is a no-op,
                // so calling Initialize twice cannot leak a second reference.
                if (_factory == IntPtr.Zero) _attempted = false;
                EnsureFactory();
            }
        }

        /// <summary>
        /// Windows' current verdict on the attached supply. Returns
        /// <see cref="PowerSupplyCapability.Unknown"/> — never a guess — if the
        /// API is unavailable or the call fails.
        /// </summary>
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

                // Windows.System.Power.PowerSupplyStatus
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

                // Stays parked: a read arriving after shutdown (a late timer
                // tick during teardown) must answer Unknown rather than quietly
                // activating COM again on the way out.
                _attempted = true;
            }
        }

        /// <summary>Callers hold <see cref="_sync"/>.</summary>
        private static void EnsureFactory()
        {
            if (_attempted) return;
            _attempted = true;

            IntPtr classId = IntPtr.Zero;
            try
            {
                // S_FALSE (already initialised) and RPC_E_CHANGED_MODE (the WPF
                // UI thread is an STA) are both fine: all we need is that the
                // calling thread belongs to some apartment. PowerManager is
                // declared agile, so the factory is safe to call from any
                // thread once obtained.
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
                // A stripped-down or future Windows that no longer activates
                // this class must leave the app running on battery telemetry
                // alone, so failure here is silent and permanent.
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

        [DllImport("combase.dll")]
        private static extern int RoInitialize(int initType);

        [DllImport("combase.dll", CharSet = CharSet.Unicode)]
        private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

        [DllImport("combase.dll")]
        private static extern int WindowsDeleteString(IntPtr hstring);

        [DllImport("combase.dll")]
        private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);
    }
}
