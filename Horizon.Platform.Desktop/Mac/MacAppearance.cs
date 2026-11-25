using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Horizon.Platform;

namespace Horizon.Platform.Desktop;

/// <summary>
/// A Mac's appearance: dark or light (<c>[NSApp effectiveAppearance]</c>) and the accent colour (<c>[NSColor
/// controlAccentColor]</c> in sRGB). It is told of a change by the distributed notification
/// <c>AppleInterfaceThemeChangedNotification</c> and <c>NSSystemColorsDidChangeNotification</c>, which an observer object
/// of the program's own class hears, on the main thread. AppKit is only read on the main thread (<see cref="MacMain"/>), the
/// values are kept for any thread to look at.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed unsafe class MacAppearance : IPlatformAppearance
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    // addObserver:selector:name:object: has four arguments, one more than MacObjC.Send takes
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendObserver(nint receiver, nint selector, nint observer, nint action, nint name, nint sender);

    private static MacAppearance? current;
    private static nint observerClass;

    private volatile bool isDark;
    private (float R, float G, float B)? accent;
    private readonly object gate = new();
    private nint observer;
    private Timer? lag;

    private MacAppearance()
    {
    }

    /// <summary>Makes it and starts listening, null where there is no Mac or AppKit isn't up. The main thread.</summary>
    public static MacAppearance? Create()
    {
        if (!OperatingSystem.IsMacOS())
            return null;

        try
        {
            var made = new MacAppearance();
            using (MacObjC.Pool())
            {
                made.Read(raise: false);
                made.Observe();
            }

            current = made;
            return made;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public bool IsDark => isDark;

    /// <inheritdoc />
    public (float R, float G, float B)? Accent { get { lock (gate) return accent; } }

    /// <inheritdoc />
    public event Action? Changed;

    /// <summary>Reads what the system says now, and tells the listeners when it isn't what was kept. The main thread.</summary>
    internal void Read(bool raise)
    {
        bool dark = ReadIsDark();
        var color = ReadAccent();
        bool changed;
        lock (gate)
        {
            changed = dark != isDark || color != accent;
            isDark = dark;
            accent = color;
        }

        if (raise && changed)
            Changed?.Invoke();
    }

    /// <summary>Whether the app's effective appearance is Dark Aqua. The main thread.</summary>
    public static bool ReadIsDark()
    {
        nint app = MacObjC.SharedApplication;
        if (app == 0)
            return false;

        nint appearance = MacObjC.Send(app, MacObjC.Sel("effectiveAppearance"));
        if (appearance == 0)
            return false;

        nint names = MacObjC.Send(MacObjC.Class("NSArray"), MacObjC.Sel("arrayWithObject:"), MacObjC.NSString("NSAppearanceNameAqua"));
        names = MacObjC.Send(names, MacObjC.Sel("arrayByAddingObject:"), MacObjC.NSString("NSAppearanceNameDarkAqua"));
        nint best = MacObjC.Send(appearance, MacObjC.Sel("bestMatchFromAppearancesWithNames:"), names);
        return best != 0 && MacObjC.ToManaged(best) == "NSAppearanceNameDarkAqua";
    }

    /// <summary>The accent colour as sRGB, null when it can't be had. The main thread.</summary>
    public static (float R, float G, float B)? ReadAccent()
    {
        nint color = MacObjC.Send(MacObjC.Class("NSColor"), MacObjC.Sel("controlAccentColor"));
        if (color == 0)
            return null;

        nint srgb = MacObjC.Send(color, MacObjC.Sel("colorUsingColorSpace:"),
            MacObjC.Send(MacObjC.Class("NSColorSpace"), MacObjC.Sel("sRGBColorSpace")));
        if (srgb == 0)
            return null;

        return ((float)MacObjC.SendDouble(srgb, MacObjC.Sel("redComponent")),
            (float)MacObjC.SendDouble(srgb, MacObjC.Sel("greenComponent")),
            (float)MacObjC.SendDouble(srgb, MacObjC.Sel("blueComponent")));
    }

    private void Observe()
    {
        if (observerClass == 0)
        {
            observerClass = new MacClassBuilder("HorizonAppearanceObserver")
                .AddMethod("appearanceChanged:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&AppearanceChanged, "v@:@")
                .Register();
        }

        observer = MacObjC.New(observerClass);
        nint action = MacObjC.Sel("appearanceChanged:");
        nint distributed = MacObjC.Send(MacObjC.Class("NSDistributedNotificationCenter"), MacObjC.Sel("defaultCenter"));
        SendObserver(distributed, MacObjC.Sel("addObserver:selector:name:object:"), observer, action,
            MacObjC.NSString("AppleInterfaceThemeChangedNotification"), 0);
        nint center = MacObjC.Send(MacObjC.Class("NSNotificationCenter"), MacObjC.Sel("defaultCenter"));
        SendObserver(center, MacObjC.Sel("addObserver:selector:name:object:"), observer, action,
            MacObjC.NSString("NSSystemColorsDidChangeNotification"), 0);
    }

    /// <summary>Whether the observer is registered, for a test of a Mac's.</summary>
    internal bool IsObserving => observer != 0;

    /// <summary>What the observer does, as the notification arrives (the main thread): looks now, and once more soon after, as AppKit's appearance can lag the notice.</summary>
    internal void Notified()
    {
        Read(raise: true);
        lag?.Dispose();
        lag = new Timer(static _ => MacMain.Run(static () => current?.Read(raise: true)), null, 300, Timeout.Infinite);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void AppearanceChanged(nint self, nint selector, nint notification)
    {
        try
        {
            current?.Notified();
        }
        catch (Exception)
        {
            // a notice that couldn't be read is a change not seen, not a crash of AppKit's
        }
    }
}
