using Bogz.Logging;
using System.Diagnostics;
using System.Numerics;

using Horizon.Core.Components;
using Horizon.Core.Primitives;
using Horizon.Core.Threading;

using Silk.NET.Input;
using Silk.NET.Input.Glfw;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Glfw;

using Monitor = System.Threading.Monitor;

namespace Horizon.Core;

/// <summary>
/// Engine component that manages all associated window activities and threads.
/// <para>
/// There are three of them. The thread the window was made on draws (it is the only one that may talk to the GPU)
/// and handles what the system sends the window. The state of the game and its physics are each updated by a loop
/// of their own (see <see cref="EngineLoop"/>), at a rate of their own: the physics in steps that are all the same
/// length, the state as often as it is set to and told how long it has been.
/// </para>
/// <para>
/// The two loops work on the same things (a game moves its bodies about from its logic), so they take turns: only
/// one of them is ever in its update, see <see cref="simulationGate"/>. What that buys over one thread doing both
/// is that neither sets the pace of the other. A slow update of the state doesn't make the physics take one big
/// step, and the physics can be run faster or slower than the logic.
/// Drawing takes its turn along with them, so every frame shows one moment of the game and never half of a step.
/// </para>
/// </summary>
public class WindowManager : GameComponent, IDisposable
{
    private readonly IWindow _window;
    private IInputContext _input;

    // Held by whichever of the logic loop, the physics loop and the drawing is at work on the game, so the
    // others wait their turn
    private readonly object simulationGate = new();

    private EngineLoop? logicLoop, physicsLoop;
    private readonly double updatesPerSecond, physicsUpdatesPerSecond;

    // The longest (in milliseconds) a loop holds its turn back for what was added to the game to be set up. That
    // happens at the start of the next frame, so this only runs out when no frames are drawn
    private const int LONGEST_SET_UP_WAIT = 250;

    private readonly LoopStatistics renderStatistics = new("Render", 0.0);
    private long lastFrame;

    // How long (in seconds) there is between two frames at the least, 0 for as many as there is time for. And when the next one is due
    private double framePeriod, nextFrame;

    // What was asked of the window from another thread and is still to be done. Only the thread the window was made on may touch it
    private readonly Lock displayLock = new();
    private DisplaySettings? pendingDisplay;

    /// <summary>
    /// How drawing and each of the loops of the engine are doing: how often they come round, how long their turns
    /// take and how unevenly they come. Logic and physics are there once they have been started, which is after
    /// the first frame.
    /// </summary>
    public IReadOnlyList<LoopStatistics> Loops { get; private set; }

    public bool IsRunning { get; private set; }

    /// <summary>
    /// The screen aspect ratio (w/h)
    /// </summary>
    public float AspectRatio { get; private set; }

    /// <summary>
    /// The viewport size.
    /// </summary>
    public Vector2 ViewportSize { get; private set; }

    /// <summary>
    /// The window size.
    /// </summary>
    public Vector2 WindowSize { get; private set; }

    /// <summary>
    /// The size of the screen the window is on, in pixels. A window can't usefully be any bigger than this.
    /// </summary>
    public Vector2 ScreenSize { get; private set; }

    /// <summary>
    /// How the window is shown and how often it is drawn, as it was last set with <see cref="Apply"/> (or by the configuration, before anybody did).
    /// </summary>
    public DisplaySettings Display { get; private set; }

    /// <summary>
    /// The GL context associated with the windows main render thread.
    /// </summary>
    public GL GL { get; private set; }

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
    /// Raised on the thread of the window when the size of what is drawn into changes, with the new size in pixels.
    /// </summary>
    public event Action<Vector2>? Resized;

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
        title = config.WindowTitle ?? string.Empty;

        updatesPerSecond = config.UpdatesPerSecond > 0.0 ? config.UpdatesPerSecond : 120.0;
        physicsUpdatesPerSecond = config.PhysicsUpdatesPerSecond > 0.0 ? config.PhysicsUpdatesPerSecond : 120.0;
        Loops = [renderStatistics];

        // Create a window with the specified options.
        WindowOptions = WindowOptions.Default with
        {
            API = new GraphicsAPI()
            {
                Flags = ContextFlags.ForwardCompatible,
                API = ContextAPI.OpenGL,
                Profile = ContextProfile.Core,
                Version = new APIVersion(4, 6),
            },
            Title = config.WindowTitle,
            WindowState = config.Fullscreen ? WindowState.Fullscreen : config.Maximized ? WindowState.Maximized : WindowState.Normal,
            Size = new Silk.NET.Maths.Vector2D<int>(
                (int)config.WindowSize.X,
                (int)config.WindowSize.Y
            ),
            // The updates are not the window's to pace, they have loops of their own. Neither are the frames,
            // it would spend the wait for the next one spinning (see WaitForFrame)
            UpdatesPerSecond = 0,
            FramesPerSecond = 0,
            ShouldSwapAutomatically = true,
            VSync = config.VSync,
            PreferredBitDepth = new Silk.NET.Maths.Vector4D<int>(8, 8, 8, 8),
            PreferredStencilBufferBits = 8,
            Samples = 0,

        };

        ViewportSize = WindowSize = ScreenSize = config.WindowSize;

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
        // A frame is drawn from one moment of the game, not from two: drawing reads where everything is piece by
        // piece, and a step of the physics landing in between would have the frame show a player where they are
        // now against a camera from where they were. So drawing takes its turn at the gate like the loops do.
        // Only the drawing itself: the wait for the screen that comes after is when the loops get theirs.
        this._window.Render += (dt) =>
        {
            lock (simulationGate)
            {
                // First, so whatever is set up or drawn from here on finds the window the size it was asked to be
                ApplyPendingDisplay();

                // Whatever the updates added since the last frame is set up before anything is drawn (and what they
                // destroyed is freed), and the loops that are holding their turn back for it are told
                if (EntityLifecycle.HasWork)
                {
                    EntityLifecycle.Flush();
                    Monitor.PulseAll(simulationGate);
                }

                Parent.Render((float)dt);
            }
        };

        this._window.Resize += WindowResize;

        this._window.Load += () =>
        {
            // A maximised window is where it belongs already, centering it would take it back out of that
            if (WindowOptions.WindowState != WindowState.Maximized)
                _window.Center();

            _window.SetDefaultIcon();

            EntityLifecycle.ClaimRenderThread();

            GL = _window.CreateOpenGL();
            GLObject.SetGL(GL);

            _input = _window.CreateInput();

            // TODO: @bogz investigate why errors crash the integration
            GL.GetError();

            UpdateViewport();
            UpdateScreenSize();
            Parent.Initialize();
        };
    }

    private void UpdateViewport()
    {
        WindowSize = new Vector2(_window.FramebufferSize.X, _window.FramebufferSize.Y);
        ViewportSize = new Vector2(_window.FramebufferSize.X, _window.FramebufferSize.Y);
        AspectRatio = WindowSize.X / WindowSize.Y;
    }

    private void WindowResize(Silk.NET.Maths.Vector2D<int> size)
    {
        // Minimised windows say they are nothing by nothing, which nobody can draw into
        if (size.X <= 0 || size.Y <= 0) return;

        UpdateViewport();
        Resized?.Invoke(ViewportSize);
    }

    private void UpdateScreenSize()
    {
        if (_window.Monitor is not { } monitor)
            return;

        var size = monitor.VideoMode.Resolution ?? monitor.Bounds.Size;
        ScreenSize = new Vector2(size.X, size.Y);
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
    /// Helper method to do to the window what <see cref="Apply"/> was asked for. On the thread of the window, at the start of a frame.
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
        _window.VSync = settings.VSync;

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

        Log.Info($"[{Name}] The window is {(settings.Fullscreen ? "fullscreen" : "windowed")} at {ViewportSize.X} by {ViewportSize.Y} now, {(settings.VSync ? "with" : "without")} vsync and {(settings.FramesPerSecond > 0.0 ? $"at most {settings.FramesPerSecond} frames a second" : "no limit on its frames")}.");
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

        EngineLoop.WaitUntil(nextFrame);
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

        // Create the window.
        _window.Initialize();

        // Run the loop.
        _window.Run(OnFrame);

        // Nothing is updated once there is no window left to show it in
        logicLoop?.Stop();
        physicsLoop?.Stop();

        // Dispose and unload
        _window.DoEvents();
    }

    /// <summary>
    /// Helper method to hold the turn of a loop back until everything that was added to the game has been set up,
    /// which the render thread does at the start of its next frame (see <see cref="EntityLifecycle"/>). Called
    /// with the gate held, which is let go of for as long as the wait takes.
    /// </summary>
    /// <returns>Whether the turn is to be taken at all, which it isn't once the window is closing.</returns>
    private bool AwaitSetUp()
    {
        long started = Environment.TickCount64;

        while (EntityLifecycle.HasPending && !_window.IsClosing && Environment.TickCount64 - started < LONGEST_SET_UP_WAIT)
            Monitor.Wait(simulationGate, 4);

        return !_window.IsClosing;
    }

    /// <summary>
    /// Helper method to start the loops that update the game, each on a thread of its own.
    /// </summary>
    private void StartLoops()
    {
        logicLoop = new EngineLoop(
            "Logic",
            updatesPerSecond,
            dt => { if (AwaitSetUp()) Parent.UpdateState(dt); },
            fixedStep: false,
            gate: simulationGate);

        // The same step every time, and the steps it misses made up for: what the physics comes to mustn't
        // depend on how busy the machine was
        physicsLoop = new EngineLoop(
            "Physics",
            physicsUpdatesPerSecond,
            dt => { if (AwaitSetUp()) Parent.UpdatePhysics(dt); },
            fixedStep: true,
            gate: simulationGate);

        Loops = [renderStatistics, logicLoop.Statistics, physicsLoop.Statistics];

        logicLoop.Start();
        physicsLoop.Start();
    }

    private bool needsDispatching = true;

    private void OnFrame()
    {
        _window.DoEvents();

        if (closing) _window.Close();

        if (titleChanged)
        {
            titleChanged = false;
            _window.Title = title;
        }

        if (!_window.IsClosing)
        {
            WaitForFrame();

            long started = Stopwatch.GetTimestamp();
            _window.DoRender();
            long ended = Stopwatch.GetTimestamp();

            // With the wait for the screen in it, which is part of what a frame takes when frames are kept in step with it
            if (lastFrame != 0)
            {
                renderStatistics.Record(
                    (ended - started) / (double)Stopwatch.Frequency,
                    0.0,
                    (started - lastFrame) / (double)Stopwatch.Frequency);
            }
            lastFrame = started;
        }

        // The loops only start once a frame has been drawn. Everything is set up on this thread, and there has to be something to update
        if (needsDispatching)
        {
            needsDispatching = false;

            StartLoops();
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        logicLoop?.Dispose();
        physicsLoop?.Dispose();

        _window.Reset();
        _window.Dispose();

        Log.Info($"[{Name}] Disposed!");
    }

}