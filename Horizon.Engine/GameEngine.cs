using System.Diagnostics;
using System.Numerics;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;

using Bogz.Logging;
using Bogz.Logging.Loggers;

using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Engine.Components;
using Horizon.Engine.Debugging.Debuggers;
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

    //public Horizon.Webhost.WebHost WebHost { get; init; }
    /// <summary>
    /// The console of the engine: a HIDL runtime that whatever talks to the running game from outside (the web
    /// dashboard) has its commands run by. It has no window of its own.
    /// </summary>
    public DeveloperConsole Console { get; init; }
    public float Runtime { get; private set; }

    public void SetScene(in Scene scene)
     {
        SceneManager.SetScene(scene);
    }

    /// <summary>
    /// Changes the scene through a transition of its own (or with a hard cut, for null), whatever the scene manager is set to.
    /// </summary>
    public void SetScene(in Scene scene, SceneTransition? transition)
    {
        SceneManager.SetScene(scene, transition);
    }

    public GameEngine(in GameEngineConfiguration engineConfiguration)
    {
        Name = "Engine";

        Instance = GameObject.Engine = this;
        Configuration = engineConfiguration;

        Enabled = true;

        // What an entity makes on the GPU while it is set up belongs to the scene it is in, and to nobody if
        // it isn't in one: see Scene.Assets
        EntityLifecycle.Scope = static entity =>
        {
            for (Entity? at = entity; at is not null; at = at.Parent)
            {
                if (at is Scene scene)
                    return scene.Assets.Enter();
            }

            return Horizon.Content.AssetScope.EnterGlobal();
        };

        // Engine components
        EventManager = AddComponent<EngineEventHandler>();
        ObjectManager = AddComponent<ObjectManager>();
        InputManager = AddComponent<InputManager>();

        // Engine children
        Console = AddComponent<DeveloperConsole>();
        SceneManager = AddEntity<SceneManager>();
        //WebHost = AddEntity<Horizon.Webhost.WebHost>(); // initialize default content provider
        //WebHost.ContentProviders.Add("dash", new DashboardContentProvider());

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

        GL.Viewport(0, 0, (uint)WindowManager.ViewportSize.X, (uint)WindowManager.ViewportSize.Y);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);
        // Render all entities & component
        base.Render(dt);

        GL.GetError();

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

#if DEBUG
    /// <summary>
    /// Aggregates all metrics to be sent to the web host
    /// </summary>
    internal TelemetryData CollectTelemetry()
    {
        // As the window manager measures them
        double RateOf(string loop)
        {
            foreach (var statistics in WindowManager.Loops)
            {
                if (statistics.Name == loop) return statistics.Rate;
            }

            return 0.0;
        }

        return new TelemetryData
        {
            LogicRate = RateOf("Logic"),
            RenderRate = RateOf("Render"),
            PhysicsRate = RateOf("Physics")
        };
    }
    
#endif
}