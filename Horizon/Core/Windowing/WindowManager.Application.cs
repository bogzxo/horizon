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

    // Methods rather than lambdas, handed over once, see Garbage in CLAUDE.md
    private void OnMouseMove(IMouse mouse, System.Numerics.Vector2 at) => NoteInput();
    private void OnMouseButton(IMouse mouse, Silk.NET.Input.MouseButton button) => NoteInput();
    private void OnScroll(IMouse mouse, ScrollWheel wheel) => NoteInput();
    private void OnKey(IKeyboard keyboard, Silk.NET.Input.Key key, int code) => NoteInput();
    private void OnChar(IKeyboard keyboard, char character) => NoteInput();
    private void OnFocus(bool focused) => NoteInput();
    private void OnState(Silk.NET.Windowing.WindowState state) => NoteInput();

    /// <summary>
    /// Helper method to hold the thread that draws until a frame is wanted, or a second has gone by. Nothing for a window
    /// that draws every frame. The thread that draws, before every frame.
    /// </summary>
    private void WaitForDemand()
    {
        if ((Behaviour & ApplicationBehaviour.DrawOnChange) == 0)
            return;

        while (!renderStopping)
        {
            if (Stopwatch.GetTimestamp() < Volatile.Read(ref drawUntil))
                return;

            if (!frameWanted.WaitOne(HEARTBEAT_MS))
                return;
        }
    }

    /// <summary>
    /// Helper method for the window's thread to wait between two looks at the system. A moment, woken early by whoever
    /// wants something done on it, or while nothing is happening as long as the system has nothing to say.
    /// </summary>
    private void WaitForEvents()
    {
        bool idle = (Behaviour & ApplicationBehaviour.WaitForEvents) != 0 && glfw is not null && drag == Drag.None && !InputLately && !closing;
        if (!idle)
        {
            windowWake.WaitOne(EVENT_POLL);
            return;
        }

        glfw!.WaitEventsTimeout(EVENT_WAIT);
    }

    /// <summary>Wakes the window's thread, whichever way it is waiting. From any thread.</summary>
    private void WakeWindow()
    {
        windowWake.Set();
        if (glfw is not null && (Behaviour & ApplicationBehaviour.WaitForEvents) != 0)
            glfw.PostEmptyEvent();
    }

    /// <summary>Helper method to get hold of GLFW for waiting on its events, where the window is GLFW's. Thread of the window.</summary>
    private void SetUpWaiting()
    {
        if ((Behaviour & ApplicationBehaviour.WaitForEvents) == 0 || OperatingSystem.IsMacOS())
            return;

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
