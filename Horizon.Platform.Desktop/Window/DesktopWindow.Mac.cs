using System.Numerics;

using Silk.NET.SDL;

using Horizon.Core;

namespace Horizon.Platform.Desktop;

// A Mac's window chrome under the UI's title bar (see MacWindowChrome): the real traffic lights, native drag, tiling, full
// screen and zoom, with the UI's bar drawn across the top of a full-size-content window and leaving room for the lights.
// HORIZON_MAC_CHROME=off keeps the frameless window it was, for finding out which of the two something is down to.
public sealed partial class DesktopWindow
{
    private MacWindowChrome? chrome;

    // SDL's button release
    private const uint SDL_MOUSEBUTTONUP = 0x402;

    private static bool MacChromeWanted =>
        OperatingSystem.IsMacOS() && UsesSdl && !string.Equals(Environment.GetEnvironmentVariable("HORIZON_MAC_CHROME"), "off", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the system draws the traffic lights over the UI's title bar, and zooms and drags the window. From any thread.</summary>
    public bool NativeChrome => OperatingSystem.IsMacOS() && chrome is { Titled: true };

    /// <summary>Maximum refresh rate of the macOS display holding this window (120 on ProMotion Macs, 60 on standard displays, 0 if unavailable). From any thread.</summary>
    public int MacScreenMaxFps => OperatingSystem.IsMacOS() && chrome is { } own ? own.ScreenMaxFramesPerSecond() : 0;

    /// <summary>What the traffic lights cover of the window's top left corner, in the window's pixels, nothing in fullscreen. From any thread.</summary>
    public PlatformFrameInsets FrameInsets
    {
        get
        {
            if (!OperatingSystem.IsMacOS() || chrome is not { Titled: true } own)
                return default;

            Vector2 scale = pixelScale;
            return new PlatformFrameInsets(own.LeadingPoints * scale.X, own.HeightPoints * scale.Y, own.TrailingPoints * scale.X, own.ExactHeight);
        }
    }

    /// <summary>Tells the window how tall the UI's title bar is, in the window's pixels, for the traffic lights to be centred in it. From any thread.</summary>
    public void SetTitleBarHeight(float pixels)
    {
        if (!OperatingSystem.IsMacOS() || chrome is not { } own)
            return;

        float scale = MathF.Max(0.01f, pixelScale.Y);
        double points = pixels / scale;
        MacMain.Run(() =>
        {
            if (OperatingSystem.IsMacOS())
                own.SetBarHeight(points);
        });
    }

    /// <summary>Makes the window a titled, full-size-content one once SDL is done with its style. Thread of the window, the main thread.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private void SetUpMacChrome()
    {
        DesktopPower.EnsureInitialized();

        nint cocoa = CocoaHandle;
        if (!MacChromeWanted || !CustomFrame || cocoa == 0)
            return;

        MacMouseDownWatch.Install();
        MacMain.Run(() =>
        {
            chrome = MacWindowChrome.Create(cocoa, () => RequestFrames());
        });
    }

    /// <summary>Puts the style mask back if SDL changed it. Thread of the window, every tick.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private void MaintainMacChrome()
    {
        chrome?.Maintain();
        GiveBackRelease();
        SyncMinimized();
    }

    // SDL_WINDOW_MINIMIZED
    private const uint SDL_WINDOW_MINIMIZED = 0x40;
    private bool minimizedSeen;

    /// <summary>
    /// Tells the engine when the window goes down to the Dock and comes back, from SDL's own flag. Silk passes on the
    /// minimise but not the restore (deminiaturize), which left the engine thinking the window still hidden: frames held back
    /// and the window dead to everything until a full screen change redrew it. Thread of the window, every tick.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private unsafe void SyncMinimized()
    {
        Silk.NET.SDL.Window* handle = SdlWindow;
        if (handle == null)
            return;

        bool minimized = (SdlProvider.SDL.Value.GetWindowFlags(handle) & SDL_WINDOW_MINIMIZED) != 0;
        if (minimized == minimizedSeen)
            return;

        minimizedSeen = minimized;
        Events.MinimizedChanged(minimized);
        if (!minimized)
        {
            UpdateViewport();
            RequestFrames();
            Publish(resized: true);
        }
    }

    /// <summary>
    /// Has the system move the window with the press that is down, the way it moves any, which returns when the button is up.
    /// False when the window isn't one that does (or the press is gone), and the window is followed by hand.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private unsafe bool MoveWithSystem()
    {
        if (chrome is not { Titled: true } own)
            return false;

        bool moved = own.TryDrag();
        if (!moved)
            return false;

        // The drag takes the release of the button for itself, which leaves SDL with the button down and so everything that asks
        // it, see GiveBackRelease
        releaseWatchUntil = Environment.TickCount64 + RELEASE_WATCH_MS;
        GiveBackRelease();
        return true;
    }

    // How long after a drag by the system the button is watched for, and until when it is
    private const long RELEASE_WATCH_MS = 3000;
    private long releaseWatchUntil;

    /// <summary>
    /// Says the release SDL never heard (the system's drag took it) once the system says the button is up, if SDL still has it
    /// down. Thread of the window, every tick for a few seconds after a drag.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private unsafe void GiveBackRelease()
    {
        if (releaseWatchUntil == 0)
            return;

        Silk.NET.SDL.Window* handle = SdlWindow;
        if (Environment.TickCount64 > releaseWatchUntil || handle == null)
        {
            releaseWatchUntil = 0;
            return;
        }

        int x = 0, y = 0;
        if ((SdlProvider.SDL.Value.GetMouseState(&x, &y) & 1) == 0)
        {
            // SDL heard it, or never had it down
            if (!MacWindowChrome.LeftIsDown())
                releaseWatchUntil = 0;
            return;
        }

        if (MacWindowChrome.LeftIsDown())
            return;

        releaseWatchUntil = 0;
        Event up = default;
        up.Type = SDL_MOUSEBUTTONUP;
        up.Button.Type = SDL_MOUSEBUTTONUP;
        up.Button.WindowID = SdlProvider.SDL.Value.GetWindowID(handle);
        up.Button.Button = 1;
        up.Button.State = 0;
        up.Button.Clicks = 1;
        up.Button.X = x;
        up.Button.Y = y;
        SdlProvider.SDL.Value.PushEvent(&up);
    }

    /// <summary>A double click on the caption, what the Dock setting says. Thread of the window.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private void ZoomWithSystem()
    {
        MacMain.Run(() => chrome?.CaptionDoubleClick());
    }

}
