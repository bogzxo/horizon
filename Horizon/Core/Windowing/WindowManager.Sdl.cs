using Silk.NET.SDL;

namespace Horizon.Core;

// Which of the two the window is made with, and what SDL needs that GLFW didn't. GLFW on Windows, where the frame is
// Windows' own to drag and snap (WindowManager.Frame.Windows). SDL on a Mac, where GLFW has no Vulkan, and on Linux,
// where GLFW's window has no way to be dragged on Wayland (a Wayland client can't say where its window is, the compositor
// moves it, which SDL asks for through its hit test, see WindowManager.Frame) and closed itself under XWayland as it was
// made. HORIZON_WINDOWING=glfw or sdl says otherwise, for finding out which of them something is down to.
public partial class WindowManager
{
    /// <summary>Whether the window is SDL's, rather than GLFW's.</summary>
    public static bool UsesSdl { get; } = Environment.GetEnvironmentVariable("HORIZON_WINDOWING")?.ToLowerInvariant() switch
    {
        "glfw" => false,
        "sdl" => true,
        _ => !OperatingSystem.IsWindows()
    };

    // What an event pushed only to wake the window's thread is, one of SDL's own for a program to use
    private const uint SDL_USEREVENT = 0x8000;

    // Whether the window's thread waits on SDL's events while nothing is happening, see WindowManager.Application
    private bool sdlWaiting;

    /// <summary>
    /// Helper method to tell SDL what it reads from the environment as it starts, before it does. Wayland first and X11
    /// when there's no Wayland (an X session, or a desktop that has no Wayland), the window's name to the desktop, and
    /// no frame of libdecor's round a window that draws its own.
    /// </summary>
    private static void ConfigureSdl(string? appId)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsFreeBSD())
            return;

        Default("SDL_VIDEODRIVER", "wayland,x11");

        // The .desktop file the window belongs to, which is where a Wayland desktop gets its icon and name from
        string id = appId ?? Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "horizon";
        Default("SDL_VIDEO_WAYLAND_WMCLASS", id);
        Default("SDL_VIDEO_X11_WMCLASS", id);
        Default("SDL_APP_NAME", id);

        // A window with the system's frame gets libdecor's on a desktop with no frames of its own (GNOME)
        Default("SDL_VIDEO_WAYLAND_ALLOW_LIBDECOR", "1");

        // Every pixel the screen has, scaled by Horizon's UI rather than stretched by the compositor
        Default("SDL_VIDEO_WAYLAND_SCALE_TO_DISPLAY", "1");

        // The screen stays on while a game runs, an editor isn't one
        Default("SDL_VIDEO_ALLOW_SCREENSAVER", "1");

        static void Default(string name, string value)
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    /// <summary>
    /// Helper method to wait for SDL to have something to say, so long at the most. Leaves what came for the window to
    /// hear the usual way. Thread of the window.
    /// </summary>
    private static unsafe void WaitForSdl(int milliseconds) => SdlProvider.SDL.Value.WaitEventTimeout((Event*)null, milliseconds);

    /// <summary>Helper method to wake the window's thread out of <see cref="WaitForSdl"/>, from any thread.</summary>
    private static unsafe void WakeSdl()
    {
        Event wake = default;
        wake.Type = SDL_USEREVENT;
        SdlProvider.SDL.Value.PushEvent(&wake);
    }

    /// <summary>
    /// Whether the window can be put somewhere by the program, which a Wayland window can't (the compositor places it,
    /// and moves it when the title bar is dragged, see WindowManager.HitTest). Asked of SDL, which knows which it's on.
    /// </summary>
    public static unsafe bool CanPlaceWindow
    {
        get
        {
            if (!UsesSdl)
                return true;

            byte* driver = SdlProvider.SDL.Value.GetCurrentVideoDriver();
            return driver == null || System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)driver) != "wayland";
        }
    }

    /// <summary>The SDL window underneath, null when the window isn't SDL's (or isn't made yet).</summary>
    private unsafe Silk.NET.SDL.Window* SdlWindow =>
        UsesSdl && _window.Native?.Sdl is { } handle && handle != 0 ? (Silk.NET.SDL.Window*)handle : null;
}
