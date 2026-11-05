using System.Numerics;

using Horizon.Core;
using Horizon.Logging;

using Silk.NET.Input;
using Silk.NET.Windowing;

namespace Horizon.Platform.Desktop;

/// <summary>
/// The desktop's window, Silk's GLFW one on Windows and SDL's on a Mac and Linux, and everything that may only be done to
/// it on the thread it was made on. The engine asks it through <see cref="IPlatformWindow"/> and hears from it through
/// <see cref="IPlatformEvents"/>, whoever needs something native (a self test posting keys to it) gets the handles here.
/// </summary>
public sealed partial class DesktopWindow : IPlatformWindow
{
    // What it's called in the log, the same as it always was
    private const string Name = "Window Manager";

    private readonly DesktopHost host;
    private readonly IWindow _window;
    private IInputContext? _input;

    // What the window was asked to be when it was made, kept as it was
    private readonly WindowOptions options;
    private readonly ApplicationBehaviour behaviour;
    private readonly bool readsGamepads;

    // What the window was last told to be, which on a Mac decides how big a fullscreen window is drawn, see RenderSize
    private DisplaySettings settings;

    // How big what is drawn into is, the window in the system's units, and the screen, as the window's thread last saw them
    private Vector2 viewport, points, screen;
    private Vector2 screenScale = Vector2.One, pixelScale = Vector2.One;

    // Whether the window's thread was asked to close the window, and what the title is to say
    private volatile bool closing;
    private volatile string title;
    private volatile bool titleChanged;

    /// <summary>The Silk window underneath, for whoever needs something native. Thread of the window.</summary>
    public IWindow Native => _window;

    /// <summary>The Silk input context of the window, there once it loaded. Thread of the window.</summary>
    public IInputContext? NativeInput => _input;

    /// <summary>The window's HWND on Windows, 0 anywhere else or before it is made.</summary>
    public nint Win32Handle => _window.Native?.Win32 is { Hwnd: not 0 } win32 ? win32.Hwnd : 0;

    internal DesktopWindow(DesktopHost host, PlatformHostRequest request)
    {
        this.host = host;
        WindowManagerConfiguration config = request.Window;
        behaviour = request.Behaviour;
        readsGamepads = request.ReadsGamepads;
        title = config.WindowTitle ?? string.Empty;
        windowIcon = config.WindowIcon;
        CustomFrame = config.CustomFrame;
        ScalesToDisplay = config.ScaleToDisplay;
        maximizeWhenMade = config.Maximized && config.CustomFrame && OperatingSystem.IsWindows() && !config.Fullscreen;
        settings = new DisplaySettings
        {
            Fullscreen = config.Fullscreen,
            WindowSize = config.WindowSize,
            VSync = config.VSync,
            FramesPerSecond = Math.Max(0.0, config.FramesPerSecond)
        };
        viewport = points = screen = config.WindowSize;

        // What the window is asked to be. Vulkan gets no say in how it is paced, see below
        options = WindowOptions.Default with
        {
            // Vulkan, so the window only has a surface to offer and the device does the rest
            API = GraphicsAPI.DefaultVulkan,
            Title = config.WindowTitle ?? string.Empty,
            // A frameless window on Windows is maximised once Windows has its frame, see SetUpFrame
            WindowState = config.Fullscreen ? WindowState.Fullscreen
                : config.Maximized && !(config.CustomFrame && OperatingSystem.IsWindows()) ? WindowState.Maximized
                : WindowState.Normal,
            Size = new Silk.NET.Maths.Vector2D<int>(
                (int)config.WindowSize.X,
                (int)config.WindowSize.Y
            ),
            // The updates are not the window's to pace, they have a loop of their own. Neither are the frames,
            // it would spend the wait for the next one spinning (see WindowManager.WaitForFrame)
            UpdatesPerSecond = 0,
            FramesPerSecond = 0,
            ShouldSwapAutomatically = false,
            VSync = config.VSync,
            Samples = 0,

            // Not shown until there is a picture in it, see Show. A window that is up before its first frame shows
            // whatever the system had lying around in it. A Mac gets it shown as it always was, nobody has ever made
            // SDL's window hidden there and the first run is not the time to find out
            IsVisible = OperatingSystem.IsMacOS(),

            // Without the system's frame the UI draws a title bar of its own (TitleBar), see DesktopWindow.Frame
            WindowBorder = config.CustomFrame ? WindowBorder.Hidden : WindowBorder.Resizable,
        };

        _window = Silk.NET.Windowing.Window.Create(options);
        SubscribeWindowEvents();
    }

    // What the window tells the engine and asks of the host, by the names the rest of the window always used
    private IPlatformEvents Events => host.Events;
    private void NoteInput() => Events.InputHeard();
    private void RequestFrames() => Events.RequestFrames();
    private void Wake() => Events.Wake();
    private void PostToWindow(Action work) => host.Post(work);
    private Vector2 ViewportSize => viewport;
    private WindowOptions WindowOptions => options;
    private static WindowEdges EdgesAt(Vector2 at, Vector2 size, float scale) => WindowManager.EdgesAt(at, size, scale);

    /// <summary>How many pixels of what is drawn there are to one of whatever the system counts the window and the pointer in.</summary>
    internal Vector2 PixelScale => pixelScale;

    /// <summary>Helper method to turn where the system says the pointer is into the pixel that's under it. Any thread.</summary>
    internal Vector2 PointerToPixels(Vector2 pointer) => pointer * pixelScale;

    /// <summary>What the window was like as it came up, once it loaded.</summary>
    internal PlatformState State { get; private set; } = null!;

    private void SubscribeWindowEvents()
    {
        this._window.Resize += WindowResize;

        // Moved onto another screen, which may be scaled differently
        this._window.Move += _ => UpdateDisplayScale();

        // The window's half of getting going, on its thread. The rest happens on the thread that draws
        this._window.Load += () =>
        {
            UpdateDisplayScale();
            SizeToDisplay();

            // A maximised window is where it belongs already, centering it would take it back out of that
            if (WindowOptions.WindowState != WindowState.Maximized)
                _window.Center();

            // SDL's window falls over its own default icon (it indexes past the end of the list it was handed), and a
            // Mac has no icon on a window anyway, the one in the Dock comes from the bundle
            if (!OperatingSystem.IsMacOS() && !TrySetIcon(windowIcon) && !UsesSdl)
                _window.SetDefaultIcon();

            // Silk's GLFW context goes through every controller there is as it is made, which a broken driver makes a minute
            _input = readsGamepads || UsesSdl ? _window.CreateInput() : new GlfwInputWithoutGamepads(_window);
            bool focused = ListenForInput();
            SetUpWaiting();

            UpdateViewport();

            // The window was made before anybody could know what a point is worth on this screen, so on a Retina one
            // it came out twice the size it was asked to be. Put right now that it is known, unless its size was in
            // points all along (ScaleToDisplay)
            if (WindowOptions.WindowState == WindowState.Normal && screenScale != Vector2.One && !ScalesToDisplay)
            {
                _window.Size = InPoints(new Vector2(WindowOptions.Size.X, WindowOptions.Size.Y));
                _window.Center();
                UpdateViewport();
            }

            UpdateScreenSize();
            focusedAtLoad = focused;
        };
    }

    /// <summary>Gets the native window made and set up, on the calling thread, which is the window's from here on.</summary>
    internal void Initialize()
    {
        _window.Initialize();
        SetUpFrame();

        // Only now, a frameless window on Windows is maximised by SetUpFrame and the engine has to start from that
        // size, not the one it loaded at
        State = new PlatformState(Display(), focusedAtLoad, _window.WindowState == WindowState.Minimized);
    }

    // Whether the window had the keyboard as it loaded, which is what the engine starts out believing
    private bool focusedAtLoad = true;

    /// <summary>
    /// Helper method to turn a size in pixels, which is what everybody asks for a window in, into whatever the system
    /// counts its windows in. The same thing everywhere but on a Mac with a Retina screen, where a point is two pixels.
    /// </summary>
    private Silk.NET.Maths.Vector2D<int> InPoints(Vector2 pixels) => new(
        Math.Max(1, (int)MathF.Round(pixels.X / screenScale.X)),
        Math.Max(1, (int)MathF.Round(pixels.Y / screenScale.Y)));

    /// <summary>Everything about how big things are, as the window's thread last saw it.</summary>
    private PlatformDisplay Display() => new(viewport, viewport, points, screen, pixelScale, displayScale);

    /// <summary>Helper method to tell the engine how big things are now. Thread of the window.</summary>
    private void Publish(bool resized = false)
    {
        if (host.HasEvents)
            Events.DisplayChanged(Display(), resized);
    }

    private void UpdateViewport()
    {
        var drawable = DrawableSize();
        viewport = RenderSize(drawable);

        // What the system counts the window and the pointer in against the pixels the screen really has, 2 on a
        // Retina one, and against what is drawn into, which is another number (and not the same one across as down)
        // while a fullscreen window is drawn small and stretched over the screen
        points = new Vector2(_window.Size.X, _window.Size.Y);
        bool known = points.X > 0 && points.Y > 0;
        screenScale = known ? drawable / points : Vector2.One;
        pixelScale = known ? viewport / points : Vector2.One;
        Publish();
    }

    /// <summary>
    /// Helper method to decide how big what is drawn into is, given how many pixels the window has. They are the same
    /// thing everywhere but in a fullscreen window on a Mac. A Retina screen is five million pixels, and going
    /// fullscreen (the green button or the game's own) would have a game that was set to 1280 by 720 draw all of
    /// them and crawl. So it stays the size it was set to, nothing has to be laid out again and whatever was in
    /// the middle still is, and the system stretches the picture over the whole of the screen (MoltenVK takes a
    /// swapchain of another size than its layer). No bars, bogz doesn't want them, so 16 by 9 on a 16 by 10 laptop
    /// comes out a tenth taller than it was drawn.
    /// </summary>
    private Vector2 RenderSize(Vector2 drawable)
    {
        if (!OperatingSystem.IsMacOS() || !IsFullscreen())
            return drawable;

        Vector2 set = settings.WindowSize;
        if (set.X < 1.0f || set.Y < 1.0f || set.X >= drawable.X || set.Y >= drawable.Y)
            return drawable;

        return new Vector2(MathF.Floor(set.X), MathF.Floor(set.Y));
    }

    /// <summary>
    /// Helper method to ask how many pixels there are to draw into. On a Mac the window is counted in points and SDL's
    /// window says those for its framebuffer too, while the Metal layer underneath (and so the swapchain) is in the
    /// pixels of the screen, twice as many each way on a Retina one. A swapchain that never comes out the size it was
    /// asked for is made anew every frame and nothing is ever shown, so SDL is asked how many pixels the window has.
    /// Not how big the layer's drawables are (SDL_Vulkan_GetDrawableSize), that is whatever MoltenVK set them to for
    /// the swapchain, which in fullscreen is the size the game was set to and no measure of the screen at all.
    /// </summary>
    private unsafe Vector2 DrawableSize()
    {
        // A Wayland screen scaled by two is the same as a Retina one
        if (SdlWindow is var handle && handle != null)
        {
            int width = 0, height = 0;
            Silk.NET.SDL.SdlProvider.SDL.Value.GetWindowSizeInPixels(handle, &width, &height);
            if (width > 0 && height > 0)
                return new Vector2(width, height);
        }

        return new Vector2(_window.FramebufferSize.X, _window.FramebufferSize.Y);
    }

    /// <summary>How many pixels the window's surface has right now, for the surface. Any thread, Silk keeps it.</summary>
    internal SurfaceSize FramebufferPixels => new((uint)Math.Max(0, _window.FramebufferSize.X), (uint)Math.Max(0, _window.FramebufferSize.Y));

    private void WindowResize(Silk.NET.Maths.Vector2D<int> size)
    {
        RequestFrames();

        // Minimised windows say they are nothing by nothing, which nobody can draw into, not even Jesus
        if (size.X <= 0 || size.Y <= 0) return;

        UpdateViewport();
        UpdateDisplayScale();

        // The engine hears the size changed, and has the surface made anew for it
        Publish(resized: true);
    }

    private void UpdateScreenSize()
    {
        if (_window.Monitor is not { } monitor)
            return;

        var size = monitor.VideoMode.Resolution ?? monitor.Bounds.Size;
        // In the same pixels as the viewport, a Mac counts its screen in points like it does its windows
        screen = new Vector2(size.X, size.Y) * screenScale;
        Publish();
    }

    /// <summary>
    /// Makes the window what it was asked to be, fullscreen or the size it was given. Thread of the window, the thread
    /// that draws waits for it so the frame finds it the way it was asked to be.
    /// </summary>
    public void ApplyDisplay(DisplaySettings settings)
    {
        this.settings = settings;

        bool fullscreen = IsFullscreen();
        if (settings.Fullscreen)
        {
            if (!fullscreen)
                SetFullscreen(true);
        }
        else
        {
            if (fullscreen)
                SetFullscreen(false);

            var size = InPoints(settings.WindowSize);
            if (_window.Size != size)
            {
                _window.Size = size;
                _window.Center();
            }
        }

        // The window says so itself when its size changes, but not always in time for the frame that is about to be drawn
        UpdateViewport();
        UpdateScreenSize();
    }

    // What SDL calls a window that is fullscreen at all, and one that is so by covering the desktop as it is
    private const uint SDL_FULLSCREEN = 0x1, SDL_FULLSCREEN_DESKTOP = 0x1001;

    /// <summary>
    /// Helper method to say whether the window is fullscreen. Thread of the window.
    /// </summary>
    private unsafe bool IsFullscreen()
    {
        if (SdlWindow is var handle && handle != null)
        {
            if ((Silk.NET.SDL.SdlProvider.SDL.Value.GetWindowFlags(handle) & SDL_FULLSCREEN) != 0)
                return true;

            // The green button takes a window into a fullscreen of the system's own that SDL was never asked for
            // and doesn't count, the window itself knows
            return OperatingSystem.IsMacOS() && _window.Native?.Cocoa is { } cocoa && cocoa != 0 && (MsgSend(cocoa, RegisterSelector("styleMask")) & NS_FULLSCREEN) != 0;
        }

        return _window.WindowState == WindowState.Fullscreen;
    }

    // NSWindowStyleMaskFullScreen
    private const ulong NS_FULLSCREEN = 1ul << 14;

    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nuint MsgSend(nint receiver, nint selector);

    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")]
    private static extern nint RegisterSelector([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPStr)] string name);

    /// <summary>
    /// Helper method to take the window into fullscreen or back out of it. Thread of the window. On a Mac SDL is asked
    /// itself, for the kind that covers the desktop as it is. What Silk asks for there is the kind that changes what
    /// the screen is set to, to whatever mode is nearest the window, which on a laptop came out as 2048 by 1280 of
    /// all things and left the window that size when it came back.
    /// </summary>
    private unsafe void SetFullscreen(bool fullscreen)
    {
        if (SdlWindow is var handle && handle != null)
        {
            Silk.NET.SDL.SdlProvider.SDL.Value.SetWindowFullscreen(handle, fullscreen ? SDL_FULLSCREEN_DESKTOP : 0);
            return;
        }

        _window.WindowState = fullscreen ? WindowState.Fullscreen : WindowState.Normal;

        // GLFW takes the styles of a frame back off a window coming out of fullscreen, which Windows needs to snap it
        if (!fullscreen)
            AddFrameStyles();
    }

    /// <summary>Shows the window, which is made hidden on all but a Mac. Thread of the window.</summary>
    public void Show()
    {
        if (!options.IsVisible)
            _window.IsVisible = true;
    }

    /// <summary>Changes what the title bar says, at the window's next look at the system. From any thread.</summary>
    public void SetTitle(string title)
    {
        this.title = title;
        titleChanged = true;
        host.Wake();
    }

    /// <summary>Asks the window to close, which the window's thread does the next time it looks. From any thread.</summary>
    internal void RequestClose()
    {
        closing = true;
        host.Wake();
    }

    /// <summary>
    /// Helper method to hear what the system has to say about the window until it closes, and do whatever else may
    /// only be done on its thread. Thread of the window.
    /// </summary>
    internal void RunUntilClosed()
    {
        while (!_window.IsClosing)
        {
            HandleEvents();

            // Asked to close (its X, alt and F4), whoever said they want a say gets it, and the window stays if they say no
            if (_window.IsClosing && !Events.MayClose())
                _window.IsClosing = false;

            // Woken up early for whatever the thread that draws needs done to the window. An application waits for the
            // system while nothing is happening instead, see DesktopWindow.Waiting
            WaitForEvents();
        }
    }

    /// <summary>
    /// Helper method to hear what the system has to say about the window, and do whatever else may only be done on its
    /// thread. Thread of the window, about a thousand times a second.
    /// </summary>
    private void HandleEvents()
    {
        _window.DoEvents();
        host.SampleGamepads();
        Events.EventsProcessed();

        host.DoWork();
        TrackFrame();
        StepSlide();

        // Asked once. Left set, a window whose program said not yet (something unsaved) was asked again a thousand times a
        // second, and the program put its question up again every tick, never to be answered
        if (closing)
        {
            closing = false;
            _window.Close();
        }

        if (titleChanged)
        {
            titleChanged = false;
            _window.Title = title;
        }
    }

    /// <summary>One last listen to the system, so the window gets to hear it is closed. Thread of the window.</summary>
    internal void Finish() => _window.DoEvents();

    /// <summary>Lets go of the input and the native window, last, once the GPU has let go of its surface. Thread of the window.</summary>
    internal void Dispose()
    {
        StopListening();
        ReleaseFrame();
        if (ReferenceEquals(hitTested, this))
            hitTested = null;
        _window.Reset();
        _window.Dispose();
        windowWake.Dispose();
    }
}
