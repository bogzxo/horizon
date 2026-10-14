using Horizon.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.ExceptionServices;

using Horizon.Core.Components;
using Horizon.Core.Diagnostics;
using Horizon.Core.Primitives;
using Horizon.Core.Threading;

using Silk.NET.Input;
using Silk.NET.Input.Glfw;
using Horizon.Graphics;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Glfw;

namespace Horizon.Core;

/// <summary>
/// How big the window and what is drawn into it are, all of it as of one moment. Replaced whole whenever any of it
/// changes, so whoever reads it from another thread never gets half of a resize.
/// </summary>
/// <param name="ViewportSize">How big what is drawn into is, in pixels.</param>
/// <param name="WindowSize">How big the window is, in pixels.</param>
/// <param name="ScreenSize">How big the screen the window is on is, in pixels.</param>
public sealed record DisplayState(Vector2 ViewportSize, Vector2 WindowSize, Vector2 ScreenSize)
{
    /// <summary>The width of what is drawn into over its height.</summary>
    public float AspectRatio => ViewportSize.Y > 0.0f ? ViewportSize.X / ViewportSize.Y : 1.0f;
}

/// <summary>
/// Engine component that manages all associated window activities and threads.
/// <para>
/// There are three of them. The thread the window was made on (the one <see cref="Run"/> is called on) hears what the
/// system has to say about the window and its input, about a thousand times a second whatever the frames are doing,
/// and is the only one that may change the window. Frames are drawn on a thread of their own, the only one that may
/// talk to the GPU. The game is simulated on a third (see <see cref="SimulationLoop"/>), its logic and its physics,
/// one tick after another, each at a rate of its own. So a frame that takes its time doesn't make the input late,
/// and a window that is being dragged about doesn't stop the drawing.
/// </para>
/// <para>
/// Neither waits for the other. At the end of every tick the simulation publishes a snapshot of everything that is
/// drawn (<see cref="Entity.Capture"/>, <see cref="SnapshotClock"/>), and frames are drawn from the last two of those,
/// at the moment between them the frame is due to show (<see cref="Presentation"/>). So whatever moves goes the same
/// distance for the same time from frame to frame, however many frames there are a second, and a frame that takes
/// its time doesn't hold the game up (or the other way round). The simulation only stands still for what has to
/// happen on this thread with nobody touching the game, which is setting up what was added to it (<see cref="EntityLifecycle"/>),
/// swapping scenes, and whatever else asks for it (<see cref="RequestExclusive"/>). It does that at the end of a tick,
/// and the next frame gets it done before it draws.
/// </para>
/// </summary>
public class WindowManager : GameComponent, IDisposable
{
    private readonly IWindow _window;
    private IInputContext _input;

    private SimulationLoop? simulation;
    private readonly double updatesPerSecond, physicsUpdatesPerSecond;

    // What the simulation publishes at the end of every tick and frames are drawn from
    private readonly SnapshotClock snapshots = new();

    // The simulation stands still at the end of a tick for whatever has to happen on this thread. It says
    // so with the first, the frame that does the work lets it carry on with the second
    private readonly SemaphoreSlim simulationParked = new(0, 1), simulationResumed = new(0, 1);
    private volatile bool exclusiveRequested;

    // The longest (in milliseconds) the simulation waits for a frame to come and do what it stood still for. That
    // happens at the start of the next frame, so this only runs out when no frames are drawn
    private const int LONGEST_SET_UP_WAIT = 250;

    private readonly LoopStatistics renderStatistics = new("Render", 0.0);
    private long lastFrame;

    // The thread frames are drawn on, told to stop once the window closes, and what it died of if it did
    private Thread? renderThread;
    private volatile bool renderStopping;
    private ExceptionDispatchInfo? renderFailure;

    // What the thread that draws needs done to the window, which only the thread of the window may do. It is woken
    // up for it rather than left to get round to it
    private readonly ConcurrentQueue<Action> windowWork = new();
    private readonly AutoResetEvent windowWake = new(false);

    // How long (in milliseconds) the thread of the window waits between two looks at what the system has to say
    private const int EVENT_POLL = 1;

    // Whether the size of what is drawn into changed since the last frame, which is told about at the start of the next
    private volatile bool resized;

    // How long (in seconds) the exclusive work of the last frame took, which the next frame doesn't count as time that went by
    private float stalled;

    // Set this in the environment to a number of seconds and the loops are written to the log that often.
    // For finding out what a game costs without building anything into it
    private const string LOG_LOOPS_VARIABLE = "HORIZON_LOG_LOOPS";

    private readonly double logLoopsEvery;
    private double nextLoopLog;

    // Set this in the environment to a number of seconds and what every thread allocates is written to the log that
    // often, by type. For finding out where the garbage comes from without a profiler
    private const string LOG_ALLOCATIONS_VARIABLE = "HORIZON_LOG_ALLOCATIONS";

    private readonly AllocationLog? allocations;
    private readonly double logAllocationsEvery;
    private double nextAllocationLog;

    // How long (in seconds) there is between two frames at the least, 0 for as many as there is time for. And when the next one is due
    private double framePeriod, nextFrame;

    // What was asked of the window from another thread and is still to be done. Only the thread the window was made on may touch it
    private readonly Lock displayLock = new();
    private DisplaySettings? pendingDisplay;

    private volatile DisplayState display;

    /// <summary>
    /// How drawing and each of the loops of the engine are doing, how often they come round, how long their turns
    /// take and how unevenly they come. The simulation's are there once it has been started, which is after the first frame.
    /// </summary>
    public IReadOnlyList<LoopStatistics> Loops { get; private set; }

    public bool IsRunning { get; private set; }

    /// <summary>
    /// How frames show the simulation, see <see cref="PresentationMode"/>. From any thread, it takes hold at the next frame.
    /// </summary>
    public PresentationMode Presentation { get; set; }

    /// <summary>
    /// What the simulation publishes and frames are drawn from. Whoever needs to publish something outside of a tick
    /// (setting a scene up) or look at how the ticks are coming can get at it here.
    /// </summary>
    public SnapshotClock Snapshots => snapshots;

    /// <summary>How many ticks the simulation has made.</summary>
    public long Tick => simulation?.Tick ?? 0;

    /// <summary>How long (in seconds) the game has been simulated for.</summary>
    public double SimulatedTime => simulation?.Time ?? 0.0;

    /// <summary>How big the window and what is drawn into it are, all of it as of one moment. From any thread.</summary>
    public DisplayState DisplayState => display;

    /// <summary>
    /// How wide what is drawn into is for how high, the width over the height.
    /// </summary>
    public float AspectRatio => display.AspectRatio;

    /// <summary>
    /// How big what is drawn into is, in pixels.
    /// </summary>
    public Vector2 ViewportSize => display.ViewportSize;

    /// <summary>
    /// How big the window is, in pixels.
    /// </summary>
    public Vector2 WindowSize => display.WindowSize;

    /// <summary>
    /// How many pixels of what is drawn into there are to one of whatever the system counts the window and the pointer
    /// in. One everywhere but on a Mac, where it is two on a Retina screen and whatever it comes to in fullscreen. From any thread.
    /// </summary>
    public Vector2 PixelScale { get; private set; } = Vector2.One;

    /// <summary>
    /// The size of the screen the window is on, in pixels. A window can't usefully be any bigger than this.
    /// </summary>
    public Vector2 ScreenSize => display.ScreenSize;

    /// <summary>
    /// How the window is shown and how often it is drawn, as it was last set with <see cref="Apply"/> (or by the configuration, before anybody did).
    /// </summary>
    public DisplaySettings Display { get; private set; }

    /// <summary>
    /// The GPU, made on the thread that draws before its first frame. Null until then.
    /// </summary>
    public GraphicsDevice? Graphics { get; private set; }

    /// <summary>
    /// The native window underneath, for whoever needs something this class doesn't offer.
    /// </summary>
    public IWindow Window => _window;

    /// <summary>
    /// Asked when the window is about to close because somebody asked it to (its X, alt and F4), false keeps it open. For
    /// a program with something unsaved that wants to ask first, which it does on its own thread and closes the window
    /// itself once it is happy, answering true then. Called on the thread of the window, so it has to be quick and
    /// mustn't touch anything that isn't safe to, a flag the program keeps up is the usual. Null, it always closes.
    /// </summary>
    public Func<bool>? CloseRequested { get; set; }

    private bool MayClose()
    {
        try
        {
            return CloseRequested?.Invoke() ?? true;
        }
        catch (Exception)
        {
            // Whatever went wrong asking, a window that won't close is worse
            return true;
        }
    }

    /// <summary>
    /// The native input context of the window. Games read their keys, mouse and gamepads through the input manager of the engine instead.
    /// </summary>
    public IInputContext Input => _input;

    /// <summary>
    /// What the title bar of the window says. From any thread, it changes at the start of the next frame.
    /// </summary>
    public string Title
    {
        get => title;
        set
        {
            title = value;
            titleChanged = true;
        }
    }

    private volatile string title;
    private volatile bool titleChanged;

    /// <summary>
    /// Raised on the thread that draws, at the start of the first frame after the size of what is drawn into changed,
    /// with the new size in pixels.
    /// </summary>
    public event Action<Vector2>? Resized;

    /// <summary>
    /// Raised on the thread of the window every time it has heard what the system had to say (keys, the mouse,
    /// gamepads coming and going), about a thousand times a second. The moment for whoever samples input to look at
    /// the devices, and for whatever else may only be done on this thread (the clipboard).
    /// </summary>
    public event Action? EventsProcessed;

    /// <summary>
    /// Raised on the thread that draws, at the start of a frame, with the simulation standing still. The place for
    /// whatever has to be done on that thread without anybody touching the game (swapping scenes). Whoever has
    /// something of the kind says so with <see cref="RequestExclusive"/> and checks whether it is still there to be done.
    /// </summary>
    public event Action<float>? Exclusive;

    /// <summary>
    /// Asks for <see cref="Exclusive"/> to be raised as soon as it can be, which is at the start of the next frame, after the
    /// simulation has finished the tick it is in. From any thread.
    /// </summary>
    public void RequestExclusive() => exclusiveRequested = true;

    /// <summary>
    /// Asks the window to close, which ends <see cref="Run"/> once the frame that is being drawn is done. From any thread.
    /// </summary>
    public void Close() => closing = true;

    private volatile bool closing;

    // what the window was asked to be when it was made, kept as it was
    public readonly WindowOptions WindowOptions;

    public WindowManager(in WindowManagerConfiguration config)
    {
        // GLFW has no Vulkan to offer on a Mac (cheers, Apple), SDL has (through a Metal surface) and can be told where MoltenVK is
        if (OperatingSystem.IsMacOS())
        {
            LoadMoltenVK();
            Silk.NET.Windowing.Sdl.SdlWindowing.RegisterPlatform();
            Silk.NET.Input.Sdl.SdlInput.RegisterPlatform();
        }
        else
        {
            GlfwWindowing.RegisterPlatform();
            GlfwInput.RegisterPlatform();
        }

        Name = "Window Manager";

        if (double.TryParse(Environment.GetEnvironmentVariable(LOG_LOOPS_VARIABLE), System.Globalization.CultureInfo.InvariantCulture, out double every) && every > 0.0)
            logLoopsEvery = every;
        if (double.TryParse(Environment.GetEnvironmentVariable(LOG_ALLOCATIONS_VARIABLE), System.Globalization.CultureInfo.InvariantCulture, out double allocationsEvery) && allocationsEvery > 0.0)
        {
            // A trimmed game has no events to count them by, see EventSourceSupport
            if (AppContext.TryGetSwitch("System.Diagnostics.Tracing.EventSource.IsSupported", out bool events) && !events)
            {
                Log.Warning($"[{Name}] Allocations can't be counted in this build, its event sources are trimmed out. Build it with <EventSourceSupport>true</EventSourceSupport> to.");
            }
            else
            {
                logAllocationsEvery = allocationsEvery;
                allocations = new AllocationLog();
            }
        }
        title = config.WindowTitle ?? string.Empty;

        updatesPerSecond = config.UpdatesPerSecond > 0.0 ? config.UpdatesPerSecond : 120.0;
        physicsUpdatesPerSecond = config.PhysicsUpdatesPerSecond > 0.0 ? config.PhysicsUpdatesPerSecond : 120.0;
        Loops = [renderStatistics];

        Presentation = config.Presentation;

        // What the window is asked to be. Vulkan gets no say in how it is paced, see below
        WindowOptions = WindowOptions.Default with
        {
            // Vulkan, so the window only has a surface to offer and the device does the rest
            API = GraphicsAPI.DefaultVulkan,
            Title = config.WindowTitle ?? string.Empty,
            WindowState = config.Fullscreen ? WindowState.Fullscreen : config.Maximized ? WindowState.Maximized : WindowState.Normal,
            Size = new Silk.NET.Maths.Vector2D<int>(
                (int)config.WindowSize.X,
                (int)config.WindowSize.Y
            ),
            // The updates are not the window's to pace, they have a loop of their own. Neither are the frames,
            // it would spend the wait for the next one spinning (see WaitForFrame)
            UpdatesPerSecond = 0,
            FramesPerSecond = 0,
            ShouldSwapAutomatically = false,
            VSync = config.VSync,
            Samples = 0,

            // Not shown until there is a picture in it, see SetUpDrawing. A window that is up before its first frame
            // shows whatever the system had lying around in it. A Mac gets it shown as it always was, nobody has
            // ever made SDL's window hidden there and the first run is not the time to find out
            IsVisible = OperatingSystem.IsMacOS(),
        };

        display = new DisplayState(config.WindowSize, config.WindowSize, config.WindowSize);

        Display = new DisplaySettings
        {
            Fullscreen = config.Fullscreen,
            WindowSize = config.WindowSize,
            VSync = config.VSync,
            FramesPerSecond = Math.Max(0.0, config.FramesPerSecond)
        };
        framePeriod = PeriodOf(Display.FramesPerSecond);

        this._window = Silk.NET.Windowing.Window.Create(WindowOptions);
        SubscribeWindowEvents();
    }

    /// <summary>
    /// Helper method to point SDL at MoltenVK, which the engine brings with it (Silk.NET.MoltenVK.Native, next to
    /// the exe), on a Mac. SDL loads its Vulkan by the SDL_VULKAN_LIBRARY hint when there is one, which is set here
    /// to the full path so it needn't be anywhere dyld looks on its own, and the Vulkan API loader of Silk.NET
    /// finds the same dylib by name once it is loaded. With the Vulkan SDK installed and no dylib next to the exe
    /// SDL finds the SDK's loader by itself and MoltenVK comes through that instead.
    /// </summary>
    private static void LoadMoltenVK()
    {
        if (!OperatingSystem.IsMacOS()) return;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SDL_VULKAN_LIBRARY"))) return;

        foreach (string candidate in new[] { Path.Combine(AppContext.BaseDirectory, "libMoltenVK.dylib"), Path.Combine(AppContext.BaseDirectory, "runtimes", "osx", "native", "libMoltenVK.dylib") })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                System.Runtime.InteropServices.NativeLibrary.Load(candidate);
                Environment.SetEnvironmentVariable("SDL_VULKAN_LIBRARY", candidate);
                Log.Info($"[Window Manager] MoltenVK is {candidate}.");
                return;
            }
            catch (Exception e)
            {
                Log.Warning($"[Window Manager] MoltenVK at {candidate} would not load, {e.Message}");
            }
        }
    }

    private void SubscribeWindowEvents()
    {
        this._window.Resize += WindowResize;

        // The window's half of getting going, on its thread. The rest happens on the thread that draws, see SetUpDrawing
        this._window.Load += () =>
        {
            // A maximised window is where it belongs already, centering it would take it back out of that
            if (WindowOptions.WindowState != WindowState.Maximized)
                _window.Center();

            // SDL's window falls over its own default icon (it indexes past the end of the list it was handed), and a
            // Mac has no icon on a window anyway, the one in the Dock comes from the bundle
            if (!OperatingSystem.IsMacOS())
                _window.SetDefaultIcon();

            _input = _window.CreateInput();

            UpdateViewport();

            // The window was made before anybody could know what a point is worth on this screen, so on a Retina one
            // it came out twice the size it was asked to be. Put right now that it is known
            if (WindowOptions.WindowState == WindowState.Normal && screenScale != Vector2.One)
            {
                _window.Size = InPoints(new Vector2(WindowOptions.Size.X, WindowOptions.Size.Y));
                _window.Center();
                UpdateViewport();
            }

            UpdateScreenSize();
        };
    }

    /// <summary>
    /// Helper method to turn a size in pixels, which is what everybody asks for a window in, into whatever the system
    /// counts its windows in. The same thing everywhere but on a Mac with a Retina screen, where a point is two pixels.
    /// </summary>
    private Silk.NET.Maths.Vector2D<int> InPoints(Vector2 pixels) => new(
        Math.Max(1, (int)MathF.Round(pixels.X / screenScale.X)),
        Math.Max(1, (int)MathF.Round(pixels.Y / screenScale.Y)));

    /// <summary>
    /// Helper method to get everything that draws going, on the thread that draws, before its first frame.
    /// </summary>
    private void SetUpDrawing()
    {
        EntityLifecycle.ClaimRenderThread();
        SnapshotClock.Active = snapshots;

        // The device belongs to this thread from here on, it is the one that records and submits every frame
        Silk.NET.Core.Contexts.IVkSurface surface = _window.VkSurface ?? throw new InvalidOperationException("The window has no Vulkan surface, is the Vulkan loader installed?");

        // On a Mac making the surface is SDL putting a Metal view into the window, and a view may only be put into a
        // window by the main thread. Done from this one nothing complains and every frame is drawn and presented,
        // into a layer the system never got told about, so the window stays blank
        if (OperatingSystem.IsMacOS())
            surface = new WindowThreadSurface(surface, OnWindowThread);

        Graphics = new GraphicsDevice(
            surface,
            (uint)ViewportSize.X,
            (uint)ViewportSize.Y,
            Display.VSync);

        // The first thing in the window, before anything of the engine or the game is set up (the engine's loading
        // screen), and only then is there a window to see
        DeviceReady?.Invoke();
        if (!WindowOptions.IsVisible)
            OnWindowThread(ShowWindow);

        Parent.Initialize();

        // Everything that was added to the engine before the window opened is set up now, before the first frame,
        // so whatever listens for that frame's exclusive work (the scene manager) is listening by then
        Parent.InitializeAll();
    }

    /// <summary>
    /// Raised on the thread that draws once there is a device to draw with and before anything else is set up, for
    /// whoever has something to put in the window first.
    /// </summary>
    internal Action? DeviceReady;

    /// <summary>Helper method to show the window, which is made hidden. Thread of the window.</summary>
    private void ShowWindow() => _window.IsVisible = true;

    /// <summary>
    /// Helper method to draw a frame from what the simulation published, after whatever it stood still for.
    /// </summary>
    private void DrawFrame(float dt)
    {
        // The time the last frame spent on exclusive work (setting a scene up can take a good while) is time the game
        // stood still. It isn't time for what moves by the frames to move on by, or a transition would jump the moment
        // the scene it was covering up is there
        dt = Math.Max(0.0f, dt - stalled);
        stalled = 0.0f;

        if (resized)
        {
            resized = false;
            Resized?.Invoke(ViewportSize);
        }

        // Whatever the simulation stopped for at the end of its last tick, done before anything is drawn
        if (simulationParked.Wait(0))
        {
            try
            {
                DoExclusiveWork(dt, always: true);
            }
            finally
            {
                simulationResumed.Release();
            }
        }
        else
        {
            // Nothing is simulated before the first frame, so there is nobody to wait for. After that the simulation
            // stands still for whatever touches the game, and only what doesn't is done here
            DoExclusiveWork(dt, always: simulation is null);
        }

        DrawFrom(snapshots.Acquire(Presentation), dt);
    }

    /// <summary>
    /// Helper method to draw the game from a frame's snapshots, and let go of them afterwards.
    /// </summary>
    private void DrawFrom(in RenderFrame frame, float dt)
    {
        RenderFrame.Begin(frame);
        try
        {
            Parent.Render(dt);
        }
        finally
        {
            RenderFrame.End();
            snapshots.Release();
        }
    }

    /// <summary>
    /// Helper method to do whatever has to happen on this thread with nobody touching the game, at the start of a frame.
    /// </summary>
    /// <param name="always">Whether the simulation is standing still for it. If not, only what doesn't touch the game is done.</param>
    private void DoExclusiveWork(float dt, bool always)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            DoExclusiveWorkTimed(dt, always);
        }
        finally
        {
            stalled += (float)((Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency);
        }
    }

    private void DoExclusiveWorkTimed(float dt, bool always)
    {
        // First, so whatever is set up or drawn from here on finds the window the size it was asked to be
        ApplyPendingDisplay();

        if (!always)
            return;

        // Whatever the updates added since the last frame is set up before anything is drawn (and what they
        // destroyed is freed)
        if (EntityLifecycle.HasWork)
            EntityLifecycle.Flush();

        exclusiveRequested = false;
        Exclusive?.Invoke(dt);
    }

    private void UpdateViewport()
    {
        var drawable = DrawableSize();
        var size = RenderSize(drawable);
        display = display with { ViewportSize = size, WindowSize = size };

        // What the system counts the window and the pointer in against the pixels the screen really has, 2 on a
        // Retina one, and against what is drawn into, which is another number (and not the same one across as down)
        // while a fullscreen window is drawn small and stretched over the screen
        var points = new Vector2(_window.Size.X, _window.Size.Y);
        bool known = points.X > 0 && points.Y > 0;
        screenScale = known ? drawable / points : Vector2.One;
        PixelScale = known ? size / points : Vector2.One;
    }

    private Vector2 screenScale = Vector2.One;

    /// <summary>
    /// Helper method to turn where the system says the pointer is in the window into the pixel of what is drawn that
    /// is under it. From any thread.
    /// </summary>
    internal Vector2 PointerToPixels(Vector2 pointer) => pointer * PixelScale;

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

        Vector2 set = Display.WindowSize;
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
        if (OperatingSystem.IsMacOS() && _window.Native?.Sdl is { } handle && handle != 0)
        {
            int width = 0, height = 0;
            Silk.NET.SDL.SdlProvider.SDL.Value.GetWindowSizeInPixels((Silk.NET.SDL.Window*)handle, &width, &height);
            if (width > 0 && height > 0)
                return new Vector2(width, height);
        }

        return new Vector2(_window.FramebufferSize.X, _window.FramebufferSize.Y);
    }

    private void WindowResize(Silk.NET.Maths.Vector2D<int> size)
    {
        // Minimised windows say they are nothing by nothing, which nobody can draw into, not even Jesus
        if (size.X <= 0 || size.Y <= 0) return;

        UpdateViewport();
        resized = true;

        // SDL sets the layer's drawables to the size of the window whenever that changes, and a swapchain that stays
        // the size it was (a window going fullscreen on a Mac, see RenderSize) is then shown small in a corner of
        // them until it is made anew
        Graphics?.SurfaceChanged();
    }

    private void UpdateScreenSize()
    {
        if (_window.Monitor is not { } monitor)
            return;

        var size = monitor.VideoMode.Resolution ?? monitor.Bounds.Size;
        // In the same pixels as the viewport, a Mac counts its screen in points like it does its windows
        display = display with { ScreenSize = new Vector2(size.X, size.Y) * screenScale };
    }

    /// <summary>
    /// Changes how the window is shown and how often it is drawn. From any thread.
    /// <para>
    /// It takes hold at the start of the next frame, before anything of that frame is set up or drawn. So a scene that is set in the same
    /// update as this is called is made for the window as it is going to be, which is the way to do it. Nothing that was made for the
    /// old size (a renderer, a camera) is resized, a scene that is to fit the new one has to be made again.
    /// </para>
    /// </summary>
    public void Apply(in DisplaySettings settings)
    {
        lock (displayLock)
        {
            Display = settings;
            pendingDisplay = settings;
        }
    }

    /// <summary>
    /// Helper method to do what <see cref="Apply"/> was asked for. On the thread that draws, at the start of a frame.
    /// How often frames are drawn and swapped is up to that thread, the window itself is changed on its own thread
    /// while this one waits, so the frame finds it the way it was asked to be.
    /// </summary>
    private void ApplyPendingDisplay()
    {
        DisplaySettings settings;
        lock (displayLock)
        {
            if (pendingDisplay is not { } pending)
                return;

            settings = pending;
            pendingDisplay = null;
        }

        ApplyDisplay(settings);
    }

    /// <summary>
    /// Helper method to make the display what somebody asked it to be. Apart from <see cref="ApplyPendingDisplay"/>
    /// on purpose, and it has to stay apart. There is a lambda in here that needs the settings, and what a lambda
    /// needs is put on the heap as the method it is written in starts, not when it gets to the lambda. Written in
    /// there this was 48 bytes of garbage every single frame, to find out that nothing was pending and leave again.
    /// The whole engine made nothing else on the render thread, the bastard.
    /// </summary>
    private void ApplyDisplay(DisplaySettings settings)
    {
        framePeriod = PeriodOf(settings.FramesPerSecond);
        Graphics?.SetVSync(settings.VSync);

        OnWindowThread(() => ApplyWindow(settings));

        Log.Info($"[{Name}] The window is {(settings.Fullscreen ? "fullscreen" : "windowed")} at {ViewportSize.X} by {ViewportSize.Y} now, {(settings.VSync ? "with" : "without")} vsync and {(settings.FramesPerSecond > 0.0 ? $"at most {settings.FramesPerSecond} frames a second" : "no limit on its frames")}.");
    }

    /// <summary>
    /// Helper method to make the window what it was asked to be, fullscreen or the size it was given. Thread of the window.
    /// </summary>
    private void ApplyWindow(DisplaySettings settings)
    {
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
        if (OperatingSystem.IsMacOS() && _window.Native?.Sdl is { } handle && handle != 0)
        {
            if ((Silk.NET.SDL.SdlProvider.SDL.Value.GetWindowFlags((Silk.NET.SDL.Window*)handle) & SDL_FULLSCREEN) != 0)
                return true;

            // The green button takes a window into a fullscreen of the system's own that SDL was never asked for
            // and doesn't count, the window itself knows
            return _window.Native?.Cocoa is { } cocoa && cocoa != 0 && (MsgSend(cocoa, RegisterSelector("styleMask")) & NS_FULLSCREEN) != 0;
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
        if (OperatingSystem.IsMacOS() && _window.Native?.Sdl is { } handle && handle != 0)
        {
            Silk.NET.SDL.SdlProvider.SDL.Value.SetWindowFullscreen((Silk.NET.SDL.Window*)handle, fullscreen ? SDL_FULLSCREEN_DESKTOP : 0);
            return;
        }

        _window.WindowState = fullscreen ? WindowState.Fullscreen : WindowState.Normal;
    }

    /// <summary>
    /// Helper method to have something done to the window on its thread, the only one that may, and wait for it to be
    /// done. From the thread that draws. Done right there on the thread of the window itself.
    /// </summary>
    private void OnWindowThread(Action work)
    {
        if (renderThread is null || Thread.CurrentThread != renderThread)
        {
            work();
            return;
        }

        using var done = new ManualResetEventSlim(false);
        Exception? failed = null;

        windowWork.Enqueue(() =>
        {
            try
            {
                work();
            }
            catch (Exception e)
            {
                failed = e;
            }
            finally
            {
                done.Set();
            }
        });
        windowWake.Set();

        done.Wait();
        if (failed is not null)
            ExceptionDispatchInfo.Throw(failed);
    }

    /// <summary>
    /// Has something done to the window on its thread the next time it looks, without waiting for it. From any thread,
    /// for whatever GLFW only lets the thread of the window touch (the cursor, say).
    /// </summary>
    internal void PostToWindow(Action work)
    {
        windowWork.Enqueue(work);
        windowWake.Set();
    }

    /// <summary>
    /// Helper method to do whatever the thread that draws asked to have done to the window. Thread of the window.
    /// </summary>
    private void DoWindowWork()
    {
        while (windowWork.TryDequeue(out Action? work))
            work();
    }

    private static double PeriodOf(double framesPerSecond) => framesPerSecond > 0.0 ? 1.0 / framesPerSecond : 0.0;

    /// <summary>
    /// Helper method to hold a frame back until it is due, for a window that has a limit on how many it draws.
    /// Asleep for most of the wait and awake for the end of it, the way the loops do it.
    /// </summary>
    private void WaitForFrame()
    {
        double period = framePeriod;
        if (period <= 0.0)
            return;

        // Fallen behind (or only just given a limit), in which case it counts from now rather than trying to catch up
        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        if (nextFrame < now - period)
            nextFrame = now;

        LoopTiming.WaitUntil(nextFrame);
        nextFrame += period;
    }

    public override void Initialize()
    {
        Log.Info($"[{Name}] Created window({WindowOptions.Size})!");
    }

    public void Run()
    {
        if (IsRunning)
            throw new Exception("Window is already running!");

        IsRunning = true;

        // The window, and then everything that draws happens on a thread of its own, see DrawFrames
        _window.Initialize();

        renderThread = new Thread(DrawFrames)
        {
            Name = "Render",
            Priority = ThreadPriority.AboveNormal,
            IsBackground = true
        };
        renderThread.Start();

        AllocationLog.NameThisThread("Window");
        LoopTiming.SharpenTimer(true);
        try
        {
            while (!_window.IsClosing)
            {
                HandleEvents();

                // Asked to close (its X, alt and F4), whoever said they want a say gets it, and the window stays if they say no
                if (_window.IsClosing && !MayClose())
                    _window.IsClosing = false;

                // Woken up early for whatever the thread that draws needs done to the window
                windowWake.WaitOne(EVENT_POLL);
            }
        }
        finally
        {
            LoopTiming.SharpenTimer(false);
        }

        // The last frame is finished, and whatever it may still need done to the window with it
        renderStopping = true;
        while (!renderThread.Join(EVENT_POLL))
            DoWindowWork();

        // Nothing is simulated once there is no window left to show it in
        simulation?.Stop();

        // Whatever is let go of from here on is let go of here, GPU and all
        EntityLifecycle.ClaimRenderThread();

        // One last listen to the system, so the window gets to hear it is closed
        _window.DoEvents();

        renderFailure?.Throw();
    }

    /// <summary>
    /// Helper method to hear what the system has to say about the window, and do whatever else may only be done on its
    /// thread. Thread of the window, about a thousand times a second.
    /// </summary>
    private void HandleEvents()
    {
        _window.DoEvents();
        EventsProcessed?.Invoke();

        DoWindowWork();

        if (closing) _window.Close();

        if (titleChanged)
        {
            titleChanged = false;
            _window.Title = title;
        }
    }

    /// <summary>
    /// Helper method to draw frame after frame for as long as the window is open. The thread that draws.
    /// </summary>
    private void DrawFrames()
    {
        try
        {
            AllocationLog.NameThisThread("Render");
            SetUpDrawing();

            long previous = 0;
            while (!renderStopping)
            {
                renderStatistics.SetState(LoopStatistics.LoopState.Sleeping);
                WaitForFrame();
                renderStatistics.SetState(LoopStatistics.LoopState.Working);

                long started = Stopwatch.GetTimestamp();
                long allocated = GC.GetAllocatedBytesForCurrentThread();

                float dt = previous == 0 ? 0.0f : (float)((started - previous) / (double)Stopwatch.Frequency);
                previous = started;

                Graphics!.BeginFrame((uint)ViewportSize.X, (uint)ViewportSize.Y);
                DrawFrame(dt);
                Graphics.EndFrame();

                long ended = Stopwatch.GetTimestamp();

                // With the wait for the screen in it, which is part of what a frame takes when frames are kept in step with it
                if (lastFrame != 0)
                {
                    renderStatistics.Record(
                        (ended - started) / (double)Stopwatch.Frequency,
                        0.0,
                        (started - lastFrame) / (double)Stopwatch.Frequency,
                        1,
                        GC.GetAllocatedBytesForCurrentThread() - allocated);
                }
                lastFrame = started;

                if (logLoopsEvery > 0.0) LogLoops(ended / (double)Stopwatch.Frequency);
                if (allocations is not null) LogAllocations(ended / (double)Stopwatch.Frequency);

                // The simulation only starts once a frame has been drawn. Everything is set up on this thread, and there has to be something to update
                if (simulation is null) StartSimulation();
            }
        }
        catch (Exception e)
        {
            // Thrown again on the thread that runs the window, which is where whoever ran it is waiting
            renderFailure = ExceptionDispatchInfo.Capture(e);
            closing = true;
            windowWake.Set();
        }
        finally
        {
            renderStatistics.SetState(LoopStatistics.LoopState.Stopped);

            // Nothing is on its way to the GPU once the frames stop, whatever gets freed afterwards is free to go
            Graphics?.WaitIdle();
        }
    }

    /// <summary>
    /// Helper method to start simulating the game, on a thread of its own.
    /// </summary>
    private void StartSimulation()
    {
        simulation = new SimulationLoop("Simulation", updatesPerSecond, physicsUpdatesPerSecond, new Host(this));

        Loops = [renderStatistics, simulation.Ticks, simulation.Logic, simulation.Physics];

        // The clock is told how far apart the ticks come rather than left to find out, see SnapshotClock.NominalInterval
        snapshots.NominalInterval = 1.0 / simulation.TickRate;

        Log.Info($"[{Name}] Simulating at {simulation.TickRate:0} ticks a second (logic at {simulation.LogicRate:0}, physics at {simulation.PhysicsRate:0}), frames {(Presentation == PresentationMode.Interpolated ? "interpolated between ticks" : "showing the newest tick")}.");
        simulation.Start();
    }

    /// <summary>
    /// A window's Vulkan surface that is made on the thread of the window whoever asks for it, see <see cref="SetUpDrawing"/>.
    /// </summary>
    private sealed unsafe class WindowThreadSurface(Silk.NET.Core.Contexts.IVkSurface inner, Action<Action> onWindowThread) : Silk.NET.Core.Contexts.IVkSurface
    {
        public Silk.NET.Core.Native.VkNonDispatchableHandle Create<T>(Silk.NET.Core.Native.VkHandle instance, T* allocator) where T : unmanaged
        {
            // A pointer can't go into a lambda, an address can
            nint address = (nint)allocator;
            Silk.NET.Core.Native.VkNonDispatchableHandle made = default;
            onWindowThread(() => made = inner.Create(instance, (T*)address));
            return made;
        }

        public byte** GetRequiredExtensions(out uint count) => inner.GetRequiredExtensions(out count);
    }

    /// <summary>
    /// What the simulation loop runs, the engine's updates and the publishing of a snapshot at the end of every tick.
    /// </summary>
    private sealed class Host(WindowManager window) : ISimulationHost
    {
        public void BeginTick() { }

        public void UpdateState(float dt) => window.Parent.UpdateState(dt);

        public void UpdatePhysics(float dt) => window.Parent.UpdatePhysics(dt);

        public void EndTick(long tick, long stamp, double time)
        {
            window.snapshots.BeginCapture();
            try
            {
                window.Parent.Capture();
            }
            finally
            {
                window.snapshots.EndCapture(stamp, time);
            }

            // Whatever has to be done on the thread of the window without anybody touching the game, done before the next tick
            if (window.exclusiveRequested || EntityLifecycle.HasWork)
            {
                long parked = Stopwatch.GetTimestamp();
                window.Rendezvous();

                // Stood still for longer than a tick (a scene was set up), so it carries on from now rather than racing
                // through the ticks that were missed, which would be published all at once and late, and have the
                // frames drawn further in the past for a while just as the new scene is uncovered
                if (window.simulation is { } loop && Stopwatch.GetTimestamp() - parked > Stopwatch.Frequency / loop.TickRate)
                    loop.Resynchronize();
            }
        }
    }

    /// <summary>
    /// Helper method to have the simulation stand still until the next frame has done what had to be done with nobody
    /// touching the game. Simulation thread, at the end of a tick.
    /// </summary>
    private void Rendezvous()
    {
        simulationParked.Release();

        if (simulationResumed.Wait(LONGEST_SET_UP_WAIT))
            return;

        // Nobody came (no frames are being drawn, lovely). Take it back, unless a frame took it just now, then wait for it to be done
        if (simulationParked.Wait(0))
            return;

        simulationResumed.Wait();
    }

    /// <summary>
    /// Helper method to write what every thread allocates to the log, every so often. Only if somebody asked for it, see <see cref="LOG_ALLOCATIONS_VARIABLE"/>.
    /// </summary>
    private void LogAllocations(double now)
    {
        if (now < nextAllocationLog) return;

        // The first look is only where the counting starts from, setting the game up is no fair measure
        bool first = nextAllocationLog == 0.0;
        nextAllocationLog = now + logAllocationsEvery;

        string report = allocations!.Report();
        if (!first) Log.Info($"[{Name}] {report}");
    }

    /// <summary>
    /// Helper method to write how every loop is doing to the log, every so often. Only if somebody asked for it, see <see cref="LOG_LOOPS_VARIABLE"/>.
    /// </summary>
    private void LogLoops(double now)
    {
        if (now < nextLoopLog) return;

        nextLoopLog = now + logLoopsEvery;

        foreach (LoopStatistics loop in Loops)
        {
            Log.Info(
                $"[{Name}] {loop.Name} at {loop.Rate:0} a second, {loop.WorkMs:0.00} ms a turn ({loop.PeakWorkMs:0.00} at worst), " +
                $"{loop.AllocatedPerTurn:0} bytes a turn which is {loop.AllocatedPerSecond / 1024.0:0.0} KB a second. " +
                $"{GC.CollectionCount(0)} small and {GC.CollectionCount(2)} big collections so far.");
        }

        // And what the GPU spent on the last frame it finished, pass by pass, the way the overlay shows it
        if (Graphics is { } device && device.GpuScopes.Length > 0)
        {
            var passes = new System.Text.StringBuilder();
            passes.Append($"[{Name}] GPU {device.GpuFrameMilliseconds:0.00} ms a frame");
            foreach (GpuScope scope in device.GpuScopes)
                passes.Append($", {new string('>', scope.Depth)}{scope.Name} {scope.Milliseconds:0.00}");
            Log.Info(passes.ToString());
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        simulation?.Dispose();

        if (SnapshotClock.Active == snapshots)
            SnapshotClock.Active = null;

        // Last, everything on the GPU has gone by now
        Graphics?.Dispose();
        Graphics = null;

        _window.Reset();
        _window.Dispose();
        windowWake.Dispose();
        simulationParked.Dispose();
        simulationResumed.Dispose();
        allocations?.Dispose();

        Log.Info($"[{Name}] Disposed!");
    }

}
