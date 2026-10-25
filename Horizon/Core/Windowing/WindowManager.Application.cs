using System.Diagnostics;

using Silk.NET.GLFW;
using Silk.NET.Input;

namespace Horizon.Core;

// A window run as an application (RunMode.Application) rather than a game, an editor that sits there doing nothing
// most of the time. It draws a frame only when something wants one, ticks a few times a second while nothing is
// happening, and its thread waits for the system instead of asking it a thousand times a second, see
// ApplicationBehaviour. A game has none of it unless it asks, every frame the screen shows, every tick at the rate.
public partial class WindowManager
{
    // How long after the last input a window counts as idle, and how many ticks without a UI changing
    private const double IDLE_AFTER = 0.25;
    private const long IDLE_TICKS = 2;

    // How long frames are drawn after input or a change, enough for the blend between two ticks and a refresh more
    private const double FRAMES_AFTER = 0.05;

    // The frame drawn a second however still everything is, so nothing anybody forgot to ask for stays stale for long
    private const int HEARTBEAT_MS = 1000;

    // How long the window's thread waits for the system while idle, the longest it takes to notice a closing flag
    private const double EVENT_WAIT = 0.1;

    // How long the simulation sleeps between idle ticks, in the window that has the keyboard, in one that hasn't (the
    // caret blinks a touch less evenly, nobody's looking at it), and minimised, where nothing's seen at all. That one
    // is as long as an idle tick can stretch (SimulationLoop.MAX_STRETCH), so whatever counts its time in ticks
    // (an auto save, a poll) still keeps time
    private const double IDLE_FOCUSED = 0.05, IDLE_UNFOCUSED = 0.1, IDLE_MINIMISED = 0.25;

    // How long frames are still drawn after the window is minimised, enough for the one that sizes everything down
    private const double FRAMES_MINIMISED = 0.25;

    // Whether the window has the keyboard and whether it's minimised, kept by the window's thread, and when it was minimised
    private volatile bool windowFocused = true, windowMinimised;
    private long minimisedAt;

    /// <summary>Whether the window has the keyboard, for whatever only moves for somebody looking (a caret's blink). From any thread.</summary>
    public bool IsFocused => windowFocused;

    /// <summary>What the window is run for, see <see cref="WindowManagerConfiguration.Mode"/>.</summary>
    public RunMode Mode { get; }

    /// <summary>Which of the ways of saving work while nothing is happening are on, see <see cref="ApplicationBehaviour"/>.</summary>
    public ApplicationBehaviour Behaviour { get; }

    // The last input (stopwatch ticks), the last tick a UI painted something new on, and until when frames are drawn
    private long lastInput, lastChangeTick, drawUntil;
    private readonly AutoResetEvent frameWanted = new(false);

    // GLFW, to wait for its events and to wake the wait from another thread. Null on a Mac, whose window is SDL's
    private Glfw? glfw;

    /// <summary>
    /// Asks for frames to be drawn for a while, for a window that only draws when something wants it to
    /// (<see cref="ApplicationBehaviour.DrawOnChange"/>). Nothing for one that draws every frame anyway. From any thread.
    /// </summary>
    public void RequestFrames(double seconds = FRAMES_AFTER)
    {
        if ((Behaviour & ApplicationBehaviour.DrawOnChange) == 0)
            return;

        long until = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
        long had;
        do
        {
            had = Volatile.Read(ref drawUntil);
            if (had >= until)
                break;
        } while (Interlocked.CompareExchange(ref drawUntil, until, had) != had);

        frameWanted.Set();
    }

    /// <summary>
    /// Says that what is on screen changed (a UI painted something new), which keeps the simulation at its full rate and
    /// has frames drawn to show it. The compositors say so by themselves. From the simulation thread.
    /// </summary>
    public void NoteChange()
    {
        Volatile.Write(ref lastChangeTick, Tick);
        RequestFrames();
    }

    /// <summary>
    /// Wakes an application that went quiet, from any thread, for work that finished somewhere else (a search, a file
    /// read, a program run off the simulation's thread) and is waiting to be taken in on the next tick. Without it the
    /// result waited for the next slow tick of an idle simulation, a twentieth of a second.
    /// </summary>
    public void Wake()
    {
        simulation?.Wake();
        RequestFrames();
    }

    /// <summary>Helper method for input of any kind, which wakes everything up. Thread of the window.</summary>
    private void NoteInput()
    {
        Volatile.Write(ref lastInput, Stopwatch.GetTimestamp());
        simulation?.Wake();
        RequestFrames();
    }

    private bool InputLately => Stopwatch.GetTimestamp() - Volatile.Read(ref lastInput) < (long)(IDLE_AFTER * Stopwatch.Frequency);

    /// <summary>Whether nothing is happening, no input lately, no UI changed for a couple of ticks, nothing waiting to be done.</summary>
    private bool IdleNow() =>
        !InputLately && Tick - Volatile.Read(ref lastChangeTick) > IDLE_TICKS && !exclusiveRequested && !EntityLifecycle.HasWork;

    /// <summary>Helper method to listen to every way input comes in, for an application. Thread of the window, once.</summary>
    private void ListenForInput()
    {
        if (Behaviour == ApplicationBehaviour.None)
            return;

        foreach (IMouse mouse in _input.Mice)
        {
            mouse.MouseMove += OnMouseMove;
            mouse.MouseDown += OnMouseButton;
            mouse.MouseUp += OnMouseButton;
            mouse.Scroll += OnScroll;
        }

        foreach (IKeyboard keyboard in _input.Keyboards)
        {
            keyboard.KeyDown += OnKey;
            keyboard.KeyUp += OnKey;
            keyboard.KeyChar += OnChar;
        }

        _window.FocusChanged += OnFocus;
        _window.StateChanged += OnState;
    }

    // Methods rather than lambdas, handed over once, a frame allocates nothing
    private void OnMouseMove(IMouse mouse, System.Numerics.Vector2 at) => NoteInput();
    private void OnMouseButton(IMouse mouse, Silk.NET.Input.MouseButton button) => NoteInput();
    private void OnScroll(IMouse mouse, ScrollWheel wheel) => NoteInput();
    private void OnKey(IKeyboard keyboard, Silk.NET.Input.Key key, int code) => NoteInput();
    private void OnChar(IKeyboard keyboard, char character) => NoteInput();
    private void OnFocus(bool focused)
    {
        windowFocused = focused;
        UpdateIdlePeriod();
        NoteInput();
    }

    private void OnState(Silk.NET.Windowing.WindowState state)
    {
        bool minimised = state == Silk.NET.Windowing.WindowState.Minimized;
        if (minimised && !windowMinimised)
            Volatile.Write(ref minimisedAt, Stopwatch.GetTimestamp());
        windowMinimised = minimised;
        UpdateIdlePeriod();
        NoteInput();
    }

    /// <summary>
    /// Helper method to have the simulation sleep longer between idle ticks while nobody's looking, see IDLE_FOCUSED.
    /// An application only, a self test run as a game keeps its pace. Any thread.
    /// </summary>
    private void UpdateIdlePeriod()
    {
        if (simulation is not { } loop || Mode != RunMode.Application)
            return;

        loop.IdlePeriod = windowMinimised ? IDLE_MINIMISED : windowFocused ? IDLE_FOCUSED : IDLE_UNFOCUSED;
    }

    /// <summary>
    /// Whether frames are held back because the window is minimised and has been for a moment, whatever painted
    /// something new. There's nothing to see, and the window has been sized down already. An application only.
    /// </summary>
    private bool Hidden => Mode == RunMode.Application && windowMinimised &&
        Stopwatch.GetTimestamp() - Volatile.Read(ref minimisedAt) > (long)(FRAMES_MINIMISED * Stopwatch.Frequency);

    /// <summary>
    /// Helper method to hold the thread that draws until a frame is wanted, or a second has gone by. Nothing for a window
    /// that draws every frame. Minimised, only for work that waits on a frame to be done (a scene swapped, something
    /// added to be set up), not for what painted something new or the heartbeat. The thread that draws, before every frame.
    /// </summary>
    private void WaitForDemand()
    {
        if ((Behaviour & ApplicationBehaviour.DrawOnChange) == 0)
            return;

        while (!renderStopping)
        {
            bool hidden = Hidden;
            if (hidden ? exclusiveRequested || EntityLifecycle.HasWork : Stopwatch.GetTimestamp() < Volatile.Read(ref drawUntil))
                return;

            if (!frameWanted.WaitOne(HEARTBEAT_MS) && !hidden)
                return;
        }
    }

    /// <summary>
    /// Helper method for the window's thread to wait between two looks at the system. A moment, woken early by whoever
    /// wants something done on it, or while nothing is happening as long as the system has nothing to say.
    /// </summary>
    private void WaitForEvents()
    {
        bool idle = (Behaviour & ApplicationBehaviour.WaitForEvents) != 0 && (glfw is not null || sdlWaiting) && drag == Drag.None && !sliding && !InputLately && !closing;
        if (!idle)
        {
            windowWake.WaitOne(EVENT_POLL);
            return;
        }

        if (sdlWaiting)
            WaitForSdl((int)(EVENT_WAIT * 1000.0));
        else
            glfw!.WaitEventsTimeout(EVENT_WAIT);
    }

    /// <summary>Wakes the window's thread, whichever way it is waiting. From any thread.</summary>
    private void WakeWindow()
    {
        windowWake.Set();
        if (glfw is not null && (Behaviour & ApplicationBehaviour.WaitForEvents) != 0)
            glfw.PostEmptyEvent();
        else if (sdlWaiting)
            WakeSdl();
    }

    /// <summary>Helper method to get hold of GLFW for waiting on its events, where the window is GLFW's. Thread of the window.</summary>
    private void SetUpWaiting()
    {
        if ((Behaviour & ApplicationBehaviour.WaitForEvents) == 0)
            return;

        // SDL's window waits on SDL, which a Mac's had nothing to wait on and asked the system every millisecond
        if (UsesSdl)
        {
            sdlWaiting = true;
            return;
        }

        try
        {
            glfw = Glfw.GetApi();
        }
        catch (Exception)
        {
            // No GLFW to wait on, the window asks the system every millisecond the way a game's does
            glfw = null;
        }
    }
}
