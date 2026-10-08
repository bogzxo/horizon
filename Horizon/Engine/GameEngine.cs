using System.Numerics;
using System.Runtime.InteropServices;

using Horizon.Logging;

using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Engine.Components;
using Horizon.Graphics;
using Horizon.Input;


using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

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
    /// <summary>
    /// A copy of the game engines initial configuration.
    /// </summary>
    public GameEngineConfiguration Configuration { get; init; }

    /// <summary>
    /// The GPU as the engine draws with it, see <see cref="Horizon.Graphics.GraphicsDevice"/>. Everything that draws goes through this.
    /// </summary>
    public Horizon.Graphics.GraphicsDevice Graphics => Horizon.Graphics.GraphicsDevice.Current;

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
    /// Kept in double precision: in single precision a clock in seconds can't tell milliseconds apart any more after a few hours.
    /// </summary>
    public double TotalTime { get; private set; }

    /// <summary>
    /// How long (in seconds) the game has been updated for. It stands still whenever the updates do.
    /// </summary>
    public double Runtime { get; private set; }

    public EngineEventHandler EventManager { get; }
    public ObjectManager ObjectManager { get; }
    public WindowManager WindowManager { get; }
    public SceneManager SceneManager { get; }

    /// <summary>
    /// The keyboard, the mouse and the gamepads.
    /// </summary>
    public InputManager Input { get; }

    /// <summary>
    /// The scene that is on screen, null before the first one has been set.
    /// </summary>
    public Scene? Scene => SceneManager.CurrentInstance;

    /// <summary>
    /// How big what is drawn into is, in pixels.
    /// </summary>
    public Vector2 ViewportSize => WindowManager.ViewportSize;

    // Set this in the environment to "some/file.png@3" and a screenshot is saved three seconds in, for runs nobody is
    // watching (a headless test box, say). Without the @ it's two seconds
    private const string SCREENSHOT_VARIABLE = "HORIZON_SCREENSHOT";

    private string? scheduledScreenshot;
    private double scheduledScreenshotAt, scheduledScreenshotEvery;
    private int scheduledScreenshotsLeft, scheduledScreenshotsTaken;
    private string? requestedScreenshot;

    /// <summary>Where screenshots taken with <see cref="ScreenshotKey"/> go. Made if it isn't there.</summary>
    public string ScreenshotDirectory { get; set; } = "screenshots";

    /// <summary>The key that saves a screenshot of the window, null for none.</summary>
    public Silk.NET.Input.Key? ScreenshotKey { get; set; } = Silk.NET.Input.Key.F12;

    /// <summary>
    /// How long (in milliseconds) the GPU spent on the last frame it finished, as measured by a timer query around
    /// everything the engine drew. Zero until the first frame has been timed, and where the driver doesn't say.
    /// </summary>
    public double GpuFrameMs => Graphics.GpuFrameMilliseconds;

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
        SceneManager = AddEntity<SceneManager>();

        // The window manager bootstraps the lot. It calls Initialize(), Render(), UpdateState() and UpdatePhysics()
        WindowManager = AddComponent<WindowManager>(new(Configuration.WindowConfiguration));

        if (Environment.GetEnvironmentVariable(SCREENSHOT_VARIABLE) is { Length: > 0 } wanted)
        {
            // file.png@3 for one at three seconds, file.png@3+8x0.5 for eight of them half a second apart from
            // three seconds on, numbered before the extension, which is how a flicker gets caught in the act
            int at = wanted.LastIndexOf('@');
            scheduledScreenshot = at > 0 ? wanted[..at] : wanted;
            string when = at > 0 ? wanted[(at + 1)..] : "2";
            int plus = when.IndexOf('+');
            string start = plus > 0 ? when[..plus] : when;
            scheduledScreenshotAt = double.TryParse(start, System.Globalization.CultureInfo.InvariantCulture, out double seconds) ? seconds : 2.0;
            if (plus > 0 && when[(plus + 1)..].Split('x') is { Length: 2 } series &&
                int.TryParse(series[0], out scheduledScreenshotsLeft) &&
                double.TryParse(series[1], System.Globalization.CultureInfo.InvariantCulture, out scheduledScreenshotEvery))
            {
                scheduledScreenshotsLeft = Math.Max(scheduledScreenshotsLeft, 1);
            }
            else
            {
                scheduledScreenshotsLeft = 1;
            }
        }
    }

    /// <summary>
    /// Saves what the window shows at the end of the next frame as a PNG. From any thread, the file is written off
    /// the render thread once the pixels are read.
    /// </summary>
    public void CaptureScreenshot(string path) => Interlocked.Exchange(ref requestedScreenshot, path);

    /// <summary>
    /// Helper method to read the frame back and write it out, at the end of a frame on the render thread.
    /// </summary>
    private void TakeScreenshot(string path)
    {
        uint width = (uint)WindowManager.ViewportSize.X, height = (uint)WindowManager.ViewportSize.Y;
        if (width == 0 || height == 0) return;

        var pixels = new byte[width * height * 4];
        Graphics.BindWindow();
        Graphics.ReadPixels(0, 0, width, height, pixels);

        // Encoding a PNG takes a while, which is not the render thread's problem
        Task.Run(() =>
        {
            try
            {
                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                using var image = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(pixels, (int)width, (int)height);

                // The pixels come out bottom row first, a picture starts at the top
                image.Mutate(context => context.Flip(SixLabors.ImageSharp.Processing.FlipMode.Vertical));
                image.SaveAsPng(path);
                Log.Info($"[Engine] Screenshot saved to {path}.");
            }
            catch (Exception e)
            {
                Log.Error($"[Engine] The screenshot couldn't be saved to {path}: {e.Message}");
            }
        });
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

        Graphics.OnDebugMessage(OnDebugMessage);
    }

    /// <summary>
    /// Called by the graphics backend when it has something to say about what it was asked to do.
    /// </summary>
    private static void OnDebugMessage(string message, DebugLevel severity, int id)
    {
        if (severity == DebugLevel.Note)
            return;

        LogLevel level = severity switch
        {
            DebugLevel.High => LogLevel.Error,
            DebugLevel.Medium => LogLevel.Warning,
            _ => LogLevel.Info
        };

        Log.Write(level, $"{message} ({severity}, {id})");
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

        if (ScreenshotKey is { } key && Input.Keyboard.WasPressed(key))
            CaptureScreenshot(Path.Combine(ScreenshotDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.png"));

        EventManager.PreState?.Invoke(dt);
        base.UpdateState(dt);
        EventManager.PostState?.Invoke(dt);
    }

    public override void Render(float dt)
    {
        TotalTime += dt;

        EventManager.PreRender?.Invoke(dt);

        var graphics = Graphics;
        graphics.SetViewport(0, 0, (uint)WindowManager.ViewportSize.X, (uint)WindowManager.ViewportSize.Y);
        graphics.Clear(Horizon.Graphics.ClearTargets.All);

        // The camera every shader draws through, bound for the frame. Whoever draws says which camera, see CameraBlock.Use
        Horizon.Rendering.CameraBlock.BeginFrame(WindowManager.ViewportSize, dt);

        // Whatever is drawn outside of a scene makes what it makes on the GPU for everybody, the same as when it's
        // set up (see EntityLifecycle.Scope above): an overlay on the engine that makes its layer the first time it's
        // drawn mustn't have it count as a leftover of whichever scene was on then. Scenes draw in their own scope
        using (Horizon.Content.AssetScope.EnterGlobal())
            base.Render(dt);

        Horizon.Rendering.CameraBlock.EndFrame();

        if (scheduledScreenshot is { } scheduled && TotalTime >= scheduledScreenshotAt)
        {
            if (scheduledScreenshotsLeft > 1)
            {
                // One of a series, numbered, the next one is due a little later
                string numbered = Path.Combine(Path.GetDirectoryName(scheduled) ?? string.Empty, $"{Path.GetFileNameWithoutExtension(scheduled)}_{scheduledScreenshotsTaken:000}{Path.GetExtension(scheduled)}");
                scheduledScreenshotsTaken++;
                scheduledScreenshotsLeft--;
                scheduledScreenshotAt += scheduledScreenshotEvery;
                CaptureScreenshot(numbered);
            }
            else
            {
                scheduledScreenshot = null;
                CaptureScreenshot(scheduledScreenshotsTaken > 0 ? Path.Combine(Path.GetDirectoryName(scheduled) ?? string.Empty, $"{Path.GetFileNameWithoutExtension(scheduled)}_{scheduledScreenshotsTaken:000}{Path.GetExtension(scheduled)}") : scheduled);
            }
        }

        if (Interlocked.Exchange(ref requestedScreenshot, null) is { } path)
            TakeScreenshot(path);

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

}
