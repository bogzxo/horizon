using Horizon.Core;

using Silk.NET.GLFW;
using Silk.NET.Input;
using Silk.NET.Windowing;

namespace Horizon.Platform.Desktop;

// How the window's thread waits between two looks at the system. A game's looks a thousand times a second, woken early
// for whatever the thread that draws needs done to the window. An application's (ApplicationBehaviour.WaitForEvents)
// waits for GLFW or SDL to have something to say while nothing is happening, which the engine decides, and is woken by
// an event pushed at it. Every kind of input is told to the engine too, which is what wakes an application gone quiet.
public sealed partial class DesktopWindow
{
    // How long (in milliseconds) the window's thread waits during manual drag/slide polling
    private const int EVENT_POLL = 1;

    // Fallback poll interval (in milliseconds) when neither GLFW nor SDL event waiting is available
    private const int FALLBACK_POLL = 4;

    // Bounded wait (in milliseconds) in WaitForWork while draining teardown tasks (woken immediately by WakeWindow)
    private const int WORK_WAIT_MS = 16;

    // How long the window's thread waits for the system while active (woken immediately by OS input or WakeWindow)
    private const double ACTIVE_WAIT = 0.004;

    // How long the window's thread waits for the system while idle; WakeWindow (PostEmptyEvent/WakeSdl) wakes it immediately on close/post
    private const double EVENT_WAIT = 0.5;

    private readonly AutoResetEvent windowWake = new(false);
    private readonly WindowWakeGate wakeGate = new();

    // GLFW, to wait for its events and to wake the wait from another thread. Null on a Mac, whose window is SDL's
    private Glfw? glfw;

    // Whether anything was listened to for the engine, so it is let go of again
    private bool listening;

    /// <summary>
    /// Helper method to listen to every way input comes in, for an application, and to say whether the window has the
    /// keyboard as it came up. Thread of the window, once.
    /// </summary>
    private unsafe bool ListenForInput()
    {
        // The window's state is everybody's, a game pauses its fight on IsMinimized. The rest is an application's,
        // which wakes on input and hears focus
        _window.StateChanged += OnState;
        if (behaviour == ApplicationBehaviour.None || _input is null)
            return true;

        listening = true;
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

        // Creation can finish without a focus transition (for example, Windows refused to activate a new
        // background window). Seed the cached state from the native window before relying on later events.
        if (UsesSdl && SdlWindow is var sdlWindow && sdlWindow != null)
            return (Silk.NET.SDL.SdlProvider.SDL.Value.GetWindowFlags(sdlWindow) & (uint)Silk.NET.SDL.WindowFlags.InputFocus) != 0;
        if (Silk.NET.Windowing.Glfw.GlfwWindowing.GetExistingApi(_window) is { } native)
            return native.GetWindowAttrib(Silk.NET.Windowing.Glfw.GlfwWindowing.GetHandle(_window), WindowAttributeGetter.Focused);
        return true;
    }

    /// <summary>Helper method to stop listening for the engine. Thread of the window, as it goes.</summary>
    private void StopListening()
    {
        _window.StateChanged -= OnState;
        if (!listening || _input is null)
            return;

        listening = false;
        foreach (IMouse mouse in _input.Mice)
        {
            mouse.MouseMove -= OnMouseMove;
            mouse.MouseDown -= OnMouseButton;
            mouse.MouseUp -= OnMouseButton;
            mouse.Scroll -= OnScroll;
        }

        foreach (IKeyboard keyboard in _input.Keyboards)
        {
            keyboard.KeyDown -= OnKey;
            keyboard.KeyUp -= OnKey;
            keyboard.KeyChar -= OnChar;
        }

        _window.FocusChanged -= OnFocus;
    }

    // Methods rather than lambdas, handed over once, a frame allocates nothing
    private void OnMouseMove(IMouse mouse, System.Numerics.Vector2 at) => NoteInput();
    private void OnMouseButton(IMouse mouse, Silk.NET.Input.MouseButton button) => NoteInput();
    private void OnScroll(IMouse mouse, ScrollWheel wheel) => NoteInput();
    private void OnKey(IKeyboard keyboard, Silk.NET.Input.Key key, int code) => NoteInput();
    private void OnChar(IKeyboard keyboard, char character) => NoteInput();
    private void OnFocus(bool focused)
    {
        if (OperatingSystem.IsMacOS())
            MarkMacChromeDirty();
        Events.FocusChanged(focused);
    }

    private void OnState(WindowState state)
    {
        if (OperatingSystem.IsMacOS())
            MarkMacChromeDirty();
        Events.MinimizedChanged(state == WindowState.Minimized);
    }

    /// <summary>
    /// Helper method for the window's thread to wait between two looks at the system. A moment, woken early by whoever
    /// wants something done on it, or while nothing is happening as long as the system has nothing to say.
    /// </summary>
    private void WaitForEvents()
    {
        if (closing)
            return;

        // Reset the coalescing flag BEFORE entering any wait so a concurrent WakeWindow() always signals the OS/event.
        if (wakeGate.TryConsume())
            return;

        bool canOsWait = (glfw is not null || sdlWaiting) && drag == Drag.None && !sliding;
        if (!canOsWait)
        {
            int pollMs = drag != Drag.None || sliding ? EVENT_POLL : FALLBACK_POLL;
            windowWake.WaitOne(pollMs);
            return;
        }

        // Wait on the OS event queue both while active (short timeout, woken with 0 ms latency by input/WakeWindow
        // without needing timeBeginPeriod(1)) and while idle/dormant (long timeout to avoid needless wakeups).
        double wait = (behaviour & ApplicationBehaviour.WaitForEvents) == 0 || !Events.MayWaitForEvents
            ? ACTIVE_WAIT
            : Events.Dormant ? DORMANT_WAIT : EVENT_WAIT;
        if (sdlWaiting)
            WaitForSdl(Math.Max(1, (int)(wait * 1000.0)));
        else
            glfw!.WaitEventsTimeout(wait);
    }

    // How long the window's thread waits for the system while the program is dormant
    private const double DORMANT_WAIT = 60.0;

    public void WakeWindowThread() => WakeWindow();

    /// <summary>Consumes any pending wake signal at the start of a work/event pump pass.</summary>
    internal void ConsumeWake() => wakeGate.TryConsume();

    /// <summary>Helper method to wait for work for the window's thread and nothing else, for a moment. Thread of the window.</summary>
    internal void WaitForWork()
    {
        if (wakeGate.TryConsume())
            return;

        windowWake.WaitOne(WORK_WAIT_MS);
    }

    /// <summary>Wakes the window's thread, whichever way it is waiting. From any thread.</summary>
    internal void WakeWindow()
    {
        // Coalesce redundant wake calls from high-frequency producers until the window thread consumes the signal.
        if (!wakeGate.TrySignal())
            return;

        try
        {
            windowWake.Set();
        }
        catch (ObjectDisposedException)
        {
            // Gone already, there's nobody left to wake
            return;
        }

        if (glfw is not null)
            glfw.PostEmptyEvent();
        else if (sdlWaiting)
            WakeSdl();
    }

    /// <summary>Helper method to get hold of GLFW for waiting on its events, where the window is GLFW's. Thread of the window.</summary>
    private void SetUpWaiting()
    {
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
            // No GLFW to wait on, fall back to the bounded wait handle
            glfw = null;
        }
    }
}

/// <summary>
/// Coalesces cross-thread wake requests so high-frequency producers only trigger the underlying OS/event wakeup once
/// per wait cycle while guaranteeing zero lost wakeups.
/// </summary>
internal sealed class WindowWakeGate
{
    private int pending;

    /// <summary>True when a wake signal is currently pending and not yet consumed by the waiting thread.</summary>
    public bool IsPending => Volatile.Read(ref pending) != 0;

    /// <summary>
    /// Marks a wake as pending. Returns true only on the 0 -> 1 transition, meaning the caller should perform the
    /// underlying OS or event wakeup; subsequent calls coalesce and return false until consumed.
    /// </summary>
    public bool TrySignal() => Interlocked.Exchange(ref pending, 1) == 0;

    /// <summary>
    /// Consumes any pending wake signal before entering a wait. Returns true if a wake was pending (the caller must
    /// skip blocking and re-check work/events); returns false if none was pending (safe to enter the wait).
    /// </summary>
    public bool TryConsume() => Interlocked.Exchange(ref pending, 0) != 0;
}
