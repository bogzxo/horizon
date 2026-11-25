using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Silk.NET.SDL;

namespace Horizon.Platform.Desktop;

/// <summary>
/// Keeps the left mouse-down <c>NSEvent</c> that SDL turned into a button press, for <c>performWindowDragWithEvent:</c>,
/// which wants the event that started the drag. The title bar's press reaches the window code a tick later (the UI is on
/// another thread), when <c>[NSApp currentEvent]</c> is long another event, so the watch (on the main thread, inside AppKit's
/// <c>sendEvent:</c>, the same way as <see cref="MacScrollWatch"/>) retains it as it passes.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe class MacMouseDownWatch
{
    private const uint SDL_MOUSEBUTTONDOWN = 0x401;

    /// <summary>NSEventTypeLeftMouseDown.</summary>
    private const nint LEFT_MOUSE_DOWN = 1;

    private static int vfixMotion; // [VFIX]
    private static nint last;
    private static int installed;

    /// <summary>Starts watching SDL's events for a left press, once.</summary>
    public static void Install()
    {
        if (!OperatingSystem.IsMacOS() || Interlocked.Exchange(ref installed, 1) != 0)
            return;

        try
        {
            SdlProvider.SDL.Value.AddEventWatch(new PfnEventFilter(&Watch), null);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // no SDL to ask, the window's own tracker moves the window
        }
    }

    /// <summary>The last left mouse-down event (owned by the watch, valid until the next press), 0 for none. Main thread.</summary>
    public static nint Last => Volatile.Read(ref last);

    /// <summary>Lets go of the kept event, for a window that is gone.</summary>
    public static void Clear() => Keep(0);

    private static void Keep(nint press)
    {
        nint old = Interlocked.Exchange(ref last, press);
        MacObjC.Release(old);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Watch(void* userdata, Event* e)
    {
        try
        {
            if (e->Type == 0x400) Interlocked.Increment(ref vfixMotion); // [VFIX]
            if (e->Type == 0x401 || e->Type == 0x402) Console.Error.WriteLine("[VFIX] button type=0x" + ((uint)e->Type).ToString("x") + " motionSoFar=" + vfixMotion); // [VFIX]
            if (e->Type != SDL_MOUSEBUTTONDOWN || e->Button.Button != 1)
                return 1;

            nint current = MacObjC.Send(MacObjC.SharedApplication, MacObjC.Sel("currentEvent"));
            if (current != 0 && MacObjC.SendInt(current, MacObjC.Sel("type")) == LEFT_MOUSE_DOWN)
                Keep(MacObjC.Retain(current));
        }
        catch (Exception)
        {
            // a press that couldn't be read is a window moved by the tracker
        }

        return 1;
    }
}
