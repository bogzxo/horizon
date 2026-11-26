using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

using Horizon.Input;
using Silk.NET.SDL;

namespace Horizon.Platform.Desktop;

/// <summary>
/// Native Wayland touchpad monitor on Linux (<c>libwayland-client.so.0</c>).
/// Binds a secondary <c>wl_pointer</c> and <c>zwp_pointer_gestures_v1</c> (pinch, swipe, hold) on SDL's
/// existing <c>wl_display</c>, dispatched synchronously on the SDL window thread with zero extra threads
/// and NativeAOT-safe <see cref="UnmanagedCallersOnlyAttribute"/> callbacks.
/// <para>
/// This delivers exact 1:1 surface-pixel two-finger vertical and horizontal scrolling, distinguishes
/// touchpad (<c>WL_POINTER_AXIS_SOURCE_FINGER</c> / <c>CONTINUOUS</c>) from notched mouse wheels,
/// emits <see cref="ScrollPhase.Touching"/> and <see cref="ScrollPhase.Released"/> (from <c>axis_stop</c>
/// and <c>hold</c>) for rubber-band overscroll and Safari-grade momentum glide, and forwards native
/// two-finger pinch-to-zoom and three-finger swipe gestures.
/// </para>
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed unsafe class WaylandTouchpad : IDisposable
{
    private const uint WL_MARSHAL_FLAG_DESTROY = 1u << 0;
    private const uint WL_SEAT_CAPABILITY_POINTER = 1u << 0;
    private const uint MAX_SEAT_VERSION = 8;
    private const uint MAX_GESTURES_VERSION = 3;

    // Singleton active instance (one desktop window per engine) so [UnmanagedCallersOnly] callbacks reach it cleanly
    private static WaylandTouchpad? active;
    private static int suppressSdlWheel;
    private static readonly string? tracePath = Environment.GetEnvironmentVariable("HORIZON_SCROLL_TRACE") is { Length: > 0 } p ? p : null;

    private readonly LinuxTouchpadState state = new();
    private readonly Action<InputEvent> raise;
    private readonly nint wlDisplay;
    private readonly nint wlSurface;

    private nint wlRegistry;
    private nint wlSeat;
    private nint wlPointer;
    private nint wlGestures;
    private uint wlGesturesVersion;
    private nint wlPinch;
    private nint wlSwipe;
    private nint wlHold;

    private bool pointerOnOurSurface;
    private bool disposed;

    // Unmanaged protocol descriptors and listener vtables allocated for the lifetime of this instance
    private ProtocolTables* tables;

    /// <summary>True when a live Wayland touchpad monitor is attached to the active window.</summary>
    public static bool Active => Volatile.Read(ref active) is not null;

    /// <summary>
    /// True when the latest Wayland <c>wl_pointer.axis_source</c> was a touchpad (<c>FINGER</c> or <c>CONTINUOUS</c>)
    /// so <see cref="SilkInputSource"/> drops SDL's duplicate <c>SDL_MOUSEWHEEL</c> while letting physical wheel ticks through.
    /// </summary>
    public static bool SuppressesSdlWheel => Active && Volatile.Read(ref suppressSdlWheel) != 0;

    /// <summary>
    /// Attempts to attach to SDL's Wayland display and surface. Returns <see langword="null"/> if the window
    /// is not using Wayland or <c>libwayland-client.so.0</c> is unavailable.
    /// </summary>
    public static WaylandTouchpad? TryCreate(nint sdlWindow, Action<InputEvent> raise)
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
            if (info.Subsystem != SysWMType.Wayland)
                return null;

            nint display = (nint)info.Info.Wayland.Display;
            nint surface = (nint)info.Info.Wayland.Surface;
            if (display == 0 || surface == 0)
                return null;

            var pad = new WaylandTouchpad(display, surface, raise);
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
            Logging.Log.Warning($"[Input] Wayland touchpad setup skipped: {e.Message}");
            return null;
        }
    }

    private WaylandTouchpad(nint display, nint surface, Action<InputEvent> raise)
    {
        wlDisplay = display;
        wlSurface = surface;
        this.raise = raise;
    }

    private bool Attach()
    {
        if (!NativeLibrary.TryLoad("libwayland-client.so.0", out nint lib))
            return false;

        if (!NativeLibrary.TryGetExport(lib, "wl_registry_interface", out nint regIface) ||
            !NativeLibrary.TryGetExport(lib, "wl_seat_interface", out nint seatIface) ||
            !NativeLibrary.TryGetExport(lib, "wl_pointer_interface", out nint ptrIface) ||
            !NativeLibrary.TryGetExport(lib, "wl_surface_interface", out nint surfIface))
            return false;

        tables = ProtocolTables.Create(regIface, seatIface, ptrIface, surfIface);
        Volatile.Write(ref active, this);
        Volatile.Write(ref suppressSdlWheel, 0);

        // wl_display.get_registry (opcode 1)
        WlArgument arg = default;
        wlRegistry = wl_proxy_marshal_array_flags(wlDisplay, 1, regIface, 1, 0, &arg);
        if (wlRegistry == 0)
            return false;

        wl_proxy_add_listener(wlRegistry, &tables->RegistryListener, null);
        wl_display_roundtrip(wlDisplay);

        // Bind seat capabilities & create pointer + gesture objects
        if (wlSeat != 0)
            wl_display_roundtrip(wlDisplay);

        if (wlPointer == 0)
            return false;

        AttachGestures();
        wl_display_flush(wlDisplay);
        Logging.Log.Info($"[Input] Wayland touchpad attached (gestures v{wlGesturesVersion}).");
        return true;
    }

    private void AttachGestures()
    {
        if (wlGestures == 0 || wlPointer == 0 || tables == null)
            return;

        // zwp_pointer_gestures_v1.get_swipe_gesture (opcode 0)
        if (wlSwipe == 0)
        {
            WlArgument* args = stackalloc WlArgument[2];
            args[0] = default;
            args[1].Ptr = wlPointer;
            wlSwipe = wl_proxy_marshal_array_flags(wlGestures, 0, (nint)(&tables->SwipeIface), Math.Min(wlGesturesVersion, 2u), 0, args);
            if (wlSwipe != 0)
                wl_proxy_add_listener(wlSwipe, &tables->SwipeListener, null);
        }

        // zwp_pointer_gestures_v1.get_pinch_gesture (opcode 1)
        if (wlPinch == 0)
        {
            WlArgument* args = stackalloc WlArgument[2];
            args[0] = default;
            args[1].Ptr = wlPointer;
            wlPinch = wl_proxy_marshal_array_flags(wlGestures, 1, (nint)(&tables->PinchIface), Math.Min(wlGesturesVersion, 2u), 0, args);
            if (wlPinch != 0)
                wl_proxy_add_listener(wlPinch, &tables->PinchListener, null);
        }

        // zwp_pointer_gestures_v1.get_hold_gesture (opcode 3, since v3)
        if (wlHold == 0 && wlGesturesVersion >= 3)
        {
            WlArgument* args = stackalloc WlArgument[2];
            args[0] = default;
            args[1].Ptr = wlPointer;
            wlHold = wl_proxy_marshal_array_flags(wlGestures, 3, (nint)(&tables->HoldIface), 3, 0, args);
            if (wlHold != 0)
                wl_proxy_add_listener(wlHold, &tables->HoldListener, null);
        }
    }

    private void DestroyGestureObjects()
    {
        if (wlHold != 0)
        {
            wl_proxy_marshal_array_flags(wlHold, 0, 0, wl_proxy_get_version(wlHold), WL_MARSHAL_FLAG_DESTROY, null);
            wlHold = 0;
        }
        if (wlPinch != 0)
        {
            wl_proxy_marshal_array_flags(wlPinch, 0, 0, wl_proxy_get_version(wlPinch), WL_MARSHAL_FLAG_DESTROY, null);
            wlPinch = 0;
        }
        if (wlSwipe != 0)
        {
            wl_proxy_marshal_array_flags(wlSwipe, 0, 0, wl_proxy_get_version(wlSwipe), WL_MARSHAL_FLAG_DESTROY, null);
            wlSwipe = 0;
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        if (ReferenceEquals(Volatile.Read(ref active), this))
            Volatile.Write(ref active, null);
        Volatile.Write(ref suppressSdlWheel, 0);

        try
        {
            DestroyGestureObjects();
            if (wlPointer != 0)
            {
                if (wl_proxy_get_version(wlPointer) >= 3)
                    wl_proxy_marshal_array_flags(wlPointer, 1, 0, wl_proxy_get_version(wlPointer), WL_MARSHAL_FLAG_DESTROY, null);
                else
                    wl_proxy_destroy(wlPointer);
                wlPointer = 0;
            }
            if (wlGestures != 0)
            {
                if (wlGesturesVersion >= 2)
                    wl_proxy_marshal_array_flags(wlGestures, 2, 0, wlGesturesVersion, WL_MARSHAL_FLAG_DESTROY, null);
                else
                    wl_proxy_destroy(wlGestures);
                wlGestures = 0;
            }
            if (wlSeat != 0)
            {
                if (wl_proxy_get_version(wlSeat) >= 5)
                    wl_proxy_marshal_array_flags(wlSeat, 3, 0, wl_proxy_get_version(wlSeat), WL_MARSHAL_FLAG_DESTROY, null);
                else
                    wl_proxy_destroy(wlSeat);
                wlSeat = 0;
            }
            if (wlRegistry != 0)
            {
                wl_proxy_destroy(wlRegistry);
                wlRegistry = 0;
            }
            if (wlDisplay != 0)
                wl_display_flush(wlDisplay);
        }
        catch (Exception)
        {
            // Display may already be torn down on window exit
        }

        if (tables != null)
        {
            ProtocolTables.Free(tables);
            tables = null;
        }
    }

    private void Emit(InputEvent? ev, string tag)
    {
        if (ev is not { } e)
            return;

        if (tracePath is not null)
            Trace($"{tag} kind={e.Kind} scroll={e.ScrollX:F4},{e.ScrollY:F4} precise={e.ScrollPrecise} phase={e.ScrollPhase} mag={e.Magnification:F4} swipe={e.SwipeX}");

        raise(e);
    }

    private static void Trace(string message)
    {
        try
        {
            File.AppendAllText(tracePath!, FormattableString.Invariant($"{Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency:F3} wl {message}\n"));
        }
        catch (IOException)
        {
        }
    }

    private static double FixedToDouble(int fixed24_8) => fixed24_8 / 256.0;

    /* ---- wl_registry callbacks ---- */

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRegistryGlobal(void* data, nint registry, uint name, byte* ifacePtr, uint version)
    {
        try
        {
            if (Volatile.Read(ref active) is not { disposed: false } self || self.tables == null || ifacePtr == null)
                return;

            string? iface = Marshal.PtrToStringUTF8((nint)ifacePtr);
            if (iface == "wl_seat" && self.wlSeat == 0)
            {
                uint bindVersion = Math.Min(version, MAX_SEAT_VERSION);
                WlArgument* args = stackalloc WlArgument[4];
                args[0].UInt = name;
                args[1].Ptr = self.tables->StrSeat;
                args[2].UInt = bindVersion;
                args[3] = default;
                self.wlSeat = wl_proxy_marshal_array_flags(registry, 0, self.tables->SeatIface, bindVersion, 0, args);
                if (self.wlSeat != 0)
                    wl_proxy_add_listener(self.wlSeat, &self.tables->SeatListener, null);
            }
            else if (iface == "zwp_pointer_gestures_v1" && self.wlGestures == 0)
            {
                uint bindVersion = Math.Min(version, MAX_GESTURES_VERSION);
                WlArgument* args = stackalloc WlArgument[4];
                args[0].UInt = name;
                args[1].Ptr = self.tables->StrGestures;
                args[2].UInt = bindVersion;
                args[3] = default;
                self.wlGestures = wl_proxy_marshal_array_flags(registry, 0, (nint)(&self.tables->GesturesIface), bindVersion, 0, args);
                if (self.wlGestures != 0)
                    self.wlGesturesVersion = bindVersion;
            }
        }
        catch (Exception) { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRegistryGlobalRemove(void* data, nint registry, uint name) { }

    /* ---- wl_seat callbacks ---- */

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSeatCapabilities(void* data, nint seat, uint capabilities)
    {
        try
        {
            if (Volatile.Read(ref active) is not { disposed: false } self || self.tables == null)
                return;

            bool hasPointer = (capabilities & WL_SEAT_CAPABILITY_POINTER) != 0;
            if (hasPointer && self.wlPointer == 0)
            {
                uint seatVersion = wl_proxy_get_version(seat);
                WlArgument arg = default;
                self.wlPointer = wl_proxy_marshal_array_flags(seat, 0, self.tables->PointerIface, seatVersion, 0, &arg);
                if (self.wlPointer != 0)
                {
                    wl_proxy_add_listener(self.wlPointer, &self.tables->PointerListener, null);
                    self.AttachGestures();
                }
            }
            else if (!hasPointer && self.wlPointer != 0)
            {
                self.pointerOnOurSurface = false;
                self.Emit(self.state.OnPointerLeave(), "seat-lost-pointer");
                self.DestroyGestureObjects();
                if (wl_proxy_get_version(self.wlPointer) >= 3)
                    wl_proxy_marshal_array_flags(self.wlPointer, 1, 0, wl_proxy_get_version(self.wlPointer), WL_MARSHAL_FLAG_DESTROY, null);
                else
                    wl_proxy_destroy(self.wlPointer);
                self.wlPointer = 0;
            }
        }
        catch (Exception) { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSeatName(void* data, nint seat, byte* name) { }

    /* ---- wl_pointer callbacks ---- */

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPointerEnter(void* data, nint pointer, uint serial, nint surface, int sx, int sy)
    {
        try
        {
            if (Volatile.Read(ref active) is { disposed: false } self)
                self.pointerOnOurSurface = surface == self.wlSurface;
        }
        catch (Exception) { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPointerLeave(void* data, nint pointer, uint serial, nint surface)
    {
        try
        {
            if (Volatile.Read(ref active) is not { disposed: false } self)
                return;

            if (surface == self.wlSurface)
            {
                self.pointerOnOurSurface = false;
                Volatile.Write(ref suppressSdlWheel, 0);
                self.Emit(self.state.OnPointerLeave(), "leave");
            }
        }
        catch (Exception) { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPointerMotion(void* data, nint pointer, uint time, int sx, int sy) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPointerButton(void* data, nint pointer, uint serial, uint time, uint button, uint state) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPointerAxis(void* data, nint pointer, uint time, uint axis, int value)
    {
        try
        {
            if (Volatile.Read(ref active) is { disposed: false } self)
                self.state.OnAxis(axis, FixedToDouble(value));
        }
        catch (Exception) { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPointerFrame(void* data, nint pointer)
    {
        try
        {
            if (Volatile.Read(ref active) is not { disposed: false } self)
                return;

            var (ev, suppress) = self.state.OnFrame(self.pointerOnOurSurface);
            if (suppress)
                Volatile.Write(ref suppressSdlWheel, 1);
            self.Emit(ev, "axis-frame");
        }
        catch (Exception) { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPointerAxisSource(void* data, nint pointer, uint source)
    {
        try
        {
            if (Volatile.Read(ref active) is { disposed: false } self)
            {
                Volatile.Write(ref suppressSdlWheel, source is LinuxTouchpadState.SourceFinger or LinuxTouchpadState.SourceContinuous ? 1 : 0);
                self.state.OnAxisSource(source);
            }
        }
        catch (Exception) { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPointerAxisStop(void* data, nint pointer, uint time, uint axis)
    {
        try
        {
            if (Volatile.Read(ref active) is { disposed: false } self)
                self.state.OnAxisStop();
        }
        catch (Exception) { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPointerAxisDiscrete(void* data, nint pointer, uint axis, int discrete) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPointerAxisValue120(void* data, nint pointer, uint axis, int value120) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPointerAxisRelativeDirection(void* data, nint pointer, uint axis, uint direction) { }

    /* ---- zwp_pointer_gesture_pinch_v1 callbacks ---- */

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPinchBegin(void* data, nint pinch, uint serial, uint time, nint surface, uint fingers)
    {
        try
        {
            if (Volatile.Read(ref active) is not { disposed: false } self)
                return;
            if (surface != self.wlSurface)
                return;
            self.Emit(self.state.OnPinchBegin(fingers), "pinch-begin");
        }
        catch (Exception) { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPinchUpdate(void* data, nint pinch, uint time, int dx, int dy, int scale, int rotation)
    {
        try
        {
            if (Volatile.Read(ref active) is not { disposed: false } self)
                return;
            self.Emit(self.state.OnPinchUpdate(FixedToDouble(scale)), "pinch-update");
        }
        catch (Exception) { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPinchEnd(void* data, nint pinch, uint serial, uint time, int cancelled)
    {
        try
        {
            if (Volatile.Read(ref active) is not { disposed: false } self)
                return;
            self.state.OnPinchEnd();
            if (tracePath is not null)
                Trace($"pinch-end cancelled={cancelled}");
        }
        catch (Exception) { }
    }

    /* ---- zwp_pointer_gesture_swipe_v1 callbacks ---- */

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSwipeBegin(void* data, nint swipe, uint serial, uint time, nint surface, uint fingers)
    {
        try
        {
            if (Volatile.Read(ref active) is not { disposed: false } self)
                return;
            if (surface != self.wlSurface)
                return;
            self.Emit(self.state.OnSwipeBegin(fingers), "swipe-begin");
        }
        catch (Exception) { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSwipeUpdate(void* data, nint swipe, uint time, int dx, int dy)
    {
        try
        {
            if (Volatile.Read(ref active) is not { disposed: false } self)
                return;
            self.state.OnSwipeUpdate(FixedToDouble(dx), FixedToDouble(dy));
        }
        catch (Exception) { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSwipeEnd(void* data, nint swipe, uint serial, uint time, int cancelled)
    {
        try
        {
            if (Volatile.Read(ref active) is not { disposed: false } self)
                return;
            self.Emit(self.state.OnSwipeEnd(cancelled != 0), "swipe-end");
        }
        catch (Exception) { }
    }

    /* ---- zwp_pointer_gesture_hold_v1 callbacks ---- */

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnHoldBegin(void* data, nint hold, uint serial, uint time, nint surface, uint fingers)
    {
        try
        {
            if (Volatile.Read(ref active) is not { disposed: false } self)
                return;
            if (surface != self.wlSurface)
                return;
            self.Emit(self.state.OnHoldBegin(fingers), "hold-begin");
        }
        catch (Exception) { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnHoldEnd(void* data, nint hold, uint serial, uint time, int cancelled)
    {
        try
        {
            if (Volatile.Read(ref active) is not { disposed: false } self)
                return;
            self.Emit(self.state.OnHoldEnd(cancelled != 0), "hold-end");
        }
        catch (Exception) { }
    }

    /* ---- Wayland C ABI structs & P/Invokes ---- */

    [StructLayout(LayoutKind.Explicit, Size = 8)]
    private struct WlArgument
    {
        [FieldOffset(0)] public int Int;
        [FieldOffset(0)] public uint UInt;
        [FieldOffset(0)] public nint Ptr;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlMessage
    {
        public nint Name;
        public nint Signature;
        public nint* Types;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlInterface
    {
        public nint Name;
        public int Version;
        public int MethodCount;
        public WlMessage* Methods;
        public int EventCount;
        public WlMessage* Events;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlRegistryListener
    {
        public delegate* unmanaged[Cdecl]<void*, nint, uint, byte*, uint, void> Global;
        public delegate* unmanaged[Cdecl]<void*, nint, uint, void> GlobalRemove;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlSeatListener
    {
        public delegate* unmanaged[Cdecl]<void*, nint, uint, void> Capabilities;
        public delegate* unmanaged[Cdecl]<void*, nint, byte*, void> Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlPointerListener
    {
        public delegate* unmanaged[Cdecl]<void*, nint, uint, nint, int, int, void> Enter;
        public delegate* unmanaged[Cdecl]<void*, nint, uint, nint, void> Leave;
        public delegate* unmanaged[Cdecl]<void*, nint, uint, int, int, void> Motion;
        public delegate* unmanaged[Cdecl]<void*, nint, uint, uint, uint, uint, void> Button;
        public delegate* unmanaged[Cdecl]<void*, nint, uint, uint, int, void> Axis;
        public delegate* unmanaged[Cdecl]<void*, nint, void> Frame;
        public delegate* unmanaged[Cdecl]<void*, nint, uint, void> AxisSource;
        public delegate* unmanaged[Cdecl]<void*, nint, uint, uint, void> AxisStop;
        public delegate* unmanaged[Cdecl]<void*, nint, uint, int, void> AxisDiscrete;
        public delegate* unmanaged[Cdecl]<void*, nint, uint, int, void> AxisValue120;
        public delegate* unmanaged[Cdecl]<void*, nint, uint, uint, void> AxisRelativeDirection;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlSwipeListener
    {
        public delegate* unmanaged[Cdecl]<void*, nint, uint, uint, nint, uint, void> Begin;
        public delegate* unmanaged[Cdecl]<void*, nint, uint, int, int, void> Update;
        public delegate* unmanaged[Cdecl]<void*, nint, uint, uint, int, void> End;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlPinchListener
    {
        public delegate* unmanaged[Cdecl]<void*, nint, uint, uint, nint, uint, void> Begin;
        public delegate* unmanaged[Cdecl]<void*, nint, uint, int, int, int, int, void> Update;
        public delegate* unmanaged[Cdecl]<void*, nint, uint, uint, int, void> End;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlHoldListener
    {
        public delegate* unmanaged[Cdecl]<void*, nint, uint, uint, nint, uint, void> Begin;
        public delegate* unmanaged[Cdecl]<void*, nint, uint, uint, int, void> End;
    }

    /// <summary>
    /// Unmanaged block holding all <c>wl_interface</c>, <c>wl_message</c>, type tables, UTF-8 protocol strings,
    /// and listener vtables for <c>pointer-gestures-unstable-v1</c>. Freed in one piece on <see cref="Dispose"/>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ProtocolTables
    {
        public nint RegistryIface, SeatIface, PointerIface, SurfaceIface;
        public nint StrSeat, StrGestures;

        public WlInterface GesturesIface;
        public WlInterface SwipeIface;
        public WlInterface PinchIface;
        public WlInterface HoldIface;

        public WlRegistryListener RegistryListener;
        public WlSeatListener SeatListener;
        public WlPointerListener PointerListener;
        public WlSwipeListener SwipeListener;
        public WlPinchListener PinchListener;
        public WlHoldListener HoldListener;

        // Backing arrays allocated inside a single pool for deterministic cleanup
        private nint stringsBlock;
        private WlMessage* messages;
        private nint* typeSlots;

        public static ProtocolTables* Create(nint regIface, nint seatIface, nint ptrIface, nint surfIface)
        {
            var t = (ProtocolTables*)NativeMemory.AllocZeroed(1, (nuint)sizeof(ProtocolTables));
            t->RegistryIface = regIface;
            t->SeatIface = seatIface;
            t->PointerIface = ptrIface;
            t->SurfaceIface = surfIface;

            // Pack all null-terminated UTF-8 protocol strings into one contiguous unmanaged buffer
            string[] strs =
            [
                "wl_seat",
                "zwp_pointer_gestures_v1",
                "zwp_pointer_gesture_swipe_v1",
                "zwp_pointer_gesture_pinch_v1",
                "zwp_pointer_gesture_hold_v1",
                "get_swipe_gesture",
                "get_pinch_gesture",
                "release",
                "get_hold_gesture",
                "destroy",
                "begin",
                "update",
                "end",
                "no",
                "2",
                "3no",
                "",
                "3",
                "uuou",
                "uff",
                "uui",
                "uffff",
                "3uuou",
                "3uui"
            ];

            int totalBytes = 0;
            foreach (string s in strs)
                totalBytes += Encoding.UTF8.GetByteCount(s) + 1;

            byte* buf = (byte*)NativeMemory.AllocZeroed((nuint)totalBytes);
            t->stringsBlock = (nint)buf;
            nint* sp = stackalloc nint[strs.Length];
            int pos = 0;
            for (int i = 0; i < strs.Length; i++)
            {
                sp[i] = (nint)(buf + pos);
                pos += Encoding.UTF8.GetBytes(strs[i], new Span<byte>(buf + pos, totalBytes - pos));
                buf[pos++] = 0;
            }

            t->StrSeat = sp[0];
            t->StrGestures = sp[1];

            // Type pointer arrays (18 slots total)
            nint* types = (nint*)NativeMemory.AllocZeroed(18, (nuint)sizeof(nint));
            t->typeSlots = types;
            // [0..1]: get_swipe_gesture -> [swipeIface, pointerIface]
            types[0] = (nint)(&t->SwipeIface);
            types[1] = ptrIface;
            // [2..3]: get_pinch_gesture -> [pinchIface, pointerIface]
            types[2] = (nint)(&t->PinchIface);
            types[3] = ptrIface;
            // [4..5]: get_hold_gesture -> [holdIface, pointerIface]
            types[4] = (nint)(&t->HoldIface);
            types[5] = ptrIface;
            // [6..9]: begin (uuou) -> [null, null, surfaceIface, null]
            types[6] = 0;
            types[7] = 0;
            types[8] = surfIface;
            types[9] = 0;
            // [10..14]: 5 nulls (shared for uff, uui, uffff, release, destroy)
            // already zeroed!

            // Messages (15 total: 4 gestures requests + 1 swipe req + 3 swipe ev + 1 pinch req + 3 pinch ev + 1 hold req + 2 hold ev)
            WlMessage* msgs = (WlMessage*)NativeMemory.AllocZeroed(15, (nuint)sizeof(WlMessage));
            t->messages = msgs;

            // zwp_pointer_gestures_v1 requests [0..3]
            msgs[0] = new WlMessage { Name = sp[5], Signature = sp[13], Types = types + 0 };  // get_swipe_gesture, "no"
            msgs[1] = new WlMessage { Name = sp[6], Signature = sp[13], Types = types + 2 };  // get_pinch_gesture, "no"
            msgs[2] = new WlMessage { Name = sp[7], Signature = sp[14], Types = types + 10 }; // release, "2"
            msgs[3] = new WlMessage { Name = sp[8], Signature = sp[15], Types = types + 4 };  // get_hold_gesture, "3no"

            // zwp_pointer_gesture_swipe_v1 request [4] & events [5..7]
            msgs[4] = new WlMessage { Name = sp[9], Signature = sp[16], Types = types + 10 };  // destroy, ""
            msgs[5] = new WlMessage { Name = sp[10], Signature = sp[18], Types = types + 6 };  // begin, "uuou"
            msgs[6] = new WlMessage { Name = sp[11], Signature = sp[19], Types = types + 10 }; // update, "uff"
            msgs[7] = new WlMessage { Name = sp[12], Signature = sp[20], Types = types + 10 }; // end, "uui"

            // zwp_pointer_gesture_pinch_v1 request [8] & events [9..11]
            msgs[8] = new WlMessage { Name = sp[9], Signature = sp[16], Types = types + 10 };   // destroy, ""
            msgs[9] = new WlMessage { Name = sp[10], Signature = sp[18], Types = types + 6 };   // begin, "uuou"
            msgs[10] = new WlMessage { Name = sp[11], Signature = sp[21], Types = types + 10 }; // update, "uffff"
            msgs[11] = new WlMessage { Name = sp[12], Signature = sp[20], Types = types + 10 }; // end, "uui"

            // zwp_pointer_gesture_hold_v1 request [12] & events [13..14]
            msgs[12] = new WlMessage { Name = sp[9], Signature = sp[17], Types = types + 10 };  // destroy, "3"
            msgs[13] = new WlMessage { Name = sp[10], Signature = sp[22], Types = types + 6 };  // begin, "3uuou"
            msgs[14] = new WlMessage { Name = sp[12], Signature = sp[23], Types = types + 10 }; // end, "3uui"

            t->GesturesIface = new WlInterface { Name = sp[1], Version = 3, MethodCount = 4, Methods = msgs + 0, EventCount = 0, Events = null };
            t->SwipeIface = new WlInterface { Name = sp[2], Version = 2, MethodCount = 1, Methods = msgs + 4, EventCount = 3, Events = msgs + 5 };
            t->PinchIface = new WlInterface { Name = sp[3], Version = 2, MethodCount = 1, Methods = msgs + 8, EventCount = 3, Events = msgs + 9 };
            t->HoldIface = new WlInterface { Name = sp[4], Version = 3, MethodCount = 1, Methods = msgs + 12, EventCount = 2, Events = msgs + 13 };

            t->RegistryListener = new WlRegistryListener { Global = &OnRegistryGlobal, GlobalRemove = &OnRegistryGlobalRemove };
            t->SeatListener = new WlSeatListener { Capabilities = &OnSeatCapabilities, Name = &OnSeatName };
            t->PointerListener = new WlPointerListener
            {
                Enter = &OnPointerEnter,
                Leave = &OnPointerLeave,
                Motion = &OnPointerMotion,
                Button = &OnPointerButton,
                Axis = &OnPointerAxis,
                Frame = &OnPointerFrame,
                AxisSource = &OnPointerAxisSource,
                AxisStop = &OnPointerAxisStop,
                AxisDiscrete = &OnPointerAxisDiscrete,
                AxisValue120 = &OnPointerAxisValue120,
                AxisRelativeDirection = &OnPointerAxisRelativeDirection
            };
            t->SwipeListener = new WlSwipeListener { Begin = &OnSwipeBegin, Update = &OnSwipeUpdate, End = &OnSwipeEnd };
            t->PinchListener = new WlPinchListener { Begin = &OnPinchBegin, Update = &OnPinchUpdate, End = &OnPinchEnd };
            t->HoldListener = new WlHoldListener { Begin = &OnHoldBegin, End = &OnHoldEnd };

            return t;
        }

        public static void Free(ProtocolTables* t)
        {
            if (t == null)
                return;
            if (t->stringsBlock != 0)
                NativeMemory.Free((void*)t->stringsBlock);
            if (t->messages != null)
                NativeMemory.Free(t->messages);
            if (t->typeSlots != null)
                NativeMemory.Free(t->typeSlots);
            NativeMemory.Free(t);
        }
    }

    [DllImport("libwayland-client.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern int wl_display_roundtrip(nint display);

    [DllImport("libwayland-client.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern int wl_display_flush(nint display);

    [DllImport("libwayland-client.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint wl_proxy_marshal_array_flags(nint proxy, uint opcode, nint iface, uint version, uint flags, WlArgument* args);

    [DllImport("libwayland-client.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern int wl_proxy_add_listener(nint proxy, void* implementation, void* data);

    [DllImport("libwayland-client.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint wl_proxy_get_version(nint proxy);

    [DllImport("libwayland-client.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void wl_proxy_destroy(nint proxy);
}
