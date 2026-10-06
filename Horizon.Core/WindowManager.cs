using System.Diagnostics;
using System.Numerics;

using Bogz.Logging.Loggers;


using Horizon.Core.Components;
using Horizon.Core.Primitives;
using Horizon.Core.Threading;

using Silk.NET.Input;
using Silk.NET.Input.Glfw;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Glfw;

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
public class WindowManager : IGameComponent, IDisposable
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

    /// <summary>
    /// How drawing and each of the loops of the engine are doing: how often they come round, how long their turns
    /// take and how unevenly they come. Logic and physics are there once they have been started, which is after
    /// the first frame.
    /// </summary>
    public IReadOnlyList<LoopStatistics> Loops { get; private set; }

    //private Task logicTask;
    //private Task physicsTask;

    private readonly CancellationTokenSource tokenSource;

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
    /// The GL context associated with the windows main render thread.
    /// </summary>
    public GL GL { get; private set; }


    public bool Enabled { get; set; }
    public string Name { get; set; } = "Window Manager";
    public Entity Parent { get; set; }

    /// <summary>
    /// Gets the underlying native window.
    /// </summary>
    /// <returns>The GLFW IWindow.</returns>
    public IWindow Window
    {
        get => _window;
    }

    /// <summary>
    /// Gets the windows native input context.
    /// </summary>
    /// <returns>Native IInputContext</returns>
    public IInputContext Input
    {
        get => _input;
    }

    // copy of initial WindowOptions instance.
    public readonly WindowOptions WindowOptions;

    public WindowManager(in WindowManagerConfiguration config)
    {
        GlfwWindowing.RegisterPlatform();
        GlfwInput.RegisterPlatform(); 

        tokenSource = new CancellationTokenSource();

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
            // The updates are not the window's to pace, they have loops of their own
            UpdatesPerSecond = 0,
            FramesPerSecond = Math.Max(0.0, config.FramesPerSecond),
            ShouldSwapAutomatically = true,
            VSync = config.VSync,
            PreferredBitDepth = new Silk.NET.Maths.Vector4D<int>(8, 8, 8, 8),
            PreferredStencilBufferBits = 8,
            Samples = 0,
            
        };

        ViewportSize = WindowSize = config.WindowSize;

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
                // Whatever the updates added since the last frame is set up before anything is drawn, and the
                // loops that are holding their turn back for it are told
                if (EntityLifecycle.HasPending)
                {
                    EntityLifecycle.Flush();
                    System.Threading.Monitor.PulseAll(simulationGate);
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
        //FrameBufferManager.ResizeAll(size.X, size.Y);
        UpdateViewport();
    }

    public void Initialize()
    {
        ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Info, $"[{Name}] Created window({WindowOptions.Size})!");
    }

    public void Render(float dt, object? obj = null)
    { }

    public void UpdateState(float dt)
    { }

    public void UpdatePhysics(float dt)
    { }

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
            System.Threading.Monitor.Wait(simulationGate, 4);

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

    //private async Task OnPhysicsFrame()
    //{
    //    // PeriodicTimer leverages OS high-resolution timers natively
    //    //using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));

    //    long previousTicks = Stopwatch.GetTimestamp();

    //    while (!(tokenSource.Token.IsCancellationRequested || _window.IsClosing))
    //    {
    //        // Await next tick without blocking thread pool resources
    //        //if (!await timer.WaitForNextTickAsync(tokenSource.Token))
    //        //    break;

    //        long currentTicks = Stopwatch.GetTimestamp();
    //        double deltaTime = (currentTicks - previousTicks) / (double)Stopwatch.Frequency;
    //        previousTicks = currentTicks;

    //        // Handle physics calculation
    //        Parent.UpdatePhysics((float)deltaTime);
    //    }
    //}

    private bool needsDispatching = true;

    private void OnFrame()
    {
        _window.DoEvents();

        if (!_window.IsClosing)
        {
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

        /* it is important to ensure that atleast one Render pass has happened, before
         * we dispatch all the threads, as lazy initialization of unmanaged object is done in the render thread. */

        // Dispatch threads.
        if (needsDispatching)
        {
            needsDispatching = false;

            StartLoops();

            //logicTask ??= Task.Run(OnLogicFrame, tokenSource.Token);
            //physicsTask ??= Task.Run(OnPhysicsFrame, tokenSource.Token);
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        tokenSource.Cancel();

        logicLoop?.Dispose();
        physicsLoop?.Dispose();

        //physicsTask.Wait();
        //logicTask.Wait();

        //physicsTask.Dispose();
        //logicTask.Dispose();

        tokenSource.Dispose();

        _window.Reset();
        _window.Dispose();

        ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Info, $"[{Name}] Disposed!");
    }

    /// <summary>
    /// Updates the windows title to the specified string <paramref name="title"/>.
    /// </summary>
    /// <param name="title">The new window title.</param>
    public void UpdateTitle(string title)
    {
        _window.Title = title;
    }
}