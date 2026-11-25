using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Silk.NET.SDL;

namespace Horizon.Platform.Desktop;

/// <summary>
/// What a wheel turn really was, which Silk doesn't hand on. Silk.NET.Input.Sdl (2.23) makes its <c>ScrollWheel</c> out of
/// <c>SDL_MOUSEWHEEL</c>'s whole <c>x</c>/<c>y</c>, which SDL fills by adding the turns up and handing out only the whole
/// notches of the sum. A trackpad's turn is a small fraction, so it came through as a run of zeros and then a jump of one,
/// nothing for a small move of the finger and a lurch for a bigger one. The turn is read where it is made instead: SDL calls
/// the event watch as it pushes the event, on the main thread, in the order the events go to the program. The watch keeps
/// <c>preciseX</c>/<c>preciseY</c>, and on a Mac the <c>NSEvent</c> itself (<c>[NSApp currentEvent]</c> while SDL pumps
/// AppKit's events) for <c>hasPreciseScrollingDeltas</c> and <c>scrollingDeltaX/Y</c>, the points the content is to move.
/// <see cref="SilkInputSource"/> takes the one that matches each wheel event Silk then says.
/// </summary>
internal static unsafe class MacScrollWatch
{
    /// <summary>NSEventTypeScrollWheel.</summary>
    private const nint SCROLL_WHEEL = 22;

    private const uint SDL_MOUSEWHEEL = 0x403;

    private static readonly ScrollTurnQueue turns = new();
    private static int installed;

    /// <summary>Where every turn is written when HORIZON_SCROLL_TRACE names a file, to see what the system said.</summary>
    private static readonly string? tracePath = Environment.GetEnvironmentVariable("HORIZON_SCROLL_TRACE") is { Length: > 0 } path ? path : null;

    /// <summary>Whether wheel turns are being read, only after <see cref="Install"/> worked.</summary>
    public static bool Active { get; private set; }

    /// <summary>Starts watching SDL's events for the wheel, once. Any platform SDL runs the window on.</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref installed, 1) != 0)
            return;

        try
        {
            SdlProvider.SDL.Value.AddEventWatch(new PfnEventFilter(&Watch), null);
            Active = true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // no SDL to ask, the whole notches are all there is
        }
    }

    /// <summary>
    /// The turn the wheel event Silk says was (its X and Y being SDL's whole notches), false when no turn that matches was seen.
    /// Turns before the match are ones whose event never reached Silk, and are dropped.
    /// </summary>
    public static bool TryTake(float x, float y, out ScrollTurn turn) => turns.TryTake(x, y, out turn);

    /// <summary>Drops what was kept, for a test or when a window is gone.</summary>
    public static void Clear() => turns.Clear();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Watch(void* userdata, Event* e)
    {
        try
        {
            if (e->Type != SDL_MOUSEWHEEL)
                return 1;

            ref MouseWheelEvent wheel = ref e->Wheel;
            float x = wheel.PreciseX, y = wheel.PreciseY;
            bool precise = false;
            nint phase = 0, momentum = 0;
            if (OperatingSystem.IsMacOS())
            {
                precise = ReadMac(ref x, ref y, ref phase, ref momentum);

                // The monitor said this one already, with its phase, and Silk's copy is let go by
                if (precise && monitorWindow != 0)
                    x = y = 0.0f;
            }

            turns.Add(wheel.X, wheel.Y, new ScrollTurn(x, y, precise));
            if (tracePath is not null)
                Trace(wheel.X, wheel.Y, wheel.PreciseX, wheel.PreciseY, x, y, precise, phase, momentum);
        }
        catch (Exception)
        {
            // an event that couldn't be read is a turn of whole notches, not a crash of SDL's
        }

        return 1;
    }

    /// <summary>
    /// A trackpad's or Magic Mouse's turn as the points it is (in SDL's tenths of a point, what the rest takes), from the
    /// <c>NSEvent</c>. SDL's own figure is <c>deltaX/Y</c>, AppKit's lines, which aren't the finger's distance.
    /// </summary>
    [SupportedOSPlatform("macos")]
    private static bool ReadMac(ref float x, ref float y, ref nint phase, ref nint momentum)
    {
        nint current = MacObjC.Send(MacObjC.SharedApplication, MacObjC.Sel("currentEvent"));
        if (current == 0 || MacObjC.SendInt(current, MacObjC.Sel("type")) != SCROLL_WHEEL)
            return false;

        if (tracePath is not null)
        {
            phase = MacObjC.SendInt(current, MacObjC.Sel("phase"));
            momentum = MacObjC.SendInt(current, MacObjC.Sel("momentumPhase"));
        }

        if (!MacObjC.SendBool(current, MacObjC.Sel("hasPreciseScrollingDeltas")))
            return false;

        // Already the way the content goes (natural scrolling or not), across as well as up and down. Turned round to
        // SDL's way across it went the wrong way
        x = (float)(MacObjC.SendDouble(current, MacObjC.Sel("scrollingDeltaX")) / Horizon.Input.PreciseScroll.MacPointsPerUnit);
        y = (float)(MacObjC.SendDouble(current, MacObjC.Sel("scrollingDeltaY")) / Horizon.Input.PreciseScroll.MacPointsPerUnit);
        return true;
    }

    /* The monitor: an NSEvent local monitor sees each of a trackpad's scroll events before SDL does, including those with no
       delta at all that only say the fingers came off, which SDL drops (SDL_SendMouseWheel ignores 0,0). Those are what the
       rubber band at an end needs: held while the fingers are on, sprung back when they lift, bounced by the glide. So a
       precise turn is said from here, phase and all, and the watch has Silk's copy of it go by */

    /// <summary>NSEventMaskScrollWheel.</summary>
    private const ulong SCROLL_WHEEL_MASK = 1UL << 22;

    /// <summary>BLOCK_IS_GLOBAL, a block that is never copied or freed.</summary>
    private const int BLOCK_IS_GLOBAL = 1 << 28;

    private static nint monitorWindow;
    private static Action<Horizon.Input.InputEvent>? raise;

    /// <summary>
    /// Says the precise scroll events of <paramref name="window"/> through <paramref name="said"/>, with the phase of the
    /// gesture, once. Without it (it couldn't be set up) they come through Silk as before, with no phase.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static void Monitor(nint window, Action<Horizon.Input.InputEvent> said)
    {
        if (window == 0 || monitorWindow != 0 || !Active)
            return;

        try
        {
            nint system = NativeLibrary.Load("/usr/lib/libSystem.B.dylib");
            nint isa = NativeLibrary.GetExport(system, "_NSConcreteGlobalBlock");

            // A block literal as clang lays one out: isa, flags, reserved, invoke, descriptor (reserved, size). Never freed,
            // AppKit keeps the handler for as long as the program runs
            nint* descriptor = (nint*)NativeMemory.AllocZeroed(2, (nuint)sizeof(nint));
            descriptor[1] = 4 * sizeof(nint);
            byte* block = (byte*)NativeMemory.AllocZeroed(4, (nuint)sizeof(nint));
            *(nint*)block = isa;
            *(int*)(block + sizeof(nint)) = BLOCK_IS_GLOBAL;
            *(nint*)(block + 2 * sizeof(nint)) = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint>)&Handle;
            *(nint*)(block + 3 * sizeof(nint)) = (nint)descriptor;

            raise = said;
            monitorWindow = window;
            nint monitor = MacObjC.Send(MacObjC.Class("NSEvent"), MacObjC.Sel("addLocalMonitorForEventsMatchingMask:handler:"), (nint)SCROLL_WHEEL_MASK, (nint)block);
            if (monitor == 0)
            {
                monitorWindow = 0;
                return;
            }

            MacObjC.Send(monitor, MacObjC.Sel("retain"));
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            monitorWindow = 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Handle(nint block, nint e)
    {
        try
        {
            if (OperatingSystem.IsMacOS() && e != 0 && tracePath is not null)
            {
                bool ours = MacObjC.Send(e, MacObjC.Sel("window")) == monitorWindow;
                bool fine = MacObjC.SendBool(e, MacObjC.Sel("hasPreciseScrollingDeltas"));
                Trace(0, 0, 0.0f, 0.0f, (float)MacObjC.SendDouble(e, MacObjC.Sel("scrollingDeltaX")), (float)MacObjC.SendDouble(e, MacObjC.Sel("scrollingDeltaY")),
                    fine, MacObjC.SendInt(e, MacObjC.Sel("phase")), MacObjC.SendInt(e, MacObjC.Sel("momentumPhase")), ours ? "seen-ours" : "seen-other");
            }

            if (OperatingSystem.IsMacOS() && e != 0 && MacObjC.Send(e, MacObjC.Sel("window")) == monitorWindow
                && MacObjC.SendBool(e, MacObjC.Sel("hasPreciseScrollingDeltas")))
            {
                nint phase = MacObjC.SendInt(e, MacObjC.Sel("phase"));
                nint momentum = MacObjC.SendInt(e, MacObjC.Sel("momentumPhase"));
                float x = (float)(MacObjC.SendDouble(e, MacObjC.Sel("scrollingDeltaX")) / Horizon.Input.PreciseScroll.MacPointsPerUnit);
                float y = (float)(MacObjC.SendDouble(e, MacObjC.Sel("scrollingDeltaY")) / Horizon.Input.PreciseScroll.MacPointsPerUnit);
                var said = Horizon.Input.PreciseScroll.PhaseOf(phase, momentum);
                if (x != 0.0f || y != 0.0f || said is not Horizon.Input.ScrollPhase.None)
                {
                    raise?.Invoke(new Horizon.Input.InputEvent
                    {
                        Kind = Horizon.Input.InputEventKind.Scroll, ScrollX = x, ScrollY = y, ScrollPrecise = true,
                        ScrollPhase = said, Timestamp = Stopwatch.GetTimestamp()
                    });
                }

                if (tracePath is not null)
                    Trace(0, 0, 0.0f, 0.0f, x, y, true, phase, momentum, "monitor");
            }
        }
        catch (Exception)
        {
            // an event that couldn't be read goes on to SDL as it is
        }

        return e;
    }

    private static void Trace(int wholeX, int wholeY, float sdlX, float sdlY, float x, float y, bool precise, nint phase, nint momentum, string source = "sdl")
    {
        try
        {
            File.AppendAllText(tracePath!, FormattableString.Invariant(
                $"{Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency:F3} {source} whole={wholeX},{wholeY} sdl={sdlX:F4},{sdlY:F4} turn={x:F4},{y:F4} precise={precise} phase={phase} momentum={momentum}\n"));
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>A turn of the wheel as it really was: SDL's units (a notch, or a tenth of a point for a Mac's trackpad) and whether a precise device made it.</summary>
internal readonly record struct ScrollTurn(float X, float Y, bool Precise);

/// <summary>
/// The turns the event watch saw, in order, for <see cref="MacScrollWatch.TryTake"/> to pair with the events Silk says. SDL
/// can drop or filter an event after the watch saw it, so a turn is only the one wanted when its whole notches are the event's.
/// </summary>
internal sealed class ScrollTurnQueue
{
    private const int LIMIT = 512;
    private const float EPSILON = 1e-6f;
    private readonly Lock gate = new();
    private readonly Queue<(float WholeX, float WholeY, ScrollTurn Turn)> turns = new();

    public void Add(float wholeX, float wholeY, ScrollTurn turn)
    {
        lock (gate)
        {
            turns.Enqueue((wholeX, wholeY, turn));
            while (turns.Count > LIMIT)
                turns.Dequeue();
        }
    }

    /// <summary>Takes the first turn whose whole notches are (x, y), dropping the ones before it; none that is leaves the queue as it was.</summary>
    public bool TryTake(float x, float y, out ScrollTurn turn)
    {
        lock (gate)
        {
            int skip = 0;
            foreach (var kept in turns)
            {
                if (MathF.Abs(kept.WholeX - x) <= EPSILON && MathF.Abs(kept.WholeY - y) <= EPSILON)
                {
                    for (int i = 0; i < skip; i++)
                        turns.Dequeue();
                    turn = turns.Dequeue().Turn;
                    return true;
                }

                skip++;
            }
        }

        turn = default;
        return false;
    }

    public void Clear()
    {
        lock (gate)
            turns.Clear();
    }
}
