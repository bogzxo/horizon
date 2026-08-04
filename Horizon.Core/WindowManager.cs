using System.Diagnostics;
using System.Numerics;

using Bogz.Logging.Loggers;

using Horizon.Core.Components;
using Horizon.Core.Primitives;

using Silk.NET.Input;
using Silk.NET.Input.Glfw;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Glfw;

namespace Horizon.Core;

/// <summary>
/// Engine component that manages all associated window activities and threads.
/// </summary>
public class WindowManager : IGameComponent, IDisposable
{
    private readonly IWindow _window;
    private IInputContext _input;

    private Thread logicThread;

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
            Size = new Silk.NET.Maths.Vector2D<int>(
                (int)config.WindowSize.X,
                (int)config.WindowSize.Y
            ),
            UpdatesPerSecond = 120,
            FramesPerSecond = 0,
            ShouldSwapAutomatically = true,
            VSync = false,
            PreferredBitDepth = new Silk.NET.Maths.Vector4D<int>(8, 8, 8, 8),
            Samples = 0,
            
        };

        ViewportSize = WindowSize = config.WindowSize;

        // Create the window.
        this._window = Silk.NET.Windowing.Window.Create(WindowOptions);
        SubscribeWindowEvents();
    }
    

    private void SubscribeWindowEvents()
    {
        this._window.Render += (dt) => Parent.Render((float)dt);
        
        this._window.Update += (dt) => Parent.UpdateState((float)dt);
        this._window.Update += (dt) => Parent.UpdatePhysics((float)dt);

        this._window.Resize += WindowResize;

        this._window.Load += () =>
        {
            _window.Center();
            _window.SetDefaultIcon();

            GL = _window.CreateOpenGL();
            GLObject.SetGL(GL);

            _input = _window.CreateInput();

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

        // Dispose and unload
        _window.DoEvents();
    }

    private void OnLogicFrame()
    {
        while (!_window.IsClosing)
        {
            if (_window.IsInitialized)
                _window.DoUpdate();
        }
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
            _window.DoRender();

        /* it is important to ensure that atleast one Render pass has happened, before
         * we dispatch all the threads, as lazy initialization of unmanaged object is done in the render thread. */

        // Dispatch threads.
        if (needsDispatching)
        {
            needsDispatching = false;

            logicThread = new Thread(OnLogicFrame)
            {
                Name = "GameLogicThread",
                Priority = ThreadPriority.AboveNormal,
                IsBackground = true
            };

            logicThread.Start();

            //logicTask ??= Task.Run(OnLogicFrame, tokenSource.Token);
            //physicsTask ??= Task.Run(OnPhysicsFrame, tokenSource.Token);
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        tokenSource.Cancel();

        logicThread.Join();

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