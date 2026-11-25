using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Horizon.Platform;
using Silk.NET.SDL;

namespace Horizon.Platform.Desktop;

/// <summary>
/// Whether a close of the window is the application being quit. On a Mac Cmd+Q, the Dock's Quit and logout reach SDL as
/// <c>terminate:</c>, which it turns into <c>SDL_QUIT</c>, and the red button is <c>SDL_WINDOWEVENT_CLOSE</c> that SDL
/// follows with an <c>SDL_QUIT</c> too once the last window closes, so a program only seeing its window close can't tell
/// them apart. An event watch sees both as SDL pushes them, in order, and <see cref="AppQuitTracker"/> says which a quit was.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe class MacQuitWatch
{
    private const uint SDL_QUIT = 0x100;
    private const uint SDL_WINDOWEVENT = 0x200;
    private const byte SDL_WINDOWEVENT_CLOSE = 14;

    private static readonly AppQuitTracker tracker = new();
    private static int installed;

    /// <summary>Whether quits are being told from window closes, only after <see cref="Install"/> worked.</summary>
    public static bool Active { get; private set; }

    /// <summary>Starts watching SDL's events for a quit, once.</summary>
    public static void Install()
    {
        if (!OperatingSystem.IsMacOS() || Interlocked.Exchange(ref installed, 1) != 0)
            return;

        try
        {
            SdlProvider.SDL.Value.AddEventWatch(new PfnEventFilter(&Watch), null);
            Active = true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // no SDL to ask, a quit stays a close of the window
        }
    }

    /// <summary>Whether the close being decided now is the application quitting. Asked once for each close.</summary>
    public static bool TakeQuit() => Active && tracker.TakeQuit();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Watch(void* userdata, Event* e)
    {
        try
        {
            if (e->Type == SDL_QUIT)
                tracker.QuitSeen();
            else if (e->Type == SDL_WINDOWEVENT && e->Window.Event == SDL_WINDOWEVENT_CLOSE)
                tracker.WindowCloseSeen();
        }
        catch (Exception)
        {
            // an event that couldn't be read is a close of the window, not a crash of SDL's
        }

        return 1;
    }
}
