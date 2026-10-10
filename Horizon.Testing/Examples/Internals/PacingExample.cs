using System.Diagnostics;
using System.Numerics;

using Horizon.Logging;

using Horizon.Core.Components;
using Horizon.Core.Threading;
using Horizon.Core.Tweening;
using Horizon.Engine;
using Horizon.Rendering;
using Horizon.Rendering.Spriting;
using Horizon.UI;
using Horizon.UI.Components;

using Silk.NET.Input;

namespace Horizon.Testing.Examples.Internals;

/// <summary>
/// How evenly things move on screen. Sprites cross the screen at a steady speed, one moved by the logic and one by
/// the physics, the camera pans along with them if asked to, and a UI marker is tweened back and forth at a steady
/// speed too. Whatever moves at a steady speed ought to move the same distance for the same time between two frames;
/// how far off that it is, in frames (a step that is 0.1 of a frame off went a tenth further or less far than it
/// should have), is what the meter in the corner shows (and the log, every couple of seconds, for runs nobody
/// watches). A judder that can hardly be seen is a number here.
/// <para>
/// The orange square is a piece of UI the scene puts on the heart every update, the way a name tag or a health bar
/// is put on whoever it belongs to. Drawn at the same moment as the heart it sits dead on it, and how many frames
/// it trails behind is the last line of the meter. T has a number in the UI change every tick, which is what a
/// HUD with a clock in it does all day and used to be enough to have the whole UI drawn in steps. The pink
/// square by the mouse is a <see cref="UICursor"/>, wave the mouse about and see whether it keeps up.
/// </para>
/// </summary>
public class PacingExample : Scene, ITestControls
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
        new("T", "a number in the UI that changes every tick"),
        new("F6", "interpolated / newest tick")
    ];

    private readonly Camera2D camera;
    private readonly UICompositor compositor;
    private readonly SpriteSheetDefinition sprites;
    private readonly TextureAtlas atlas = new(256, 256);

    private Sprite logicRunner = null!, physicsRunner = null!;
    private Label meter = null!, ticking = null!;
    private Panel marker = null!, follower = null!;
    private bool cameraFollows;
    private bool tickingOn = true;
    private long ticks;

    // How far over the heart its tag sits, so both can be seen
    private const float FOLLOWER_LIFT = 44.0f;

    private readonly PacingProbe probe;

    // Where the runners were at the end of every tick, for measuring them where a decoupled frame draws them
    private readonly Snapshot<Runners> runners = new();

    private readonly record struct Runners(float Logic, float Physics) : IBlendable<Runners>
    {
        public static Runners Blend(in Runners from, in Runners to, float amount) =>
            new(Interpolate.Linear(from.Logic, to.Logic, amount), Interpolate.Linear(from.Physics, to.Physics, amount));
    }

    public PacingExample()
    {
        camera = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize));
        ActiveCamera = camera;

        sprites = SpriteSheetDefinition.Load("Assets/uix/dead_revolver/", "sprites.hor");

        var batch = AddEntity(new SpriteBatch());
        logicRunner = CreateRunner(batch, "heart", 120.0f);
        physicsRunner = CreateRunner(batch, "star", -120.0f);

        compositor = AddComponent(new UICompositor(camera) { DesignSize = DesignSize });

        // A cursor the UI draws, to hold against the real one. It is put where the mouse is as every frame is
        // drawn rather than painted with the rest of the UI a tick ago, see UICursor
        compositor.Cursor = new UICursor { Size = new Vector2(10.0f), Hotspot = new Vector2(-14.0f, -14.0f), Tint = new Vector4(1.0f, 0.3f, 0.5f, 1.0f) };
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
        ticking = panel.Add(new Label { Align = Origin.TopLeft, Anchor = Origin.Left, TextScale = 0.2f });

        // The tag of the heart, put where the heart is by the scene every update, see UpdateState
        follower = module.AddComponent(new Panel
        {
            Anchor = Origin.Center,
            Size = new Vector2(20),
            Color = FollowerTint
        });

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

        Engine.Graphics.ClearColor = new Vector4(0.16f, 0.17f, 0.22f, 1.0f);
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
        if (Engine.Input.Keyboard.WasPressed(Key.F6))
            window.Presentation = window.Presentation == PresentationMode.Interpolated ? PresentationMode.Latest : PresentationMode.Interpolated;

        camera.Position = cameraFollows
            ? new Vector3(logicRunner.Transform.Position.X, 0.0f, camera.Position.Z)
            : new Vector3(0.0f, 0.0f, camera.Position.Z);

        if (Engine.Input.Keyboard.WasPressed(Key.T))
            tickingOn = !tickingOn;

        // What every game does with a name tag, after it has moved whoever the tag belongs to. The UI is laid out
        // against what the camera sees, in its own units
        float scale = compositor.UIScale > 0.0f ? compositor.UIScale : 1.0f;
        follower.Position = new Vector2(logicRunner.Transform.Position.X - camera.Position.X, logicRunner.Transform.Position.Y - camera.Position.Y + FOLLOWER_LIFT) / scale;

        ticks++;
        ticking.Text = tickingOn ? $"tick {ticks}" : "tick (standing still, T)";
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
    /// alongside the simulation (the way the sprite batch blends them), as they are with it standing still.
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

    // The colour of the marker on its track, which is how its quad is found among the ones the UI draws
    private static readonly uint MarkerColor = SpriteItem.PackColor(new Vector4(0.0f, 0.86f, 1.0f, 1.0f));

    /// <summary>
    /// Where the UI draws the marker this frame, as uploaded to be drawn: the real thing, blended or not. Render thread,
    /// after the UI was drawn.
    /// </summary>
    internal float? DrawnMarker()
    {
        foreach (SpriteItem item in compositor.Drawn)
        {
            if (item.Color == MarkerColor && item.AxisX.X > 0.0f)
                return item.Origin.X;
        }

        return null;
    }

    // The colour of the tag, which is how its quad is found
    private static readonly Vector4 FollowerTint = new(1.0f, 0.55f, 0.1f, 1.0f);
    private static readonly uint FollowerColor = SpriteItem.PackColor(FollowerTint);

    /// <summary>Where the middle of the heart's tag is drawn this frame, as uploaded. Render thread, after the UI was drawn.</summary>
    internal float? DrawnFollower()
    {
        foreach (SpriteItem item in compositor.Drawn)
        {
            if (item.Color == FollowerColor && item.AxisX.X > 0.0f)
                return item.Origin.X + item.AxisX.X * 0.5f;
        }

        return null;
    }

    // Where the camera is as the frame shows it, the UI is drawn against what it sees
    internal float CameraX => camera.Position.X;

    /// <summary>How the frames are drawn right now, for the meter.</summary>
    internal string Mode
    {
        get
        {
            return Engine.WindowManager.Presentation == PresentationMode.Interpolated ? "interpolated" : "newest tick";
        }
    }

    /// <summary>
    /// Watches every frame on the render thread: how far the runners got since the last one against how far a
    /// steady speed would have taken them in that time.
    /// </summary>
    internal sealed class PacingProbe : GameComponent
    {
        private readonly PacingExample scene;
        private readonly Track logic = new(), physics = new(), marker = new(backAndForth: true);

        // How far the tag was behind the heart, added up over the frames it was measured in
        private double trailing;
        private int trailed;

        private long lastFrame;
        private double nextReport;
        private int frames;
        private double frameTime;

        private volatile string summary = "measuring...";

        public PacingProbe(PacingExample scene) => this.scene = scene;

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
                marker.Note(scene.DrawnMarker(), interval);

                // The tag against the heart it is meant to be on, in the same frame. Not while either is on its way
                // back in on the other side
                if (scene.DrawnX(false) is { } heart && scene.DrawnFollower() is { } tagAt && Math.Abs(heart - scene.CameraX - tagAt) < SPAN / 4.0f)
                {
                    trailing += heart - scene.CameraX - tagAt;
                    trailed++;
                }
            }
            else
            {
                logic.Note(scene.DrawnX(false), 0.0);
                physics.Note(scene.DrawnX(true), 0.0);
                marker.Note(scene.DrawnMarker(), 0.0);
            }

            lastFrame = now;

            double seconds = now / (double)Stopwatch.Frequency;
            if (nextReport == 0.0) nextReport = seconds + REPORT_EVERY;
            if (seconds < nextReport || frames == 0) return;

            nextReport = seconds + REPORT_EVERY;

            // What a steady frame moves something by, which is what everything here is counted in
            double frameStep = SPEED * frameTime / frames;
            double tick = Engine.WindowManager.Snapshots.TickInterval;
            string tag = trailed == 0
                ? "not seen"
                : $"{trailing / trailed / frameStep:0.00} frames behind the heart ({trailing / trailed:0.00} px, {(tick > 0.0 ? trailing / trailed / (SPEED * tick) : 0.0):0.00} ticks)";

            string text =
                $"{frames / frameTime:0} fps, {scene.Mode}, a frame is {frameStep:0.00} px\n" +
                $"logic runner: {logic.Describe()}\n" +
                $"physics runner: {physics.Describe()}\n" +
                $"UI marker: {marker.Describe()}\n" +
                $"UI tag: {tag}";

            summary = text;
            Log.Info($"[Pacing] {frames / frameTime:0} fps, {scene.Mode} | logic {logic.Describe()} | physics {physics.Describe()} | UI {marker.Describe()} | tag {tag}");

            logic.Reset();
            physics.Reset();
            marker.Reset();
            trailing = 0.0;
            trailed = 0;
            frames = 0;
            frameTime = 0.0;
        }

        /// <summary>
        /// The steps one runner took between frames, against the steps a steady speed takes.
        /// </summary>
        /// <param name="backAndForth">For something that turns round at the ends: the steps are measured either way, and the frames it turns round in not at all.</param>
        private sealed class Track(bool backAndForth = false)
        {
            private float last;
            private bool hasLast;
            private double lastStep;
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

                    if (backAndForth)
                    {
                        // Turned round (or stood at the end) in this frame: nothing steady about it
                        bool turned = Math.Sign(step) != Math.Sign(lastStep);
                        lastStep = step;
                        last = x;
                        if (turned || step == 0.0) return;

                        step = Math.Abs(step);
                    }

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
            /// How far off the steps are, on average (root mean square), in frames, a steady frame's step being 1, and
            /// in units of the world (a pixel each at this zoom). 0 is as smooth as it gets, 1 is a step that was a whole
            /// frame out, which is a frame that stood still or one that went twice as far. The frames are what to watch
            /// at a refresh rate; uncapped the frames are so short that a step a few hundredths of a pixel out is already
            /// a fair part of one, so look at the pixels too. Also how many frames showed no step at all, and how many a
            /// step and a half or more.
            /// </summary>
            public string Describe()
            {
                if (count == 0) return "no frames";

                double rms = Math.Sqrt(error / count);
                double mean = expected / count;
                return $"off by {rms / mean:0.000} frames ({rms:0.000} px), {standing} of {count} frames still, {doubled} double steps";
            }

            public void Reset()
            {
                count = standing = doubled = 0;
                error = expected = 0.0;
            }
        }
    }
}
