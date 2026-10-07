using System.Diagnostics;
using System.Numerics;

using Bogz.Logging;

using Horizon.Core.Components;
using Horizon.Core.Threading;
using Horizon.Core.Tweening;
using Horizon.Engine;
using Horizon.Rendering;
using Horizon.Rendering.Spriting;
using Horizon.Rendering.UIX;
using Horizon.Rendering.UIX.Components;

using Silk.NET.Input;

namespace Horizon.Testing.Scenes;

/// <summary>
/// How evenly things move on screen. Sprites cross the screen at a steady speed, one moved by the logic and one by
/// the physics, the camera pans along with them if asked to, and a UI marker is tweened back and forth at a steady
/// speed too. Whatever moves at a steady speed ought to move the same distance for the same time between two frames;
/// how far off that it is, frame to frame, is what the meter in the corner shows (and the log, every couple of
/// seconds, for runs nobody watches). A judder that can hardly be seen is a number here.
/// </summary>
public class PacingTestScene : Scene, ITestControls
{
    private static readonly Vector2 DesignSize = new(1600, 900);

    // How fast (in units a second) everything goes, and how far across before it comes back in on the other side
    private const float SPEED = 360.0f;
    private const float SPAN = 1400.0f;
    private const float MARKER_TRACK = 900.0f;

    // How often (in seconds) the meter is read out
    private const double REPORT_EVERY = 2.0;

    public override Camera ActiveCamera { get; protected set; }

    public IReadOnlyList<TestControl> Controls { get; } =
    [
        new("C", "camera follows / stands still"),
        new("F5", "lockstep / decoupled"),
        new("F6", "interpolated / newest tick")
    ];

    private readonly Camera2D camera;
    private readonly UICompositor compositor;
    private readonly SpriteSheetDefinition sprites;
    private readonly TextureAtlas atlas = new(256, 256);

    private Sprite logicRunner = null!, physicsRunner = null!;
    private Label meter = null!;
    private Panel marker = null!;
    private bool cameraFollows;

    private readonly PacingProbe probe;

    // Where the runners were at the end of every tick, for measuring them where a decoupled frame draws them
    private readonly Snapshot<Runners> runners = new();

    private readonly record struct Runners(float Logic, float Physics) : IBlendable<Runners>
    {
        public static Runners Blend(in Runners from, in Runners to, float amount) =>
            new(Interpolate.Linear(from.Logic, to.Logic, amount), Interpolate.Linear(from.Physics, to.Physics, amount));
    }

    public PacingTestScene()
    {
        camera = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize));
        ActiveCamera = camera;

        sprites = SpriteSheetDefinition.Load("Assets/uix/dead_revolver/", "sprites.hor");

        var batch = AddEntity(new SpriteBatch());
        logicRunner = CreateRunner(batch, "heart", 120.0f);
        physicsRunner = CreateRunner(batch, "star", -120.0f);

        compositor = AddComponent(new UICompositor(camera) { DesignSize = DesignSize });
        probe = AddComponent(new PacingProbe(this));
    }

    private Sprite CreateRunner(SpriteBatch batch, string art, float y)
    {
        var sprite = batch.AddEntity(new Sprite(new Vector2(48)));
        sprite.ConfigureAtlas(atlas, sprites, art);
        sprite.Transform.Position = new Vector2(-SPAN / 2.0f, y);
        batch.Add(sprite);
        return sprite;
    }

    public override void PostInit()
    {
        base.PostInit();

        var module = compositor.CreateModule();

        var panel = module.AddComponent(new StackPanel
        {
            Anchor = Origin.TopLeft,
            Position = new Vector2(24, -24),
            Color = new Vector4(0.1f, 0.12f, 0.17f, 0.92f),
            Padding = new UIEdges(16),
            Spacing = 8
        });
        panel.Add(new Label("Frame pacing") { TextScale = 0.3f });
        meter = panel.Add(new Label("measuring...") { Align = Origin.TopLeft, TextScale = 0.2f });

        var track = module.AddComponent(new Panel
        {
            Anchor = Origin.Bottom,
            Position = new Vector2(0, 60),
            Size = new Vector2(MARKER_TRACK, 20),
            Color = new Vector4(0.03f, 0.03f, 0.05f, 1.0f)
        });
        marker = track.Add(new Panel
        {
            Anchor = Origin.Left,
            Size = new Vector2(20),
            Color = new Vector4(0.0f, 0.86f, 1.0f, 1.0f)
        });

        // A linear tween is a steady speed, there and back
        marker.Tweens.Play(
            Tween.To(() => marker.Position.X, x => marker.Position = new Vector2(x, 0), MARKER_TRACK - 20, (MARKER_TRACK - 20) / SPEED)
                .SetEasing(Easing.Linear)
                .SetLoops(-1, LoopMode.PingPong));

        Engine.GL.ClearColor(0.16f, 0.17f, 0.22f, 1.0f);
    }

    private Vector2 Advance(Vector2 position, float dt)
    {
        position.X += SPEED * dt;
        if (position.X > SPAN / 2.0f)
        {
            // Back in on the other side: put there, not moved there, so no frame shows it on its way across
            position.X -= SPAN;
            runners.Break();
        }

        return position;
    }

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);

        logicRunner.Transform.Position = Advance(logicRunner.Transform.Position, dt);

        if (Engine.Input.Keyboard.WasPressed(Key.C))
            cameraFollows = !cameraFollows;

        var window = Engine.WindowManager;
        if (Engine.Input.Keyboard.WasPressed(Key.F5))
            window.Threading = window.Threading == ThreadingMode.Lockstep ? ThreadingMode.Decoupled : ThreadingMode.Lockstep;
        if (Engine.Input.Keyboard.WasPressed(Key.F6))
            window.Presentation = window.Presentation == PresentationMode.Interpolated ? PresentationMode.Latest : PresentationMode.Interpolated;

        camera.Position = cameraFollows
            ? new Vector3(logicRunner.Transform.Position.X, 0.0f, camera.Position.Z)
            : new Vector3(0.0f, 0.0f, camera.Position.Z);

        meter.Text = probe.Summary;
    }

    public override void UpdatePhysics(float dt)
    {
        base.UpdatePhysics(dt);

        physicsRunner.Transform.Position = Advance(physicsRunner.Transform.Position, dt);
    }

    public override void Capture()
    {
        runners.Publish(new Runners(logicRunner.Transform.Position.X, physicsRunner.Transform.Position.X));
        base.Capture();
    }

    /// <summary>
    /// Where the runners are drawn, as the renderer sees them this frame: between the last two ticks when drawn
    /// alongside the simulation (the way the sprite batch blends them), as they are when drawn in turns with it.
    /// Measured on the render thread.
    /// </summary>
    internal float? DrawnX(bool physics)
    {
        if (RenderFrame.Active is { IsDecoupled: true } frame)
        {
            // Across a wrap nothing is moving steadily, there is nothing to measure
            if (!runners.TryGet(frame, out _, out _, out bool continuous) || !continuous)
                return null;

            runners.TryBlend(frame, out Runners shown);
            return physics ? shown.Physics : shown.Logic;
        }

        return (physics ? physicsRunner : logicRunner).Transform.Position.X;
    }

    /// <summary>How the frames are drawn right now, for the meter.</summary>
    internal string Mode
    {
        get
        {
            var window = Engine.WindowManager;
            return window.Threading == ThreadingMode.Lockstep ? "lockstep" : $"decoupled, {(window.Presentation == PresentationMode.Interpolated ? "interpolated" : "newest tick")}";
        }
    }

    /// <summary>
    /// Watches every frame on the render thread: how far the runners got since the last one against how far a
    /// steady speed would have taken them in that time.
    /// </summary>
    internal sealed class PacingProbe : GameComponent
    {
        private readonly PacingTestScene scene;
        private readonly Track logic = new(), physics = new();

        private long lastFrame;
        private double nextReport;
        private int frames;
        private double frameTime;

        private volatile string summary = "measuring...";

        public PacingProbe(PacingTestScene scene) => this.scene = scene;

        /// <summary>The last read out of the meter, for showing.</summary>
        public string Summary => summary;

        public override void Render(float dt)
        {
            long now = Stopwatch.GetTimestamp();
            if (lastFrame != 0)
            {
                // Between the moments the frames were taken (which is what they show), rather than between the moments
                // this happened to be reached in drawing them, which wanders with whatever was drawn before it
                RenderFrame frame = RenderFrame.Active;
                double interval = frame.HasSnapshot && frame.RealDelta > 0.0f ? frame.RealDelta : (now - lastFrame) / (double)Stopwatch.Frequency;
                frames++;
                frameTime += interval;

                logic.Note(scene.DrawnX(false), interval);
                physics.Note(scene.DrawnX(true), interval);
            }
            else
            {
                logic.Note(scene.DrawnX(false), 0.0);
                physics.Note(scene.DrawnX(true), 0.0);
            }

            lastFrame = now;

            double seconds = now / (double)Stopwatch.Frequency;
            if (nextReport == 0.0) nextReport = seconds + REPORT_EVERY;
            if (seconds < nextReport || frames == 0) return;

            nextReport = seconds + REPORT_EVERY;

            string text =
                $"{frames / frameTime:0} fps, {scene.Mode}\n" +
                $"logic runner: {logic.Describe()}\n" +
                $"physics runner: {physics.Describe()}";

            summary = text;
            Log.Info($"[Pacing] {frames / frameTime:0} fps, {scene.Mode} | logic {logic.Describe()} | physics {physics.Describe()}");

            logic.Reset();
            physics.Reset();
            frames = 0;
            frameTime = 0.0;
        }

        /// <summary>
        /// The steps one runner took between frames, against the steps a steady speed takes.
        /// </summary>
        private sealed class Track
        {
            private float last;
            private bool hasLast;
            private int count, standing, doubled;
            private double error, expected;

            public void Note(float? drawn, double interval)
            {
                if (drawn is not { } x)
                {
                    hasLast = false;
                    return;
                }

                if (hasLast && interval > 0.0)
                {
                    double step = x - last;
                    double steady = SPEED * interval;

                    // Came back in on the other side, which is no step at all
                    if (Math.Abs(step) < SPAN / 2.0f)
                    {
                        count++;
                        error += (step - steady) * (step - steady);
                        expected += steady;

                        if (step == 0.0) standing++;
                        else if (step > steady * 1.5) doubled++;
                    }
                }

                last = x;
                hasLast = true;
            }

            /// <summary>
            /// How far off the steps are, on average, as a share of a steady step. 0% is as smooth as it gets.
            /// Also how many frames showed no step at all, and how many a step and a half or more.
            /// </summary>
            public string Describe()
            {
                if (count == 0) return "no frames";

                double rms = Math.Sqrt(error / count);
                double mean = expected / count;
                return $"off by {100.0 * rms / mean:0.0}%, {100.0 * standing / count:0.0}% frames still, {100.0 * doubled / count:0.0}% double steps";
            }

            public void Reset()
            {
                count = standing = doubled = 0;
                error = expected = 0.0;
            }
        }
    }
}
