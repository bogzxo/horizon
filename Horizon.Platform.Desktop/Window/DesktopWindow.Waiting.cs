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
    // How long (in milliseconds) the thread of the window waits between two looks at what the system has to say
    private const int EVENT_POLL = 1;

    // How long the window's thread waits for the system while idle, the longest it takes to notice a closing flag
    private const double EVENT_WAIT = 0.1;

    private readonly AutoResetEvent windowWake = new(false);

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
    private void OnFocus(bool focused) => Events.FocusChanged(focused);
    private void OnState(WindowState state) => Events.MinimizedChanged(state == WindowState.Minimized);

    /// <summary>
    /// Helper method for the window's thread to wait between two looks at the system. A moment, woken early by whoever
    /// wants something done on it, or while nothing is happening as long as the system has nothing to say.
    /// </summary>
    private void WaitForEvents()
    {
        bool idle = (behaviour & ApplicationBehaviour.WaitForEvents) != 0 && (glfw is not null || sdlWaiting) && drag == Drag.None && !sliding && Events.MayWaitForEvents && !closing;
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

    /// <summary>Helper method to wait for work for the window's thread and nothing else, for a moment. Thread of the window.</summary>
    internal void WaitForWork() => windowWake.WaitOne(EVENT_POLL);

    /// <summary>Wakes the window's thread, whichever way it is waiting. From any thread.</summary>
    internal void WakeWindow()
    {
        try
        {
            windowWake.Set();
        }
        catch (ObjectDisposedException)
        {
            // Gone already, there's nobody left to wake
            return;
        }

        if (glfw is not null && (behaviour & ApplicationBehaviour.WaitForEvents) != 0)
            glfw.PostEmptyEvent();
        else if (sdlWaiting)
            WakeSdl();
    }

    /// <summary>Helper method to get hold of GLFW for waiting on its events, where the window is GLFW's. Thread of the window.</summary>
    private void SetUpWaiting()
    {
        if ((behaviour & ApplicationBehaviour.WaitForEvents) == 0)
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
