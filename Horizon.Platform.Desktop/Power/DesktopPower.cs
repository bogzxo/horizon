using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Horizon.Platform;

namespace Horizon.Platform.Desktop;

/// <summary>
/// Cross-platform desktop implementation of <see cref="IPlatformPower"/>. Automatically registers itself with
/// <see cref="PlatformPower"/> when <c>Horizon.Platform.Desktop</c> loads so macOS Low Power Mode, Windows Energy Saver,
/// and Linux power supply / ACPI profiles are available immediately without reflection or child processes.
/// </summary>
public static class DesktopPower
{
    private static int initialized;
    private static IPlatformPower? provider;

    [ModuleInitializer]
    internal static void Install() => EnsureInitialized();

    /// <summary>Ensures the OS-appropriate desktop power provider is registered with <see cref="PlatformPower"/>.</summary>
    public static void EnsureInitialized()
    {
        if (Interlocked.Exchange(ref initialized, 1) == 0)
        {
            if (OperatingSystem.IsMacOS())
            {
                var mac = new MacPower();
                provider = mac;
                PlatformPower.Register(mac);
            }
            else if (OperatingSystem.IsWindows())
            {
                var win = new Win32Power();
                provider = win;
                PlatformPower.Register(win);
            }
            else if (OperatingSystem.IsLinux())
            {
                var linux = new LinuxPower();
                provider = linux;
                PlatformPower.Register(linux);
            }
        }

        if (OperatingSystem.IsMacOS() && provider is MacPower macPower)
            macPower.EnsureSubscribed();
    }

    [SupportedOSPlatform("macos")]
    private sealed unsafe class MacPower : IPlatformPower
    {
        private const string IOKitPath = "/System/Library/Frameworks/IOKit.framework/IOKit";
        private const string CoreFoundationPath = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const uint kCFStringEncodingUTF8 = 0x08000100;
        private const long BATTERY_CACHE_MS = 30_000;

        private static MacPower? active;
        private static nint observer;

        private static int ioKitInit;
        private static delegate* unmanaged[Cdecl]<nint> iopsCopyPowerSourcesInfo;
        private static delegate* unmanaged[Cdecl]<nint, nint> iopsGetProvidingPowerSourceType;
        private static delegate* unmanaged[Cdecl]<nint, void> cfRelease;
        private static delegate* unmanaged[Cdecl]<nint, byte*, nint, uint, byte> cfStringGetCString;

        private volatile bool isLowPowerMode;
        private volatile bool isOnBattery;
        private long nextBatteryCheckMs;
        private int subscribed;

        public event Action? Changed;

        public MacPower()
        {
            active = this;
            RefreshLowPowerMode();
            RefreshBattery(force: true);
            EnsureSubscribed();
        }

        public bool IsOnBattery
        {
            get
            {
                RefreshBattery(force: false);
                return isOnBattery;
            }
        }

        public bool IsLowPowerMode => isLowPowerMode;

        public void EnsureSubscribed()
        {
            if (Volatile.Read(ref subscribed) != 0)
                return;

            MacMain.Run(() =>
            {
                if (Interlocked.Exchange(ref subscribed, 1) != 0)
                    return;

                try
                {
                    if (observer == 0)
                    {
                        nint cls = MacClassBuilder.Find("HorizonPowerObserver");
                        if (cls == 0)
                        {
                            var builder = new MacClassBuilder("HorizonPowerObserver");
                            builder.AddMethod(
                                "powerStateChanged:",
                                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnPowerStateChanged,
                                "v@:@");
                            cls = builder.Register();
                        }

                        observer = MacObjC.New(cls);
                    }

                    nint centre = MacObjC.Send(MacObjC.Class("NSNotificationCenter"), MacObjC.Sel("defaultCenter"));
                    if (centre != 0 && observer != 0)
                    {
                        AddObserver(
                            centre,
                            MacObjC.Sel("addObserver:selector:name:object:"),
                            observer,
                            MacObjC.Sel("powerStateChanged:"),
                            MacObjC.NSString("NSProcessInfoPowerStateDidChangeNotification"),
                            0);
                    }

                    RefreshLowPowerMode();
                    RefreshBattery(force: true);
                }
                catch (Exception)
                {
                    Volatile.Write(ref subscribed, 0);
                }
            });
        }

        private void RefreshLowPowerMode()
        {
            try
            {
                using var pool = MacObjC.Pool();
                nint info = MacObjC.Send(MacObjC.Class("NSProcessInfo"), MacObjC.Sel("processInfo"));
                nint sel = MacObjC.Sel("isLowPowerModeEnabled");
                if (info != 0 && MacObjC.RespondsTo(info, sel))
                    isLowPowerMode = MacObjC.SendBool(info, sel);
            }
            catch (Exception)
            {
                // Keep last known value if Foundation cannot be queried yet
            }
        }

        private void RefreshBattery(bool force)
        {
            long now = Environment.TickCount64;
            if (!force && now < Volatile.Read(ref nextBatteryCheckMs))
                return;

            Volatile.Write(ref nextBatteryCheckMs, now + BATTERY_CACHE_MS);
            isOnBattery = QueryOnBattery();
        }

        private static bool QueryOnBattery()
        {
            EnsureIOKitLoaded();
            if (iopsCopyPowerSourcesInfo == null
                || iopsGetProvidingPowerSourceType == null
                || cfRelease == null
                || cfStringGetCString == null)
            {
                return false;
            }

            nint snapshot = iopsCopyPowerSourcesInfo();
            if (snapshot == 0)
                return false;

            try
            {
                nint sourceType = iopsGetProvidingPowerSourceType(snapshot);
                if (sourceType == 0)
                    return false;

                byte* buffer = stackalloc byte[64];
                if (cfStringGetCString(sourceType, buffer, 64, kCFStringEncodingUTF8) == 0)
                    return false;

                ReadOnlySpan<byte> expected = "Battery Power"u8;
                return buffer[expected.Length] == 0
                    && new ReadOnlySpan<byte>(buffer, expected.Length).SequenceEqual(expected);
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                cfRelease(snapshot);
            }
        }

        private static void EnsureIOKitLoaded()
        {
            if (Interlocked.Exchange(ref ioKitInit, 1) != 0)
                return;

            if (!NativeLibrary.TryLoad(IOKitPath, out nint ioKit)
                || !NativeLibrary.TryLoad(CoreFoundationPath, out nint cf))
            {
                return;
            }

            if (NativeLibrary.TryGetExport(ioKit, "IOPSCopyPowerSourcesInfo", out nint copyInfo)
                && NativeLibrary.TryGetExport(ioKit, "IOPSGetProvidingPowerSourceType", out nint getType)
                && NativeLibrary.TryGetExport(cf, "CFRelease", out nint release)
                && NativeLibrary.TryGetExport(cf, "CFStringGetCString", out nint getCString))
            {
                iopsCopyPowerSourcesInfo = (delegate* unmanaged[Cdecl]<nint>)copyInfo;
                iopsGetProvidingPowerSourceType = (delegate* unmanaged[Cdecl]<nint, nint>)getType;
                cfRelease = (delegate* unmanaged[Cdecl]<nint, void>)release;
                cfStringGetCString = (delegate* unmanaged[Cdecl]<nint, byte*, nint, uint, byte>)getCString;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void OnPowerStateChanged(nint self, nint selector, nint notification)
        {
            try
            {
                if (active is { } current)
                {
                    current.RefreshLowPowerMode();
                    current.RefreshBattery(force: true);
                    current.Changed?.Invoke();
                }
            }
            catch (Exception)
            {
                // Never allow managed exceptions to cross an UnmanagedCallersOnly boundary
            }
        }

        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern void AddObserver(nint centre, nint selector, nint observer, nint action, nint name, nint source);
    }

    [SupportedOSPlatform("windows")]
    private sealed class Win32Power : IPlatformPower
    {
        private const long POLL_INTERVAL_MS = 10_000;

        private volatile bool isOnBattery;
        private volatile bool isLowPowerMode;
        private long nextPollMs;
        private int hasPolled;

        public event Action? Changed;

        public bool IsOnBattery
        {
            get
            {
                PollIfStale();
                return isOnBattery;
            }
        }

        public bool IsLowPowerMode
        {
            get
            {
                PollIfStale();
                return isLowPowerMode;
            }
        }

        private void PollIfStale()
        {
            long now = Environment.TickCount64;
            if (now < Volatile.Read(ref nextPollMs))
                return;

            Volatile.Write(ref nextPollMs, now + POLL_INTERVAL_MS);

            if (GetSystemPowerStatus(out SYSTEM_POWER_STATUS status) == 0)
                return;

            bool onBattery = status.ACLineStatus == 0;
            bool lowPower = status.SystemStatusFlag == 1;
            bool wasPolled = Interlocked.Exchange(ref hasPolled, 1) != 0;
            bool changed = wasPolled && (onBattery != isOnBattery || lowPower != isLowPowerMode);

            isOnBattery = onBattery;
            isLowPowerMode = lowPower;

            if (changed)
                Changed?.Invoke();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public uint BatteryLifeTime;
            public uint BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern int GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
    }

    private sealed class LinuxPower : IPlatformPower
    {
        private const long POLL_INTERVAL_MS = 15_000;
        private const string PowerSupplyRoot = "/sys/class/power_supply";
        private const string PlatformProfilePath = "/sys/firmware/acpi/platform_profile";

        private volatile bool isOnBattery;
        private volatile bool isLowPowerMode;
        private long nextPollMs;
        private int hasPolled;

        public event Action? Changed;

        public bool IsOnBattery
        {
            get
            {
                PollIfStale();
                return isOnBattery;
            }
        }

        public bool IsLowPowerMode
        {
            get
            {
                PollIfStale();
                return isLowPowerMode;
            }
        }

        private void PollIfStale()
        {
            long now = Environment.TickCount64;
            if (now < Volatile.Read(ref nextPollMs))
                return;

            Volatile.Write(ref nextPollMs, now + POLL_INTERVAL_MS);

            bool onBattery = ReadLinuxOnBattery();
            bool lowPower = ReadLinuxLowPower();
            bool wasPolled = Interlocked.Exchange(ref hasPolled, 1) != 0;
            bool changed = wasPolled && (onBattery != isOnBattery || lowPower != isLowPowerMode);

            isOnBattery = onBattery;
            isLowPowerMode = lowPower;

            if (changed)
                Changed?.Invoke();
        }

        private static bool ReadLinuxOnBattery()
        {
            try
            {
                if (!Directory.Exists(PowerSupplyRoot))
                    return false;

                bool sawMains = false;
                bool anyOnline = false;
                foreach (string entry in Directory.EnumerateDirectories(PowerSupplyRoot))
                {
                    string typePath = Path.Combine(entry, "type");
                    if (!File.Exists(typePath))
                        continue;

                    string type = File.ReadAllText(typePath).Trim();
                    if (!string.Equals(type, "Mains", StringComparison.OrdinalIgnoreCase))
                        continue;

                    sawMains = true;
                    string onlinePath = Path.Combine(entry, "online");
                    if (!File.Exists(onlinePath))
                        continue;

                    string online = File.ReadAllText(onlinePath).Trim();
                    if (online.Length > 0 && online[0] == '1')
                        anyOnline = true;
                }

                return sawMains && !anyOnline;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool ReadLinuxLowPower()
        {
            try
            {
                if (!File.Exists(PlatformProfilePath))
                    return false;

                string profile = File.ReadAllText(PlatformProfilePath);
                return profile.Contains("low-power", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
