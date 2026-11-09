using Horizon.Platform;

namespace Horizon.Core;

// What the window manager hears from the platform it runs on and what it hands on to it. The host says how big things
// are, when the window got the keyboard and when input came in, the window manager decides what the engine does about
// it. The keys for everywhere and the drop down a terminal wants are the platform's, asked for here.
public partial class WindowManager
{
    /// <summary>
    /// Whether the window's sizes are in the system's units rather than pixels and its UI is drawn at the scale of the
    /// screen it's on, see <see cref="WindowManagerConfiguration.ScaleToDisplay"/>.
    /// </summary>
    public bool ScalesToDisplay { get; }

    /// <summary>
    /// How many pixels of what is drawn there are to one of the system's units on the screen the window is on right now,
    /// 1 at 100%, 1.5 at 150%, 2 on a Retina screen. Kept up by the platform as the window moves between screens, read from any thread.
    /// </summary>
    public float DisplayScale => platformDisplay.DisplayScale;

    /// <summary>
    /// How many of the system's units on the screen the window is on there are to whatever it counts the window and the
    /// pointer in. The same as <see cref="DisplayScale"/> on Windows, which counts in pixels, and 1 on a Mac or Wayland,
    /// which count in points already. For whoever measures something in the window's coordinates (a grab at its edge).
    /// </summary>
    public float WindowUnitScale => DisplayScale / MathF.Max(0.01f, PixelScale.X);

    /// <summary>
    /// Registers a key that is heard whatever window is in front, "Ctrl+`", "F12", "Ctrl+Alt+Space". Its id, which
    /// <see cref="TakeHotKey"/> asks after, or -1 where there are no such keys. From any thread.
    /// </summary>
    public int RegisterHotKey(string shortcut) => host.Window?.RegisterHotKey(shortcut) ?? -1;

    /// <summary>A key registered with <see cref="RegisterHotKey"/> let go of. From any thread.</summary>
    public void UnregisterHotKey(int id) => host.Window?.UnregisterHotKey(id);

    /// <summary>Whether a registered key was pressed since it was last asked, once a press. From any thread.</summary>
    public bool TakeHotKey(int id) => host.Window?.TakeHotKey(id) ?? false;

    /// <summary>Whether the window is down from the top of the screen, as far as the last slide went.</summary>
    public bool IsDropped => host.Window?.IsDropped ?? false;

    /// <summary>
    /// The window slid down from the top of the screen it is on, as wide as that and a share of it high, shown and in
    /// front, or back up out of sight and hidden, in so many seconds (0 at once). From any thread.
    /// </summary>
    public void Drop(bool down, float share = 0.45f, float seconds = 0.18f) => host.Window?.Drop(down, share, seconds);

    /// <summary>
    /// The window shown, put back from the taskbar if it was down there, and in front, for a program asked to by another
    /// start of itself. From any thread.
    /// </summary>
    public void BringToFront() => host.Window?.BringToFront();

    /// <summary>Helper method to take in how big things are now. Platform thread.</summary>
    private void TakeDisplay(PlatformDisplay now, bool resized)
    {
        platformDisplay = now;
        display = new DisplayState(now.ViewportSize, now.WindowSize, now.ScreenSize);
        if (!resized)
            return;

        this.resized = true;

        // SDL sets the layer's drawables to the size of the window whenever that changes, and a swapchain that stays
        // the size it was (a window going fullscreen on a Mac) is then shown small in a corner of them until it is made anew
        Graphics?.SurfaceChanged();
    }

    /// <summary>What the host tells the window manager, kept off its public face.</summary>
    private sealed class HostEvents(WindowManager window) : IPlatformEvents
    {
        public void DisplayChanged(PlatformDisplay display, bool resized) => window.TakeDisplay(display, resized);

        public void FocusChanged(bool focused) => window.OnFocus(focused);

        public void MinimizedChanged(bool minimized) => window.OnMinimized(minimized);

        public void InputHeard() => window.NoteInput();

        public void EventsProcessed() => window.EventsProcessed?.Invoke();

        public void RequestFrames() => window.RequestFrames();

        public void Wake() => window.Wake();

        public bool MayClose() => window.MayClose();

        public bool MayWaitForEvents => window.MayWaitForEvents;
    }
}
