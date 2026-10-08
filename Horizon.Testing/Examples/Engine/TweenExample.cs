using System;
using System.Numerics;

using Horizon.Core.Tweening;
using Horizon.Engine;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

using Silk.NET.Input;

namespace Horizon.Testing.Examples.Engine;

/// <summary>
/// The tweens of the engine. On the left a marker per easing goes back and forth along a track, which is the
/// quickest way to see what each of them does; on the right is how the checks went that are made as the scene
/// starts: values, delays, loops, sequences and the rest, each on a tween that is moved along by hand so
/// nothing about them depends on how fast the test happens to run.
/// </summary>
public class TweenExample : Scene, ITestControls
{
    private static readonly Vector2 DesignSize = new(1600, 900);

    private static readonly Easing[] Shown =
    [
        Easing.Linear, Easing.InQuad, Easing.OutQuad, Easing.InOutCubic, Easing.OutExpo, Easing.InOutCirc,
        Easing.OutBack, Easing.InOutBack, Easing.OutElastic, Easing.OutBounce
    ];

    private const float TRACK = 360;
    private const float MARKER = 16;
    private const float TRAVEL_TIME = 1.4f;

    public override Camera ActiveCamera { get; protected set; }

    // Listed on screen by the test host
    public IReadOnlyList<TestControl> Controls { get; } =
    [
        new("Space", "pop the panels in again"),
        new("P", "pause the markers")
    ];

    private readonly UICompositor _compositor;
    private readonly List<Tween> _travels = [];
    private StackPanel _tracks = null!, _results = null!;
    private bool _paused;

    public TweenExample()
    {
        var cam = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize));
        ActiveCamera = cam;

        _compositor = AddComponent(new UICompositor(cam) { DesignSize = DesignSize });
    }

    public override void PostInit()
    {
        base.PostInit();

        BuildTracks();
        BuildResults(RunChecks());

        Engine.GL.ClearColor(0.22f, 0.27f, 0.36f, 1.0f);
    }

    private void BuildTracks()
    {
        var module = _compositor.CreateModule();
        module.Position = new Vector2(-400, 0);

        _tracks = module.AddComponent(new StackPanel
        {
            Color = new Vector4(0.1f, 0.12f, 0.17f, 0.92f),
            Padding = new UIEdges(20),
            Spacing = 10
        });
        _tracks.Add(new Label("Easings, there and back") { TextScale = 0.3f });

        foreach (Easing easing in Shown)
        {
            var row = _tracks.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 14 });
            row.Add(new Label(easing.ToString()) { Size = new Vector2(170, 0), Align = Origin.Left, TextScale = 0.22f });

            var track = row.Add(new Panel { Size = new Vector2(TRACK, MARKER), Color = new Vector4(0.03f, 0.03f, 0.05f, 1.0f) });
            var marker = track.Add(new Panel
            {
                Anchor = Origin.Left,
                Size = new Vector2(MARKER),
                Color = new Vector4(0.0f, 0.86f, 1.0f, 1.0f)
            });

            // The marker's own tweens, which the UI moves along for as long as the marker is in it.
            _travels.Add(marker.Tweens.Play(
                Tween.To(() => marker.Position.X, x => marker.Position = new Vector2(x, 0), TRACK - MARKER, TRAVEL_TIME)
                    .SetEasing(easing)
                    .SetLoops(-1, LoopMode.PingPong)));
        }

        _tracks.PopIn(0.5f);
    }

    private void BuildResults(TestChecks checks)
    {
        var module = _compositor.CreateModule();
        module.Position = new Vector2(400, 0);

        _results = module.AddComponent(new StackPanel
        {
            Color = new Vector4(0.1f, 0.12f, 0.17f, 0.92f),
            Padding = new UIEdges(20),
            Spacing = 10
        });
        _results.Add(new Label("Checked as the scene started") { TextScale = 0.3f });
        _results.Add(new Label(checks.Describe())
        {
            Align = Origin.TopLeft,
            TextScale = 0.19f,
            Color = checks.Passed == checks.Count ? new Vector4(0.6f, 0.95f, 0.65f, 1.0f) : new Vector4(1.0f, 0.5f, 0.45f, 1.0f)
        });

        _results.SlideIn(new Vector2(300, 0), 0.5f, 0.15f);
    }

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);

        var keyboard = Engine.Input.Keyboard;

        if (keyboard.WasPressed(Key.Space))
        {
            _tracks.PopIn(0.5f);
            _results.SlideIn(new Vector2(300, 0), 0.5f, 0.15f);
        }

        if (keyboard.WasPressed(Key.P))
        {
            _paused = !_paused;
            foreach (Tween travel in _travels)
            {
                if (_paused) travel.Pause(); else travel.Play();
            }
        }
    }

    /// <summary>
    /// Everything a tween promises, each on a context of its own that is ticked by hand.
    /// </summary>
    private static TestChecks RunChecks()
    {
        var checks = new TestChecks("Tween test");
        bool Near(float a, float b) => MathF.Abs(a - b) < 0.001f;

        checks.Check("a value gets to where it is going in the time it is given", () =>
        {
            var context = new TweenContext();
            float value = 0;
            Tween tween = context.Play(Tween.To(() => value, x => value = x, 10, 1));

            context.Tick(0.5f);
            bool halfway = Near(value, 5) && !tween.IsCompleted;

            context.Tick(0.5f);
            return halfway && value == 10 && tween.IsCompleted && context.Count == 0;
        });

        checks.Check("an easing bends the way there, not where it ends", () =>
        {
            var context = new TweenContext();
            float value = 0;
            context.Play(Tween.To(() => value, x => value = x, 10, 1).SetEasing(Easing.OutQuad));

            context.Tick(0.5f);
            bool ahead = Near(value, 7.5f);

            context.Tick(0.5f);
            return ahead && value == 10 && Near(Ease.Apply(Easing.InQuad, 0.5f), 0.25f);
        });

        checks.Check("every easing starts at nothing and ends at all of it", () =>
            Enum.GetValues<Easing>().All(easing => Near(Ease.Apply(easing, 0), 0) && Near(Ease.Apply(easing, 1), 1)));

        checks.Check("a delay holds the tween back, and what is left of an update after it counts", () =>
        {
            var context = new TweenContext();
            float value = 0;
            context.Play(Tween.To(() => value, x => value = x, 10, 1).SetDelay(0.5f));

            context.Tick(0.4f);
            bool waiting = value == 0;

            context.Tick(0.6f);
            return waiting && Near(value, 5);
        });

        checks.Check("a tween that loops is over after its last time round", () =>
        {
            var context = new TweenContext();
            float value = 0;
            int rounds = 0;
            Tween tween = context.Play(Tween.To(() => value, x => value = x, 10, 1).SetLoops(3).OnLoop(_ => rounds++));

            context.Tick(1.0f);
            context.Tick(1.0f);
            bool running = !tween.IsCompleted;

            context.Tick(0.5f);
            bool again = Near(value, 5);

            context.Tick(0.5f);
            return running && again && tween.IsCompleted && value == 10 && rounds >= 2;
        });

        checks.Check("back and forth ends where it started", () =>
        {
            var context = new TweenContext();
            float value = 0;
            Tween tween = context.Play(Tween.To(() => value, x => value = x, 10, 1).SetLoops(2, LoopMode.PingPong));

            context.Tick(1.0f);
            bool there = Near(value, 10);

            context.Tick(0.5f);
            bool onTheWayBack = Near(value, 5);

            context.Tick(0.5f);
            return there && onTheWayBack && Near(value, 0) && tween.IsCompleted;
        });

        checks.Check("a time scale makes a tween run faster", () =>
        {
            var context = new TweenContext();
            float value = 0;
            context.Play(Tween.To(() => value, x => value = x, 10, 1).SetTimeScale(2.0f));

            context.Tick(0.25f);
            return Near(value, 5);
        });

        checks.Check("a sequence plays its steps in order, joined ones together", () =>
        {
            var context = new TweenContext();
            float a = 0, b = 0, c = 0, bAtCallback = -1;

            Tween sequence = context.Play(Tween.Sequence()
                .Append(Tween.To(() => a, x => a = x, 1, 0.5f))
                .AppendCallback(() => bAtCallback = b)
                .Append(Tween.To(() => b, x => b = x, 1, 0.5f))
                .Join(Tween.To(() => c, x => c = x, 1, 0.5f))
                .Build());

            context.Tick(0.25f);
            bool firstOnly = Near(a, 0.5f) && b == 0 && c == 0;

            context.Tick(0.5f);
            bool secondAndThird = a == 1 && Near(b, 0.5f) && Near(c, 0.5f);

            context.Tick(0.25f);
            return firstOnly && secondAndThird && bAtCallback == 0 && b == 1 && c == 1 && sequence.IsCompleted && Near(sequence.Duration, 1.0f);
        });

        checks.Check("killing leaves things where they are, completing puts them at the end", () =>
        {
            var context = new TweenContext();
            float killed = 0, completed = 0;
            bool heardKill = false, heardComplete = false;

            Tween first = context.Play(Tween.To(() => killed, x => killed = x, 10, 1).OnKill(() => heardKill = true));
            Tween second = context.Play(Tween.To(() => completed, x => completed = x, 10, 1).OnComplete(() => heardComplete = true));

            context.Tick(0.3f);
            first.Kill();
            second.Complete();
            context.Tick(0.3f);

            return Near(killed, 3) && first.IsKilled && heardKill && completed == 10 && second.IsCompleted && heardComplete && context.Count == 0;
        });

        checks.Check("a paused tween waits to be played again", () =>
        {
            var context = new TweenContext();
            float value = 0;
            Tween tween = context.Play(Tween.To(() => value, x => value = x, 10, 1));

            context.Tick(0.2f);
            tween.Pause();
            context.Tick(0.5f);
            bool held = Near(value, 2);

            tween.Play();
            context.Tick(0.3f);
            return held && Near(value, 5);
        });

        checks.Check("a second tween on the same channel takes over from the first", () =>
        {
            var context = new TweenContext();
            float value = 0;

            Tween first = context.Play(Tween.To(() => value, x => value = x, 10, 1), "position");
            Tween other = context.Play(Tween.To(() => value, x => value = x, 10, 1), "something else");
            context.Tick(0.2f);

            Tween second = context.Play(Tween.To(() => value, x => value = x, -10, 1), "position");
            return first.IsKilled && !other.IsFinished && second.IsPlaying;
        });

        checks.Check("vectors are tweened the way numbers are", () =>
        {
            var context = new TweenContext();
            Vector2 position = Vector2.Zero;
            Vector4 colour = Vector4.Zero;

            context.Play(Tween.To(() => position, x => position = x, new Vector2(10, -20), 1));
            context.Play(Tween.To(() => colour, x => colour = x, Vector4.One, 1));
            context.Tick(0.5f);

            return Vector2.Distance(position, new Vector2(5, -10)) < 0.001f && Vector4.Distance(colour, new Vector4(0.5f)) < 0.001f;
        });

        checks.Check("a component that pops in is hidden until its turn and whole after it", () =>
        {
            // Not in a UI, so nothing ticks its tweens but this
            var panel = new Panel();
            panel.PopIn(0.4f, 0.2f);
            bool hidden = panel.Opacity == 0 && panel.VisualScale == Vector2.Zero;

            panel.Tweens.Tick(0.1f);
            bool waiting = panel.Opacity == 0;

            panel.Tweens.Tick(1.0f);
            return hidden && waiting && panel.Opacity == 1 && Vector2.Distance(panel.VisualScale, Vector2.One) < 0.001f && panel.Tweens.Count == 0;
        });

        checks.Report();
        return checks;
    }
}
