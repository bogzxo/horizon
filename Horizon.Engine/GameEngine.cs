using System.Numerics;
using System.Runtime.InteropServices;

using Bogz.Logging;

using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Engine.Components;
using Horizon.Engine.Debugging.Debuggers;
using Horizon.Engine.Webhost;
using Horizon.Engine.WebHost;
using Horizon.Input;
using Horizon.OpenGL.Managers;

using Silk.NET.OpenGL;

namespace Horizon.Engine;

/// <summary>
/// The engine. It opens the window, keeps the scene that is on screen updated and drawn, and has everything a game reaches for.
/// A whole game starts like this:
/// <code>
/// using var engine = new GameEngine(WindowManagerConfiguration.Default1600x900 with { WindowTitle = "My game" });
/// engine.Run&lt;MainMenuScene&gt;();
/// </code>
/// Everything in a scene gets at it through <see cref="GameObject.Engine"/>, everything else through <see cref="Instance"/>.
/// </summary>
public class GameEngine : Entity
{
    // Things the driver likes to go on about that nobody needs to read. How its buffers are doing, mostly
    private const int NOTE_BUFFER_DETAILS = 131185;
    private const int NOTE_INVALID_ENUM = 1280;

    /// <summary>
    /// A copy of the game engines initial configuration.
    /// </summary>
    public GameEngineConfiguration Configuration { get; init; }

    public GL GL => WindowManager.GL;

    public static GameEngine Instance { get; private set; } = null!;

    /// <summary>
    /// The camera of the scene that is on screen, or the one of the engine if the scene hasn't got one.
    /// </summary>
    public Camera ActiveCamera => SceneManager.CurrentInstance?.ActiveCamera ?? DefaultCamera;

    /// <summary>
    /// The camera that is used while no scene says otherwise. It looks at the middle of the world, a unit a pixel.
    /// </summary>
    public Camera DefaultCamera { get; private set; } = null!;

    /// <summary>
    /// How long (in seconds) frames have been drawn for. This is the clock for anything that only animates what is seen.
    /// </summary>
    public float TotalTime { get; private set; }

    /// <summary>
    /// How long (in seconds) the game has been updated for. It stands still whenever the updates do.
    /// </summary>
    public float Runtime { get; private set; }

    public EngineEventHandler EventManager { get; }
    public ObjectManager ObjectManager { get; }
    public WindowManager WindowManager { get; }
    public SceneManager SceneManager { get; }

    /// <summary>
    /// The keyboard, the mouse and the gamepads.
    /// </summary>
    public InputManager Input { get; }

    /// <summary>
    /// The console of the engine. It is a HIDL runtime that whatever talks to the running game from outside (the web
    /// dashboard) has its commands run by. It has no window of its own.
    /// </summary>
    public DeveloperConsole Console { get; }

    /// <summary>
    /// The scene that is on screen, null before the first one has been set.
    /// </summary>
    public Scene? Scene => SceneManager.CurrentInstance;

    /// <summary>
    /// How big what is drawn into is, in pixels.
    /// </summary>
    public Vector2 ViewportSize => WindowManager.ViewportSize;

    // Kept here for as long as the driver may call it, the garbage collector doesn't know that it does
    private DebugProc? debugProc;

    public GameEngine()
        : this(GameEngineConfiguration.Default) { }

    /// <summary>
    /// An engine with a window made the way a configuration says and everything else left as it comes.
    /// </summary>
    public GameEngine(in WindowManagerConfiguration window)
        : this(new GameEngineConfiguration { WindowConfiguration = window }) { }

    public GameEngine(in GameEngineConfiguration engineConfiguration)
    {
        Name = "Engine";

        Instance = GameObject.Engine = this;
        Configuration = engineConfiguration;

        Enabled = true;

        // What an entity makes on the GPU while it is set up belongs to the scene it is in, and to nobody if
        // it isn't in one. See Scene.Assets
        EntityLifecycle.Scope = static entity =>
        {
            for (Entity? at = entity; at is not null; at = at.Parent)
            {
                if (at is Scene scene)
                    return scene.Assets.Enter();
            }

            return Horizon.Content.AssetScope.EnterGlobal();
        };

        // In the order they get their turns. The input comes before anything that reads it
        EventManager = AddComponent<EngineEventHandler>();
        ObjectManager = AddComponent<ObjectManager>();
        Input = AddComponent<InputManager>();
        Console = AddComponent<DeveloperConsole>();
        SceneManager = AddEntity<SceneManager>();

        // The window manager bootstraps the lot. It calls Initialize(), Render(), UpdateState() and UpdatePhysics()
        WindowManager = AddComponent<WindowManager>(new(Configuration.WindowConfiguration));
    }

    /// <summary>
    /// Has a scene take over from the one that is on screen, the way the scene manager is set to do it. From any thread.
    /// </summary>
    public void SetScene(Scene scene)
    {
        SceneManager.SetScene(scene);
    }

    /// <summary>
    /// Changes the scene through a transition of its own (or with a hard cut, for null), whatever the scene manager is set to.
    /// </summary>
    public void SetScene(Scene scene, SceneTransition? transition)
    {
        SceneManager.SetScene(scene, transition);
    }

    /// <summary>
    /// Has a new scene of a kind take over from the one that is on screen.
    /// </summary>
    public void SetScene<TScene>() where TScene : Scene, new()
    {
        SceneManager.SetScene(new TScene());
    }

    public override void Initialize()
    {
        base.Initialize();
        DefaultCamera = AddEntity(new Camera2D(WindowManager.ViewportSize));

        unsafe
        {
            GL.Enable(EnableCap.DebugOutput);
            GL.DebugMessageCallback(debugProc = OnDebugMessage, null);
        }
    }

    /// <summary>
    /// Called by the driver when it has something to say about what it was asked to do.
    /// </summary>
    private void OnDebugMessage(GLEnum source, GLEnum type, int id, GLEnum severity, int length, nint message, nint userParam)
    {
        if (id is NOTE_BUFFER_DETAILS or NOTE_INVALID_ENUM || severity == GLEnum.DebugSeverityNotification)
            return;

        LogLevel level = severity switch
        {
            GLEnum.DebugSeverityHigh => LogLevel.Error,
            GLEnum.DebugSeverityMedium => LogLevel.Warning,
            _ => LogLevel.Info
        };

        Log.Write(level, $"[{source}] [{severity}] [{type}] [{id}] {Marshal.PtrToStringAnsi(message)}");
    }

    public override void UpdatePhysics(float dt)
    {
        EventManager.PrePhysics?.Invoke(dt);
        base.UpdatePhysics(dt);
        EventManager.PostPhysics?.Invoke(dt);
    }

    public override void UpdateState(float dt)
    {
        Runtime += dt;

        EventManager.PreState?.Invoke(dt);
        base.UpdateState(dt);
        EventManager.PostState?.Invoke(dt);
    }

    public override void Render(float dt)
    {
        TotalTime += dt;

        EventManager.PreRender?.Invoke(dt);

        GL.Viewport(0, 0, (uint)WindowManager.ViewportSize.X, (uint)WindowManager.ViewportSize.Y);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);

        base.Render(dt);

        EventManager.PostRender?.Invoke(dt);
    }

    protected override void DisposeOther()
    {
        Logger.Dispose();
    }

    /// <summary>
    /// Opens the window and runs the game until it is closed.
    /// </summary>
    public virtual void Run() => WindowManager.Run();

    /// <summary>
    /// Opens the window with a scene on screen and runs the game until it is closed.
    /// </summary>
    /// <param name="transition">How the scene comes in, null for a hard cut.</param>
    public void Run(Scene scene, SceneTransition? transition = null)
    {
        SceneManager.SetScene(scene, transition ?? SceneManager.Transition);
        Run();
    }

    /// <summary>
    /// Opens the window with a new scene of a kind on screen and runs the game until it is closed.
    /// </summary>
    public void Run<TScene>() where TScene : Scene, new() => Run(new TScene());

    /// <summary>
    /// Closes the window, which is the end of <see cref="Run()"/>. From any thread.
    /// </summary>
    public void Exit() => WindowManager.Close();

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
