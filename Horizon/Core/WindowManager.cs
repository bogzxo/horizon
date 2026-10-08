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
/// talk to the GPU. The game is simulated on a third (see <see cref="SimulationLoop"/>): its logic and its physics,
/// one tick after another, each at a rate of its own. So a frame that takes its time doesn't make the input late,
/// and a window that is being dragged about doesn't stop the drawing.
/// </para>
/// <para>
/// Neither waits for the other. At the end of every tick the simulation publishes a snapshot of everything that is
/// drawn (<see cref="Entity.Capture"/>, <see cref="SnapshotClock"/>), and frames are drawn from the last two of those,
/// at the moment between them the frame is due to show (<see cref="Presentation"/>). So whatever moves goes the same
/// distance for the same time from frame to frame, however many frames there are a second, and a frame that takes
/// its time doesn't hold the game up (or the other way round). The simulation only stands still for what has to
/// happen on this thread with nobody touching the game: setting up what was added to it (<see cref="EntityLifecycle"/>),
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
    /// How drawing and each of the loops of the engine are doing: how often they come round, how long their turns
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
    /// The screen aspect ratio (w/h)
    /// </summary>
    public float AspectRatio => display.AspectRatio;

    /// <summary>
    /// The viewport size.
    /// </summary>
    public Vector2 ViewportSize => display.ViewportSize;

    /// <summary>
    /// The window size.
    /// </summary>
    public Vector2 WindowSize => display.WindowSize;

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
    /// gamepads coming and going), about a thousand times a second: the moment for whoever samples input to look at
    /// the devices, and for whatever else may only be done on this thread (the clipboard).
    /// </summary>
    public event Action? EventsProcessed;

    /// <summary>
    /// Raised on the thread that draws, at the start of a frame, with the simulation standing still: the place for
    /// whatever has to be done on that thread without anybody touching the game (swapping scenes). Whoever has
    /// something of the kind says so with <see cref="RequestExclusive"/> and checks whether it is still there to be done.
    /// </summary>
    public event Action<float>? Exclusive;

    /// <summary>
    /// Asks for <see cref="Exclusive"/> to be raised as soon as it can be: at the start of the next frame, after the
    /// simulation has finished the tick it is in. From any thread.
    /// </summary>
    public void RequestExclusive() => exclusiveRequested = true;

    /// <summary>
    /// Asks the window to close, which ends <see cref="Run"/> once the frame that is being drawn is done. From any thread.
    /// </summary>
    public void Close() => closing = true;

    private volatile bool closing;

    // copy of initial WindowOptions instance.
    public readonly WindowOptions WindowOptions;

    public WindowManager(in WindowManagerConfiguration config)
    {
        GlfwWindowing.RegisterPlatform();
        GlfwInput.RegisterPlatform();

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

        // Create a window with the specified options.
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

        // Create the window.
        this._window = Silk.NET.Windowing.Window.Create(WindowOptions);
        SubscribeWindowEvents();
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

            _window.SetDefaultIcon();

            _input = _window.CreateInput();

            UpdateViewport();
            UpdateScreenSize();
        };
    }

    /// <summary>
    /// Helper method to get everything that draws going, on the thread that draws, before its first frame.
    /// </summary>
    private void SetUpDrawing()
    {
        EntityLifecycle.ClaimRenderThread();
        SnapshotClock.Active = snapshots;

        // The device belongs to this thread from here on, it is the one that records and submits every frame
        Graphics = new GraphicsDevice(
            _window.VkSurface ?? throw new InvalidOperationException("The window has no Vulkan surface, is the Vulkan loader installed?"),
            (uint)ViewportSize.X,
            (uint)ViewportSize.Y,
            Display.VSync);

        Parent.Initialize();

        // Everything that was added to the engine before the window opened is set up now, before the first frame,
        // so whatever listens for that frame's exclusive work (the scene manager) is listening by then
        Parent.InitializeAll();
    }

    /// <summary>
    /// Helper method to draw a frame from what the simulation published, after whatever it stood still for.
    /// </summary>
    private void DrawFrame(float dt)
    {
        // The time the last frame spent on exclusive work (setting a scene up can take a good while) is time the game
        // stood still: it isn't time for what moves by the frames to move on by, or a transition would jump the moment
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
    /// <param name="always">Whether the simulation is standing still for it: if not, only what doesn't touch the game is done.</param>
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
        var size = new Vector2(_window.FramebufferSize.X, _window.FramebufferSize.Y);
        display = display with { ViewportSize = size, WindowSize = size };
    }

    private void WindowResize(Silk.NET.Maths.Vector2D<int> size)
    {
        // Minimised windows say they are nothing by nothing, which nobody can draw into
        if (size.X <= 0 || size.Y <= 0) return;

        UpdateViewport();
        resized = true;
    }

    private void UpdateScreenSize()
    {
        if (_window.Monitor is not { } monitor)
            return;

        var size = monitor.VideoMode.Resolution ?? monitor.Bounds.Size;
        display = display with { ScreenSize = new Vector2(size.X, size.Y) };
    }

    /// <summary>
    /// Changes how the window is shown and how often it is drawn. From any thread.
    /// <para>
    /// It takes hold at the start of the next frame, before anything of that frame is set up or drawn. So a scene that is set in the same
    /// update as this is called is made for the window as it is going to be, which is the way to do it: nothing that was made for the
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
    /// Helper method to do what <see cref="Apply"/> was asked for. On the thread that draws, at the start of a frame:
    /// how often frames are drawn and swapped is up to that thread, the window itself is changed on its own thread
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
        bool fullscreen = _window.WindowState == WindowState.Fullscreen;
        if (settings.Fullscreen)
        {
            if (!fullscreen)
                _window.WindowState = WindowState.Fullscreen;
        }
        else
        {
            if (fullscreen)
                _window.WindowState = WindowState.Normal;

            var size = new Silk.NET.Maths.Vector2D<int>(Math.Max(1, (int)settings.WindowSize.X), Math.Max(1, (int)settings.WindowSize.Y));
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

        // Create the window. Everything that draws happens on a thread of its own, see DrawFrames
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

        // Dispose and unload
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
                WaitForFrame();

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

        Log.Info($"[{Name}] Simulating at {simulation.TickRate:0} ticks a second (logic at {simulation.LogicRate:0}, physics at {simulation.PhysicsRate:0}), frames {(Presentation == PresentationMode.Interpolated ? "interpolated between ticks" : "showing the newest tick")}.");
        simulation.Start();
    }

    /// <summary>
    /// What the simulation loop runs: the engine's updates, and the publishing of a snapshot at the end of every tick.
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

                // Stood still for longer than a tick (a scene was set up): carried on from now rather than raced
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

        // Nobody came (no frames are being drawn): take it back, unless a frame took it just now, then wait for it to be done
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
        allocations?.Dispose();

        Log.Info($"[{Name}] Disposed!");
    }

}
