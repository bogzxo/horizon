using System.Numerics;
using System.Runtime.InteropServices;

namespace Horizon.Core;

// How big the screen the window is on wants things drawn. Windows has a scale for every screen (150% on a 4K one is
// usual) and GLFW tells it the program does its own scaling, so nothing is stretched for it and a UI laid out a unit
// per pixel came out tiny there. A window that ScalesToDisplay has its sizes in the system's units instead, the
// pixels of a screen at 100%, and its compositors draw that much bigger (UICompositor.DisplayScale). Dragged onto
// another screen it is resized by that screen's scale, so it looks the same size on both, and the UI follows.
public partial class WindowManager
{
    private const float DEFAULT_DPI = 96.0f;

    /// <summary>
    /// Whether the window's sizes are in the system's units rather than pixels and its UI is drawn at the scale of the
    /// screen it's on, see <see cref="WindowManagerConfiguration.ScaleToDisplay"/>.
    /// </summary>
    public bool ScalesToDisplay { get; }

    /// <summary>
    /// How many pixels of what is drawn there are to one of the system's units on the screen the window is on right now,
    /// 1 at 100%, 1.5 at 150%, 2 on a Retina screen. Kept up on the window's thread as it moves between screens, read from any.
    /// </summary>
    public float DisplayScale => displayScale;

    private volatile float displayScale = 1.0f;

    /// <summary>
    /// How many of the system's units on the screen the window is on there are to whatever it counts the window and the
    /// pointer in. The same as <see cref="DisplayScale"/> on Windows, which counts in pixels, and 1 on a Mac or Wayland,
    /// which count in points already. For whoever measures something in the window's coordinates (a grab at its edge).
    /// </summary>
    public float WindowUnitScale => displayScale / MathF.Max(0.01f, PixelScale.X);

    /// <summary>
    /// Helper method to find out the scale of the screen the window is on now, and to wake everything up to draw at it
    /// when it changed. Thread of the window, whenever it may have moved to another screen.
    /// </summary>
    private void UpdateDisplayScale()
    {
        float scale = OperatingSystem.IsWindows() && _window.Native?.Win32 is { Hwnd: not 0 } win32
            ? WindowsScaleOf(win32.Hwnd)
            : screenScale.X;

        if (!float.IsFinite(scale) || scale <= 0.0f || scale == displayScale)
            return;

        displayScale = scale;
        Logging.Log.Info($"[{Name}] The screen the window is on is scaled {scale * 100.0f:0}%.");
        NoteInput();
        RequestFrames();
    }

    /// <summary>
    /// Helper method to size a window made with its size in pixels by the scale of the screen it came up on, so a window
    /// asked to be 1600 by 900 is that much of a screen at 100% and half as big again at 150%. Thread of the window, as it loads.
    /// </summary>
    private void SizeToDisplay()
    {
        if (!ScalesToDisplay || !OperatingSystem.IsWindows() || displayScale == 1.0f || WindowOptions.WindowState != Silk.NET.Windowing.WindowState.Normal)
            return;

        var size = new Vector2(WindowOptions.Size.X, WindowOptions.Size.Y) * displayScale;
        _window.Size = new Silk.NET.Maths.Vector2D<int>((int)MathF.Round(size.X), (int)MathF.Round(size.Y));
        UpdateViewport();
    }

    private static float WindowsScaleOf(nint window)
    {
        try
        {
            uint dpi = GetDpiForWindow(window);
            return dpi > 0 ? dpi / DEFAULT_DPI : 1.0f;
        }
        catch (EntryPointNotFoundException)
        {
            // Older than Windows 10's 1607, everything is one scale there anyway
            return 1.0f;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
}
