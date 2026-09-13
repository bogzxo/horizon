using System.Diagnostics;
using System.Numerics;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;

using Bogz.Logging;
using Bogz.Logging.Loggers;

using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Engine.Components;
using Horizon.Engine.Debugging;
using Horizon.Engine.Webhost;
using Horizon.Engine.WebHost;
using Horizon.Input;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Managers;

using Silk.NET.Input.Glfw;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing.Glfw;

using SixLabors.ImageSharp;

namespace Horizon.Engine;

public class GameEngine : Entity
{
    /// <summary>
    /// A copy of the game engines initial configuration.
    /// </summary>
    public GameEngineConfiguration Configuration { get; init; }

    public GL GL => WindowManager.GL;

    public static GameEngine Instance { get; private set; }

    /// <summary>
    /// Gets the main active camera associated with the current active Scene.
    /// </summary>
    public Camera ActiveCamera
    {
        get
        {
            var activeCamera = SceneManager.CurrentInstance?.ActiveCamera;
            return activeCamera ?? camera;
        }
    }

    public Camera camera;

    /// <summary>
    /// Total time in seconds that the window has been open.
    /// </summary>
    public float TotalTime { get; private set; } = 0.0f;

    public EngineEventHandler EventManager { get; init; }
    public ObjectManager ObjectManager { get; init; }
    public WindowManager WindowManager { get; init; }
    public SceneManager SceneManager { get; init; }
    public InputManager InputManager { get; init; }

    public Horizon.Webhost.WebHost WebHost { get; init; }
    public SkylineDebugger Debugger { get; init; }
    public float Runtime { get; private set; }

    public void SetScene(in Scene scene)
     {
        SceneManager.SetScene(scene);
    }

    public GameEngine(in GameEngineConfiguration engineConfiguration)
    {
        Name = "Engine";

        Instance = GameObject.Engine = this;
        Configuration = engineConfiguration;

        Enabled = true;

        // Engine components
        EventManager = AddComponent<EngineEventHandler>();
        ObjectManager = AddComponent<ObjectManager>();
        InputManager = AddComponent<InputManager>();

        // Engine children
        Debugger = AddEntity<SkylineDebugger>();
        SceneManager = AddEntity<SceneManager>();
        WebHost = AddEntity<Horizon.Webhost.WebHost>(); // initialize default content provider
        WebHost.ContentProviders.Add("dash", new DashboardContentProvider());

        // TryCreate window manager, the window manager will bootstrap and call Initialize(), Render(), UpdateState() and UpdatePhysics()
        WindowManager = AddComponent<WindowManager>(new(Configuration.WindowConfiguration));
    }

    public override void Initialize()
    {
        base.Initialize();
        camera = AddEntity(new Camera2D(WindowManager.ViewportSize));

        unsafe
        {
            GL.Enable(EnableCap.Texture2D);
            GL.Enable(EnableCap.DebugOutput);

            for (int i = 0; i < 16; i++)
            {
                GL.ActiveTexture(TextureUnit.Texture0 + i);
            }

            GL.DebugMessageCallback(debugCallback, null);
        }

    }


    private void debugCallback(
        GLEnum source,
        GLEnum type,
        int id,
        GLEnum severity,
        int length,
        nint message,
        nint userParam
    )
    {
        if (id == 131185 || id == 1280)
            return;

        ConcurrentLogger
            .Instance
            .Log(
                LogLevel.Info,
                $"[{source}] [{severity}] [{type}] [{id}] {Marshal.PtrToStringAnsi(message)}"
            );
    }

    public void DrawWithMetrics(in Entity entity, in float dt)
    {
        var startTime = Stopwatch.GetTimestamp();
        entity.InitializeAll();
        entity.Render(dt, null);
        var endTime = Stopwatch.GetTimestamp();
        var val = (double)(endTime - startTime) / Stopwatch.Frequency;
        Debugger.PerformanceDebugger.GpuMetrics.Aggregate(
            "EngineComponents",
            entity.Name,
            val
        );
    }

    public void DrawWithMetrics(in IGameComponent component, in float dt)
    {
        var startTime = Stopwatch.GetTimestamp();
        component.Render(dt, null);
        var endTime = Stopwatch.GetTimestamp();
        if (component.Name == "Scene Manager")
            return;

        var val = (double)(endTime - startTime) / Stopwatch.Frequency;
        Debugger.PerformanceDebugger.GpuMetrics.Aggregate(
            "EngineComponents",
            component.Name,
            val
        );
    }

    public override void UpdatePhysics(float dt)
    {
        EventManager.PrePhysics?.Invoke(dt);
        //Debugger.PerformanceDebugger.CpuMetrics.TimeAndTrackMethod(
        //        () =>
        //        {
        //            base.UpdatePhysics(dt);
        //        },
        //        "Engine",
        //        "Physics"
        //      );
        base.UpdatePhysics(dt);
        EventManager.PostPhysics?.Invoke(dt);
    }

    public override void UpdateState(float dt)
    {
        Runtime += dt;

        // Run our custom events.
        EventManager.PreState?.Invoke(dt);
        base.UpdateState(dt);
        //UpdatePhysics(dt);

        // Run our custom events.
        EventManager.PostState?.Invoke(dt);
    }

    public override void Render(float dt, object? obj = null)
    {
        TotalTime += dt;

        // Run our custom events.
        EventManager.PreRender?.Invoke(dt);

        if (Debugger.RenderToContainer)
        {
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);
            Debugger.GameContainerDebugger.FrameBuffer.Bind();
            Debugger.GameContainerDebugger.FrameBuffer.Viewport();
        }
        else GL.Viewport(0, 0, (uint)WindowManager.ViewportSize.X, (uint)WindowManager.ViewportSize.Y);
        
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);
        // Render all entities & component
        base.Render(dt);

        if (Debugger.RenderToContainer)
        {
            ObjectManager.GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

            GL.Viewport(0, 0, (uint)WindowManager.ViewportSize.X, (uint)WindowManager.ViewportSize.Y);
        }

        GL.GetError();
        WindowManager.Egui.Run(this.RenderUi);

        EventManager.PostRender?.Invoke(dt);
    }

    protected override void DisposeOther()
    {
        ConcurrentLogger.Instance.Dispose();
    }

    /// <summary>
    /// Instantiates a window, and opens it.
    /// </summary>
    public virtual void Run() => WindowManager.Run();

    /// <summary>
    /// Aggregates all metrics to be sent to the web host
    /// </summary>
    internal TelemetryData CollectTelemetry()
    {
        return new TelemetryData
        {
            LogicRate = Debugger.PerformanceDebugger.LogicRate,
            RenderRate = Debugger.PerformanceDebugger.RenderRate,
            PhysicsRate = Debugger.PerformanceDebugger.PhysicsRate
        };
    }
}