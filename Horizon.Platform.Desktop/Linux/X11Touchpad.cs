using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Horizon.Input;
using Silk.NET.SDL;

namespace Horizon.Platform.Desktop;

/// <summary>
/// Native X11 XInput 2.4 touchpad gesture support (<c>XI_GesturePinch*</c> and <c>XI_GestureSwipe*</c>)
/// for Linux X11 and XWayland sessions. Merges the XI 2.4 gesture mask with SDL's existing XI2 selection
/// and listens via <c>SDL_SYSWMEVENT</c> on the SDL window thread.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed unsafe class X11Touchpad : IDisposable
{
    private const uint SDL_SYSWMEVENT = 0x201;
    private const int SDL_ENABLE = 1;
    private const int GenericEvent = 35;
    private const int XIAllMasterDevices = 1;

    private const int XI_GesturePinchBegin = 27;
    private const int XI_GesturePinchUpdate = 28;
    private const int XI_GesturePinchEnd = 29;
    private const int XI_GestureSwipeBegin = 30;
    private const int XI_GestureSwipeUpdate = 31;
    private const int XI_GestureSwipeEnd = 32;
    private const int XI_LASTEVENT = 32;

    private const int XIGesturePinchEventCancelled = 1 << 0;
    private const int XIGestureSwipeEventCancelled = 1 << 0;

    private static X11Touchpad? active;
    private static readonly string? tracePath = Environment.GetEnvironmentVariable("HORIZON_SCROLL_TRACE") is { Length: > 0 } p ? p : null;

    private readonly LinuxTouchpadState state = new();
    private readonly Action<InputEvent> raise;
    private readonly nint xDisplay;
    private readonly nuint xWindow;
    private readonly int xiOpcode;
    private bool watchInstalled;
    private bool disposed;

    /// <summary>
    /// Attempts to attach XInput 2.4 gesture listening to an X11 SDL window.
    /// Returns <see langword="null"/> when not running on X11 or when XI 2.4 is unavailable.
    /// </summary>
    public static X11Touchpad? TryCreate(nint sdlWindow, Action<InputEvent> raise)
    {
        if (!OperatingSystem.IsLinux() || sdlWindow == 0)
            return null;

        try
        {
            var sdl = SdlProvider.SDL.Value;
            SysWMInfo info = default;
            sdl.GetVersion(&info.Version);
            if (!sdl.GetWindowWMInfo((Silk.NET.SDL.Window*)sdlWindow, &info))
                return null;
            if (info.Subsystem != SysWMType.X11)
                return null;

            nint display = (nint)info.Info.X11.Display;
            nuint window = (nuint)info.Info.X11.Window;
            if (display == 0 || window == 0)
                return null;

            int opcode = 0, firstEvent = 0, firstError = 0;
            if (XQueryExtension(display, "XInputExtension", &opcode, &firstEvent, &firstError) == 0)
                return null;

            int major = 2, minor = 4;
            if (XIQueryVersion(display, &major, &minor) != 0 || major < 2 || (major == 2 && minor < 4))
                return null;

            var pad = new X11Touchpad(display, window, opcode, raise);
            if (!pad.Attach())
            {
                pad.Dispose();
                return null;
            }
            return pad;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        catch (Exception e)
        {
            Logging.Log.Warning($"[Input] X11 touchpad gesture setup skipped: {e.Message}");
            return null;
        }
    }

    private X11Touchpad(nint display, nuint window, int opcode, Action<InputEvent> raise)
    {
        xDisplay = display;
        xWindow = window;
        xiOpcode = opcode;
        this.raise = raise;
    }

    private bool Attach()
    {
        // Preserve SDL's existing XI2 event mask on XIAllMasterDevices while adding XI 2.4 gestures
        int maskLen = (XI_LASTEVENT >> 3) + 1;
        byte* maskBytes = stackalloc byte[maskLen];
        for (int i = 0; i < maskLen; i++)
            maskBytes[i] = 0;

        int existingCount = 0;
        XIEventMask* existing = XIGetSelectedEvents(xDisplay, xWindow, &existingCount);
        if (existing != null)
        {
            for (int i = 0; i < existingCount; i++)
            {
                if (existing[i].DeviceId == XIAllMasterDevices && existing[i].Mask != null)
                {
                    int copyLen = Math.Min(maskLen, existing[i].MaskLen);
                    for (int b = 0; b < copyLen; b++)
                        maskBytes[b] |= existing[i].Mask[b];
                }
            }
            XFree(existing);
        }

        SetBit(maskBytes, XI_GesturePinchBegin);
        SetBit(maskBytes, XI_GesturePinchUpdate);
        SetBit(maskBytes, XI_GesturePinchEnd);
        SetBit(maskBytes, XI_GestureSwipeBegin);
        SetBit(maskBytes, XI_GestureSwipeUpdate);
        SetBit(maskBytes, XI_GestureSwipeEnd);

        var evMask = new XIEventMask
        {
            DeviceId = XIAllMasterDevices,
            MaskLen = maskLen,
            Mask = maskBytes
        };

        if (XISelectEvents(xDisplay, xWindow, &evMask, 1) != 0)
            return false;

        XFlush(xDisplay);

        Volatile.Write(ref active, this);
        var sdl = SdlProvider.SDL.Value;
        sdl.EventState(SDL_SYSWMEVENT, SDL_ENABLE);
        sdl.AddEventWatch(new PfnEventFilter(&Watch), null);
        watchInstalled = true;
        Logging.Log.Info("[Input] X11 XInput 2.4 touchpad gestures attached.");
        return true;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        if (ReferenceEquals(Volatile.Read(ref active), this))
            Volatile.Write(ref active, null);

        if (watchInstalled)
        {
            watchInstalled = false;
            try { SdlProvider.SDL.Value.DelEventWatch(new PfnEventFilter(&Watch), null); }
            catch (Exception) { }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Watch(void* userdata, Event* e)
    {
        try
        {
            if (e == null || e->Type != SDL_SYSWMEVENT)
                return 1;
            if (Volatile.Read(ref active) is not { disposed: false } self)
                return 1;

            SysWMMsg* msg = e->Syswm.Msg;
            if (msg == null || msg->Subsystem != SysWMType.X11)
                return 1;

            // SDL_SysWMmsg.msg.x11.event is an XEvent union; check XGenericEventCookie
            var cookie = (XGenericEventCookie*)&msg->Msg.X11;
            if (cookie->Type != GenericEvent || cookie->Extension != self.xiOpcode)
                return 1;

            int evtype = cookie->EvType;
            if (evtype is < XI_GesturePinchBegin or > XI_GestureSwipeEnd)
                return 1;

            // SDL may or may not have fetched cookie data yet; if Data is null, call XGetEventData safely
            bool fetched = false;
            if (cookie->Data == null)
            {
                if (XGetEventData(self.xDisplay, cookie) == 0 || cookie->Data == null)
                    return 1;
                fetched = true;
            }

            try
            {
                self.HandleXiGesture(evtype, cookie->Data);
            }
            finally
            {
                if (fetched)
                    XFreeEventData(self.xDisplay, cookie);
            }
        }
        catch (Exception)
        {
            // Never let managed exceptions unwind into SDL's C event loop
        }

        return 1;
    }

    private void HandleXiGesture(int evtype, void* data)
    {
        switch (evtype)
        {
            case XI_GesturePinchBegin:
            {
                var p = (XIGesturePinchEvent*)data;
                Emit(state.OnPinchBegin((uint)Math.Max(0, p->Detail), p->Scale), "xi-pinch-begin");
                break;
            }
            case XI_GesturePinchUpdate:
            {
                var p = (XIGesturePinchEvent*)data;
                Emit(state.OnPinchUpdate(p->Scale), "xi-pinch-update");
                break;
            }
            case XI_GesturePinchEnd:
            {
                var p = (XIGesturePinchEvent*)data;
                state.OnPinchEnd();
                if (tracePath is not null)
                    Trace($"xi-pinch-end cancelled={(p->Flags & XIGesturePinchEventCancelled) != 0}");
                break;
            }
            case XI_GestureSwipeBegin:
            {
                var s = (XIGestureSwipeEvent*)data;
                Emit(state.OnSwipeBegin((uint)Math.Max(0, s->Detail)), "xi-swipe-begin");
                break;
            }
            case XI_GestureSwipeUpdate:
            {
                var s = (XIGestureSwipeEvent*)data;
                state.OnSwipeUpdate(s->DeltaX, s->DeltaY);
                break;
            }
            case XI_GestureSwipeEnd:
            {
                var s = (XIGestureSwipeEvent*)data;
                Emit(state.OnSwipeEnd((s->Flags & XIGestureSwipeEventCancelled) != 0), "xi-swipe-end");
                break;
            }
        }
    }

    private void Emit(InputEvent? ev, string tag)
    {
        if (ev is not { } e)
            return;

        if (tracePath is not null)
            Trace($"{tag} kind={e.Kind} mag={e.Magnification:F4} swipe={e.SwipeX}");

        raise(e);
    }

    private static void Trace(string message)
    {
        try
        {
            File.AppendAllText(tracePath!, FormattableString.Invariant($"{Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency:F3} x11 {message}\n"));
        }
        catch (IOException)
        {
        }
    }

    private static void SetBit(byte* mask, int bit) => mask[bit >> 3] |= (byte)(1 << (bit & 7));

    /* ---- X11 / XInput2 ABI structs & P/Invokes ---- */

    [StructLayout(LayoutKind.Sequential)]
    private struct XIEventMask
    {
        public int DeviceId;
        public int MaskLen;
        public byte* Mask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XGenericEventCookie
    {
        public int Type;
        public nuint Serial;
        public int SendEvent;
        public nint Display;
        public int Extension;
        public int EvType;
        public uint Cookie;
        public void* Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XIGesturePinchEvent
    {
        public int Type;
        public nuint Serial;
        public int SendEvent;
        public nint Display;
        public int Extension;
        public int EvType;
        public nuint Time;
        public int DeviceId;
        public int SourceId;
        public int Detail;
        public nuint Root;
        public nuint Event;
        public nuint Child;
        public double RootX, RootY;
        public double EventX, EventY;
        public double DeltaX, DeltaY;
        public double DeltaUnaccelX, DeltaUnaccelY;
        public double Scale;
        public double DeltaAngle;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XIGestureSwipeEvent
    {
        public int Type;
        public nuint Serial;
        public int SendEvent;
        public nint Display;
        public int Extension;
        public int EvType;
        public nuint Time;
        public int DeviceId;
        public int SourceId;
        public int Detail;
        public nuint Root;
        public nuint Event;
        public nuint Child;
        public double RootX, RootY;
        public double EventX, EventY;
        public double DeltaX, DeltaY;
        public double DeltaUnaccelX, DeltaUnaccelY;
        public int Flags;
    }

    [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
    private static extern int XQueryExtension(nint display, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int* majorOpcode, int* firstEvent, int* firstError);

    [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
    private static extern int XGetEventData(nint display, XGenericEventCookie* cookie);

    [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
    private static extern void XFreeEventData(nint display, XGenericEventCookie* cookie);

    [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
    private static extern int XFree(void* data);

    [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
    private static extern int XFlush(nint display);

    [DllImport("libXi.so.6", CallingConvention = CallingConvention.Cdecl)]
    private static extern int XIQueryVersion(nint display, int* major, int* minor);

    [DllImport("libXi.so.6", CallingConvention = CallingConvention.Cdecl)]
    private static extern int XISelectEvents(nint display, nuint window, XIEventMask* masks, int numMasks);

    [DllImport("libXi.so.6", CallingConvention = CallingConvention.Cdecl)]
    private static extern XIEventMask* XIGetSelectedEvents(nint display, nuint window, int* numMasksReturn);
}
