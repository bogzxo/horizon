using Horizon.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.ExceptionServices;

using Horizon.Core.Components;
using Horizon.Core.Diagnostics;
using Horizon.Core.Primitives;
using Horizon.Core.Threading;

using Horizon.Graphics;
using Horizon.Platform;

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
/// There are three of them. The platform's thread (the one <see cref="Run"/> or <see cref="Start"/> is called on) hears
/// what the system has to say about the window and its input, and is the only one that may change the window. What that
/// thread is and how it listens is the platform host's (<see cref="IPlatformHost"/>), a desktop's asks about a thousand
/// times a second whatever the frames are doing, a phone's is called back by its system. Frames are drawn on a thread of their own, the only one that may
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
public partial class WindowManager : GameComponent, IDisposable
{
    // The platform the window manager runs on, owned by it and disposed once the GPU is gone
    private readonly IPlatformHost host;
    private readonly HostEvents events;

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

    // Done once the frames stopped, failed with what they died of if they did, and the stop that waits for the
    // simulation after them
    private readonly TaskCompletionSource framesStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock stopLock = new();
    private Task? stopped;

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
    public Vector2 PixelScale => platformDisplay.PixelScale;

    /// <summary>
    /// How big the window is in whatever the system counts it and the pointer in, points on a Mac and pixels everywhere
    /// else. From any thread.
    /// </summary>
    public Vector2 PointSize => platformDisplay.PointSize;

    // Everything the platform said about how big things are, swapped whole
    private volatile PlatformDisplay platformDisplay;

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
    /// The render scale, how big the scene is drawn for the size of the window and what blows it back up. Its settings
    /// are set from any thread and take at the next frame, see <see cref="Horizon.Rendering.Scaling.FrameScaler"/>.
    /// </summary>
    public Horizon.Rendering.Scaling.FrameScaler Scaling { get; } = new();

    /// <summary>
    /// The platform the engine runs on. A desktop program that needs something native gets its concrete host from here
    /// (Horizon.Platform.Desktop's DesktopHost), the engine and the UI only ever ask the contract.
    /// </summary>
    public IPlatformHost Host => host;

    /// <summary>What the platform has to offer beyond a surface, see <see cref="PlatformFeatures"/>.</summary>
    public PlatformFeatures Features => host.Features;

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
    /// What the title bar of the window says. From any thread, it changes at the start of the next frame.
    /// </summary>
    public string Title
    {
        get => title;
        set
        {
            title = value;
            host.Window?.SetTitle(value);
        }
    }

    private volatile string title;

    /// <summary>
    /// Raised on the thread that draws, at the start of the first frame after the size of what is drawn into changed,
    /// with the new size in pixels.
    /// </summary>
    public event Action<Vector2>? Resized;

    /// <summary>
    /// Raised on the platform's thread every time it has heard what the system had to say (keys, the mouse, gamepads
    /// coming and going), about a thousand times a second on a desktop. The moment for whatever may only be done on
    /// that thread (the clipboard).
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
    public void RequestExclusive()
    {
        exclusiveRequested = true;
        RequestFrames();
    }

    /// <summary>
    /// Asks the window to close, which ends <see cref="Run"/> once the frame that is being drawn is done. From any thread.
    /// </summary>
    public void Close() => host.RequestClose();

    // How big the window was asked to be
    private readonly Vector2 requestedSize;

    /// <summary>A window manager on the platform the program installed, see <see cref="PlatformInstallation"/>.</summary>
    public WindowManager(in WindowManagerConfiguration config)
        : this(config, PlatformInstallation.CreateHost(PlatformInstallation.Resolve(null), new PlatformHostRequest(config, BehaviourOf(config), ReadsGamepads: true)))
    {
    }

    /// <summary>
    /// A window manager on a host made for it, which it owns from here on and disposes last, after the GPU.
    /// </summary>
    public WindowManager(in WindowManagerConfiguration config, IPlatformHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        this.host = host;
        events = new HostEvents(this);

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
        requestedSize = config.WindowSize;

        updatesPerSecond = config.UpdatesPerSecond > 0.0 ? config.UpdatesPerSecond : 120.0;
        physicsUpdatesPerSecond = config.PhysicsUpdatesPerSecond > 0.0 ? config.PhysicsUpdatesPerSecond : 120.0;
        Loops = [renderStatistics];

        Presentation = config.Presentation;

        // Only a window with a frame of its own to draw gets one, a phone's view has none
        CustomFrame = config.CustomFrame && (host.Features & PlatformFeatures.WindowFrame) != 0;
        // HORIZON_SCALE_TO_DISPLAY=on has the UI fold the screen's scale in for a run whose program didn't ask for it,
        // a phone's density for a test of the UI at dp sizes. The window itself is made the way the configuration says
        ScalesToDisplay = config.ScaleToDisplay
            || string.Equals(Environment.GetEnvironmentVariable("HORIZON_SCALE_TO_DISPLAY"), "on", StringComparison.OrdinalIgnoreCase);
        Scaling.Settings = Horizon.Rendering.Scaling.RenderScaleEnvironment.Startup(config.RenderScale, host.Features, Name);
        Mode = config.Mode;
        Behaviour = BehaviourOf(config);

        display = new DisplayState(config.WindowSize, config.WindowSize, config.WindowSize);
        platformDisplay = PlatformDisplay.Of(config.WindowSize);

        Display = new DisplaySettings
        {
            Fullscreen = config.Fullscreen,
            WindowSize = config.WindowSize,
            VSync = config.VSync,
            FramesPerSecond = Math.Max(0.0, config.FramesPerSecond)
        };
        framePeriod = PeriodOf(Display.FramesPerSecond);

        // What the Vulkan loader is told goes in before GLFW or the device ask it anything, see VulkanLoader
        Horizon.Graphics.Vulkan.VulkanLoader.Configure(application: Mode == RunMode.Application);

        // The GPU gets going now, while the game makes its scene and the window is made, see WindowManager.Device
        StartDeviceAhead();

        // The window itself is the platform's to make, which is told who hears about it
        host.Attach(events);
    }

    /// <summary>What a configuration's window does to save work while nothing is happening, see <see cref="ApplicationBehaviour"/>.</summary>
    public static ApplicationBehaviour BehaviourOf(in WindowManagerConfiguration config) =>
        config.Behaviour ?? (config.Mode == RunMode.Application ? ApplicationBehaviour.All : ApplicationBehaviour.None);
    /// <summary>
    /// Helper method to get everything that draws going, on the thread that draws, before its first frame.
    /// </summary>
    private void SetUpDrawing()
    {
        EntityLifecycle.ClaimRenderThread();
        SnapshotClock.Active = snapshots;

        // The device belongs to this thread from here on, it is the one that records and submits every frame. The
        // surface is the platform's, which makes it on whichever thread its system wants (a Mac's main one)
        surface = host.Surface;

        // Most of it made already on Windows, see WindowManager.Device
        Graphics = MakeDevice(surface);
        StartupTrace.Mark("device made");

        // The first thing in the window, before anything of the engine or the game is set up (the engine's loading
        // screen), and only then is there a window to see
        DeviceReady?.Invoke();
        if (host.Window is { } window)
            OnWindowThread(window.Show);
        StartupTrace.Mark("window shown with the loading screen");

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

    /// <summary>
    /// Helper method to turn where the system says the pointer is in the window into the pixel of what is drawn that
    /// is under it. From any thread.
    /// </summary>
    internal Vector2 PointerToPixels(Vector2 pointer) => pointer * PixelScale;
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

        if (host.Window is { } window)
            OnWindowThread(() => window.ApplyDisplay(settings));

        Log.Info($"[{Name}] The window is {(settings.Fullscreen ? "fullscreen" : "windowed")} at {ViewportSize.X} by {ViewportSize.Y} now, {(settings.VSync ? "with" : "without")} vsync and {(settings.FramesPerSecond > 0.0 ? $"at most {settings.FramesPerSecond} frames a second" : "no limit on its frames")}.");
    }

    /// <summary>
    /// Helper method to have something done on the platform's thread, the only one that may touch the window, and wait
    /// for it. Done right there when it is the platform's thread already, which the host knows. Not just any thread
    /// that isn't the one that draws, a phone's simulation or audio thread isn't the platform's either.
    /// </summary>
    private void OnWindowThread(Action work) => host.Invoke(work);

    /// <summary>
    /// Has something done to the window on its thread the next time it looks, without waiting for it. From any thread,
    /// for whatever only the platform's thread may touch (the cursor, say).
    /// </summary>
    internal void PostToWindow(Action work) => host.Post(work);
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

        LoopTiming.WaitUntil(nextFrame, spin: Mode != RunMode.Application);
        nextFrame += period;
    }

    public override void Initialize()
    {
        Log.Info($"[{Name}] Created window({requestedSize})!");
    }

    /// <summary>
    /// Opens the window and runs the game until it is closed, on a platform with a loop of its own (a desktop's). The
    /// calling thread is the platform's from here on and is handed to that loop.
    /// </summary>
    public void Run()
    {
        if (host is not IPlatformLoop loop)
            throw new InvalidOperationException($"{host.GetType().Name} has no loop to run, start the engine with Start and stop it with StopAsync.");

        Start();

        AllocationLog.NameThisThread("Window");
        LoopTiming.SharpenTimer(true);
        try
        {
            loop.Run();
        }
        finally
        {
            LoopTiming.SharpenTimer(false);
        }

        // The last frame is finished, and whatever it may still need done to the window with it
        Task done = StopAsync();
        loop.PumpUntil(done);

        // Whatever is let go of from here on is let go of here, GPU and all
        EntityLifecycle.ClaimRenderThread();

        // One last listen to the system, so the window gets to hear it is closed
        loop.Finish();

        done.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Gets the window going on the calling thread, which is the platform's from here on, starts the frames on a thread
    /// of their own and returns. A desktop goes into its loop next (see <see cref="Run"/>), a phone's activity goes back
    /// to its system. The thread that draws makes the GPU, puts the loading screen up, has the window shown, sets the
    /// engine up and draws the first frame, and only then starts the simulation.
    /// </summary>
    public void Start()
    {
        if (IsRunning)
            throw new InvalidOperationException("Window is already running!");

        IsRunning = true;

        // The window, and then everything that draws happens on a thread of its own, see DrawFrames
        StartupTrace.Mark("window being made");
        host.Initialize();
        TakeState(host.State);
        StartupTrace.Mark("window made");

        renderThread = new Thread(DrawFrames)
        {
            Name = "Render",
            Priority = ThreadPriority.AboveNormal,
            IsBackground = true
        };
        renderThread.Start();
    }

    /// <summary>
    /// Stops the frames and then the simulation without waiting for either. Done when both are, failed with whatever the
    /// frames died of. The platform's thread has to go on doing what is posted to it until then, a frame may still need
    /// something done to the window to finish. The GPU and its surface are let go of in <see cref="Dispose"/> on the
    /// platform's thread, and only after them the native window. From any thread, the same task every time.
    /// </summary>
    public Task StopAsync()
    {
        lock (stopLock)
        {
            if (stopped is not null)
                return stopped;

            renderStopping = true;

            // Out of its wait for something to draw, there's no heartbeat to wait for
            frameWanted.Set();

            if (renderThread is null)
            {
                framesStopped.TrySetResult();
                if (simulation is null)
                    return stopped = framesStopped.Task;
            }

            stopped = framesStopped.Task.ContinueWith(static (frames, state) =>
            {
                // Nothing is simulated once there is no window left to show it in
                ((WindowManager)state!).simulation?.Stop();
                frames.GetAwaiter().GetResult();
            }, this, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return stopped;
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
                WaitForDemand();
                WaitForFrame();
                WaitWithoutSurface();
                renderStatistics.SetState(LoopStatistics.LoopState.Working);

                // The surface goes and comes back between frames, never in one, see WindowManager.Surface
                DoSurfaceRequests();

                long started = Stopwatch.GetTimestamp();
                long allocated = GC.GetAllocatedBytesForCurrentThread();

                float dt = previous == 0 ? 0.0f : (float)((started - previous) / (double)Stopwatch.Frequency);
                previous = started;

                Graphics!.BeginFrame((uint)ViewportSize.X, (uint)ViewportSize.Y);
                Scaling.BeginFrame(Graphics, (uint)ViewportSize.X, (uint)ViewportSize.Y, DisplayScale);
                DrawFrame(dt);
                Scaling.EndFrame();
                Graphics.EndFrame();
                if (StartupTrace.SceneReady)
                    StartupTrace.Finish("first frame of the scene shown");

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
            host.RequestClose();
        }
        finally
        {
            renderStatistics.SetState(LoopStatistics.LoopState.Stopped);
            CancelSurfaceRequests();

            // Nothing is on its way to the GPU once the frames stop, whatever gets freed afterwards is free to go
            try
            {
                // The scaled scene's target goes with the frames, on the thread that made it
                if (Graphics is not null)
                    Scaling.Dispose();
                Graphics?.WaitIdle();
            }
            finally
            {
                if (renderFailure is { } failure)
                    framesStopped.TrySetException(failure.SourceException);
                else
                    framesStopped.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Helper method to start simulating the game, on a thread of its own.
    /// </summary>
    private void StartSimulation()
    {
        simulation = new SimulationLoop("Simulation", updatesPerSecond, physicsUpdatesPerSecond, new SimulationHost(this))
        {
            // An application's ticks that run late are one longer tick, not a run of them, see SimulationLoop.StretchWhenBehind
            StretchWhenBehind = Mode == RunMode.Application,
            PrecisePacing = Mode != RunMode.Application
        };
        if ((Behaviour & ApplicationBehaviour.SlowWhenIdle) != 0)
        {
            simulation.IsIdle = IdleNow;
            UpdateIdlePeriod();
        }

        Loops = [renderStatistics, simulation.Ticks, simulation.Logic, simulation.Physics];

        // The clock is told how far apart the ticks come rather than left to find out, see SnapshotClock.NominalInterval
        snapshots.NominalInterval = 1.0 / simulation.TickRate;

        Log.Info($"[{Name}] Simulating at {simulation.TickRate:0} ticks a second (logic at {simulation.LogicRate:0}, physics at {simulation.PhysicsRate:0}), frames {(Presentation == PresentationMode.Interpolated ? "interpolated between ticks" : "showing the newest tick")}.");
        simulation.Start();
    }

    /// <summary>
    /// What the simulation loop runs, the engine's updates and the publishing of a snapshot at the end of every tick.
    /// </summary>
    private sealed class SimulationHost(WindowManager window) : ISimulationHost
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

        // A window that only draws when asked has to be asked, or this waited for its heartbeat
        RequestFrames();

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
            var totals = loop.AllocationTotals;
            Log.Info(
                $"[{Name}] {loop.Name} at {loop.Rate:0} a second, {loop.WorkMs:0.00} ms a turn ({loop.PeakWorkMs:0.00} at worst), " +
                $"{loop.AllocatedPerTurn:0} bytes a turn which is {loop.AllocatedPerSecond / 1024.0:0.0} KB a second. " +
                $"{GC.CollectionCount(0)} small and {GC.CollectionCount(2)} big collections so far. " +
                $"Totals: {totals.Turns} turns, {totals.Bytes} bytes.");
        }

        // And what the process weighs. The heap the garbage collector has in hand against what it has committed
        // from the machine, the process as the machine sees it, the memory the device has taken in blocks (the part
        // of it the CPU can see is the machine's memory as well), and the threads
        var info = GC.GetGCMemoryInfo();
        using (var process = System.Diagnostics.Process.GetCurrentProcess())
        {
            Log.Info(
                $"[{Name}] memory, heap {GC.GetTotalMemory(false) / (1024.0 * 1024.0):0.0} MB of {info.TotalCommittedBytes / (1024.0 * 1024.0):0.0} committed, " +
                $"working set {process.WorkingSet64 / (1024.0 * 1024.0):0} MB, private {process.PrivateMemorySize64 / (1024.0 * 1024.0):0} MB, " +
                $"{process.Threads.Count} threads" +
                (Graphics is { } gpu
                    ? $", device {gpu.Statistics.MemoryInUse / (1024.0 * 1024.0):0.0} MB in use of {gpu.Statistics.MemoryBlockBytes / (1024.0 * 1024.0):0} in {gpu.Statistics.MemoryBlocks} blocks, {gpu.Statistics.MemoryHostBytes / (1024.0 * 1024.0):0} MB of them mapped"
                    : string.Empty));
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

        // A host that's called back by its system may dispose without having stopped first. The frames are done before
        // anything goes, and whatever is let go of is let go of on this thread
        if (IsRunning)
        {
            Task stopping = StopAsync();
            if (!stopping.IsCompleted)
            {
                // A frame may still need the platform's thread, so that one goes on doing its work while it waits. A host
                // without a loop has to wait for StopAsync itself before it disposes, nothing here can pump its thread
                if (host is IPlatformLoop loop)
                    loop.PumpUntil(stopping);
                else if (host.IsPlatformThread)
                    throw new InvalidOperationException("Stop the engine with StopAsync and let the platform's thread go on until that's done, then dispose it.");
            }

            try
            {
                stopping.Wait();
            }
            catch (AggregateException)
            {
                // What the frames died of was somebody else's to hear, Run throws it
            }
            EntityLifecycle.ClaimRenderThread();
        }

        simulation?.Dispose();

        // A device made ahead for a window that never ran
        TakeDeviceAhead()?.Dispose();

        if (SnapshotClock.Active == snapshots)
            SnapshotClock.Active = null;

        // Last, everything on the GPU has gone by now, the window's surface with it
        Graphics?.Dispose();
        Graphics = null;

        // And only then the native window that was under the surface
        host.Dispose();
        simulationParked.Dispose();

        simulationResumed.Dispose();
        allocations?.Dispose();

        Log.Info($"[{Name}] Disposed!");
    }

}
