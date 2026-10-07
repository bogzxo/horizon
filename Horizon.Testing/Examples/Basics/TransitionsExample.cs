using System.Diagnostics;
using System.Numerics;

using Horizon.Core;
using Horizon.Core.Threading;
using Horizon.Engine;
using Horizon.Engine.Components;
using Horizon.Input;
using Horizon.OpenGL.Descriptions;
using Horizon.Rendering;
using Horizon.Rendering.Spriting;
using Horizon.Rendering.Transitions;
using Horizon.Rendering.UIX;
using Horizon.Rendering.UIX.Components;

using Silk.NET.Input;
using Silk.NET.OpenGL;

using Texture = Horizon.OpenGL.Assets.Texture;

namespace Horizon.Testing.Examples.Basics;

/// <summary>
/// Scenes and how you get from one to the next. A little tour goes round three scenes on its own, Home, Night and
/// the Workshop, using a different transition every hop, and each scene tells you how it got there, how it was set
/// up and whether it was ready before you asked for it. Turn on slow set up and you can feel what preloading buys you.
/// <para>
/// What to look at: <see cref="GameEngine.SetScene(Scene, SceneTransition)"/> with a <see cref="FadeTransition"/>,
/// <see cref="BlurTransition"/>, <see cref="RotTransition"/> or null for a hard cut (<see cref="ScreenTransition"/> is
/// where you start for one of your own, and <see cref="SceneManager.Transition"/> sets one for every change),
/// <see cref="SceneManager.Preload(Scene)"/>, <see cref="Scene.IsLeaving"/>, <see cref="Scene.Persistent"/>, and
/// <see cref="Scene.Assets"/>, which is why nothing in here frees a thing by hand. A scene's
/// <see cref="Entity.Initialize"/> runs on the render thread with the simulation standing still
/// (<see cref="RenderFrame.IsDecoupled"/> is false in there), which is the one place its GPU stuff gets made.
/// </para>
/// <para>
/// In your own game:
/// <code>
/// // Make your transitions once and keep them, they hang on to their GPU bits between uses
/// var fade = new FadeTransition { OutTime = 0.3f, InTime = 0.45f };
///
/// // Simulation thread, e.g. in your scene's UpdateState. IsLeaving stops a mashed button setting it twice
/// if (!IsLeaving &amp;&amp; Engine.Input.Keyboard.WasPressed(Key.Enter))
///     Engine.SetScene(new LevelScene(), fade);        // null for a hard cut, or GoTo(scene, fade) from a scene
///
/// // Know what's coming? Set it up ahead, then setting it later swaps straight away
/// _next = new LevelScene();
/// Engine.SceneManager.Preload(_next);
///
/// // A scene that's kept between visits (a hub, a world map) instead of thrown away when it's left
/// public override bool Persistent => true;
/// </code>
/// </para>
/// </summary>
public sealed class TransitionsExample : Scene, ITestControls
{
    // The camera and the UI both see exactly this much of the world, so a spot in the UI is the same spot in the world
    private static readonly Vector2 DesignSize = new(1600, 900);

    // How long the auto tour hangs about at each stop, and how far into a stop it preloads the next one
    private const float STOP_TIME = 4.5f;
    private const float PRELOAD_AT = 1.0f;

    // With slow set up on, every fresh scene sleeps this long in its Initialize. Pretend it's loading a massive level
    private const int SLOW_SET_UP_MS = 1000;

    // The panels are rewritten this often. Ten times a second is plenty for something you read
    private const float READOUT_EVERY = 0.1f;

    // All the art is one texture painted in code: two CELL square frames side by side, a soft glow and a crisp disc.
    // Both are white, so a Tint makes them whatever colour you like
    private const int CELL = 64;
    private const string GLOW = "glow", DISC = "disc";

    private static readonly Vector4 PanelColour = new(0.1f, 0.12f, 0.17f, 0.88f);
    private static readonly Vector4 CaptionColour = new(1.0f, 1.0f, 1.0f, 0.9f);
    private static readonly Vector4 LeavingColour = new(1.0f, 0.7f, 0.35f, 1.0f);

    /// <summary>The stops of the tour, in the order it goes round them.</summary>
    private enum Stop { Home, Night, Workshop }

    /// <summary>What a stop looks like, and what it says about itself on screen.</summary>
    private readonly record struct Look(string Title, string Blurb, Vector4 Sky, Vector4 Main, Vector4 Accent);

    private static readonly Look[] Looks =
    [
        new("Home",
            "Persistent: the very same scene object every visit, kept as you left it.\n" +
            "Nothing in it moves while you're away, its Time stands still.",
            new(0.27f, 0.15f, 0.3f, 1.0f), new(1.0f, 0.72f, 0.38f, 1.0f), new(1.0f, 0.5f, 0.6f, 1.0f)),
        new("Night",
            "A plain Scene: made fresh every visit and thrown away when it's left,\n" +
            "with everything it put on the GPU (that's Scene.Assets).",
            new(0.04f, 0.06f, 0.14f, 1.0f), new(0.95f, 0.95f, 0.86f, 1.0f), new(0.65f, 0.78f, 1.0f, 1.0f)),
        new("Workshop",
            "Another fresh one. Turn slow set up on and every fresh scene sleeps in\n" +
            "Initialize, so you can feel what setting one up on the spot costs.",
            new(0.05f, 0.2f, 0.2f, 1.0f), new(0.35f, 0.95f, 0.8f, 1.0f), new(1.0f, 0.85f, 0.35f, 1.0f))
    ];

    // Which key and which button picks which transition, in the order of Tour.Transitions
    private static readonly Key[] TransitionKeys = [Key.Number1, Key.Number2, Key.Number3, Key.Number4];
    private static readonly GamepadInput[] TransitionButtons = [GamepadInput.A, GamepadInput.B, GamepadInput.X, GamepadInput.Y];

    // Every stop is one of these and they all react to the same keys, so whichever one the host started the test on
    // lists the lot
    private static readonly TestControl[] ControlList =
    [
        new("1 / A", "next scene, FadeTransition"),
        new("2 / B", "next scene, BlurTransition"),
        new("3 / X", "next scene, RotTransition"),
        new("4 / Y", "next scene, hard cut"),
        new("P / RB", "preload the next scene"),
        new("S / LB", "slow set up on / off"),
        new("T / Start", "auto tour on / off")
    ];

    /// <summary>A sprite that's moved by hand every update, and the numbers it's moved by. What they mean is up to the stop.</summary>
    private sealed record Mover(Sprite Sprite, Vector2 Home, float Radius, float Speed, float Phase, float Size);

    public override Camera ActiveCamera { get; protected set; }

    // Only Home is kept. The rest are thrown away the moment they're left, and a new one is made for the next visit
    public override bool Persistent => _stop == Stop.Home;

    // Listed on screen by the test host
    public IReadOnlyList<TestControl> Controls => ControlList;

    private readonly Tour _tour;
    private readonly Stop _stop;
    private readonly Look _look;
    private readonly UICompositor _ui;
    private readonly Random _random = new(1234);

    private readonly List<Mover> _movers = [], _comets = [];
    private Sprite _sun = null!, _moon = null!;
    private Label _details = null!, _leaving = null!, _status = null!;
    private float _readoutTimer;

    // How the scene got here: which of Tour.Transitions it came in with (-1 for "the host started the test on it"),
    // when it was asked for, and how many times
    private int _arrivedWith = -1, _visits = 1;
    private long _askedAt;

    // How the scene was set up: when that finished, how long it took, which thread it happened on, and whether the
    // frame was being drawn from snapshots at the time. Written by Initialize on the render thread while the
    // simulation's parked, read by the simulation once it carries on
    private long _setUpAt;
    private double _setUpMs;
    private string _setUpOn = "?";
    private bool _setUpDecoupled;

    /// <summary>Starts the tour at Home. This is the scene the test host sets.</summary>
    public TransitionsExample() : this(new Tour(), Stop.Home)
    {
        _tour.Home = this;
    }

    private TransitionsExample(Tour tour, Stop stop)
    {
        _tour = tour;
        _stop = stop;
        _look = Looks[(int)stop];

        // What the scene manager calls it in the log when it sets it up
        Name = $"Transitions: {_look.Title}";

        // Cameras are just matrices and UI components own nothing on the GPU, so all of this is fine in the
        // constructor. Which is handy, because the fresh scenes get made on the simulation thread (see GoNext), and
        // any GL from there would go bang
        var camera = AddEntity(new Camera2D(DesignSize));
        ActiveCamera = camera;
        _ui = AddComponent(new UICompositor(camera) { DesignSize = DesignSize });

        BuildPanels();
    }

    public override void Initialize()
    {
        // The scene manager sets a scene up every time it's set, and that includes a Persistent one coming back for
        // another visit. Home still has everything from the first time, so there's nothing to make: doing it all again
        // would stack a second renderer on top of the first and you'd be drawing two of bloody everything
        if (IsInitialized)
        {
            base.Initialize();
            return;
        }

        // Render thread, simulation parked. RenderFrame.Active.IsDecoupled is false in here: nothing is being drawn
        // from snapshots, the game is standing still and you've got it all to yourself
        long started = Stopwatch.GetTimestamp();
        _setUpOn = Thread.CurrentThread.Name ?? "unnamed";
        _setUpDecoupled = RenderFrame.Active.IsDecoupled;

        var renderer = AddEntity(new Renderer2D((uint)DesignSize.X, (uint)DesignSize.Y) { ClearColor = _look.Sky });

        // Batches draw in the order they went into the renderer: the background bits, then what goes on top
        var back = renderer.AddEntity(new SpriteBatch());
        var front = renderer.AddEntity(new SpriteBatch());

        // GPU stuff, so it's made here and not in the constructor. The scene manager has Scene.Assets entered while
        // this runs, so the texture is noted down as this scene's and freed when the scene is left. A fresh scene
        // leaves nothing behind without a single line of cleanup, and Home (Persistent) just keeps it
        var art = Texture.Create(2 * CELL, CELL, TextureDefinition.RgbaUnsignedByte);
        Engine.GL.TextureSubImage2D<byte>(art.Handle, 0, 0, 0, 2 * CELL, CELL, PixelFormat.Rgba, PixelType.UnsignedByte, PaintArt());
        var sheet = SpriteSheet.FromTexture(art, new Vector2(CELL));

        switch (_stop)
        {
            case Stop.Home: BuildHome(back, front, sheet); break;
            case Stop.Night: BuildNight(back, front, sheet); break;
            case Stop.Workshop: BuildWorkshop(back, front, sheet); break;
        }

        // The deliberately slow bit. In a real game this is decoding textures, building a level, compiling shaders.
        // However long it takes, the screen is frozen for it: nothing is drawn or updated until a scene is set up
        if (_tour.SlowSetUp && !Persistent)
            Thread.Sleep(SLOW_SET_UP_MS);

        _setUpMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _setUpAt = Stopwatch.GetTimestamp();

        base.Initialize();
    }

    public override void UpdateState(float dt)
    {
        // Simulation thread. Children first: the sprites and the UI get their turns in here
        base.UpdateState(dt);

        Animate((float)Time, dt);

        // Only the scene on screen gets to drive the tour, and only while it's staying. Once another scene's been set
        // this one stays on screen (and keeps getting updated) until the transition has covered it up, and if it
        // still listened to its keys you could set the next scene twice. That's what IsLeaving is for.
        // The Engine.Scene check is for the scene being preloaded: it gets updated a few times out of sight as it's
        // warmed up, and it's got no business touching the tour from back there
        if (!IsLeaving && Engine.Scene == this)
            Steer(dt);

        _readoutTimer -= dt;
        if (_readoutTimer <= 0.0f)
        {
            _readoutTimer = READOUT_EVERY;
            UpdateReadout();
        }
    }

    protected override void DisposeOther()
    {
        // A Persistent scene is never disposed of for you, whoever keeps it does it when they're done. Here that's
        // the test host, which disposes the scene it started (Home) when you hit ESC and then frees everything the
        // test put on the GPU, ours or not. If Night or the Workshop is on screen at that moment it'd get drawn once
        // more with its texture gone, so it goes with Home. Render thread, simulation parked, so it's safe to do
        if (Persistent && Engine.Scene is TransitionsExample { IsDisposed: false } showing && showing._tour == _tour && showing != this)
            showing.Dispose();

        base.DisposeOther();
    }

    /// <summary>
    /// Helper method to drive the tour: the keys, and the auto tour if it's on. Simulation thread, and only ever the
    /// scene on screen (see <see cref="UpdateState"/>).
    /// </summary>
    private void Steer(float dt)
    {
        // Gotcha: a scene that's just been set is warmed up before it's shown, updated a few times out of sight inside
        // the frame, and input doesn't move on during that. So with a hard cut the new scene sees the very key press
        // that brought it here and goes again, and you skip a scene without touching a thing, which is a right shit
        // to track down. So nothing that comes of a press happens twice in the same tick, and that's the warm up sorted
        if (Engine.WindowManager.Tick != _tour.ActedOnTick)
        {
            for (int i = 0; i < TransitionKeys.Length; i++)
            {
                if (Pressed(TransitionKeys[i], TransitionButtons[i]))
                {
                    // You're driving now, the auto tour gets out of the way
                    _tour.Auto = false;
                    GoNext(i);
                    return;
                }
            }

            if (Pressed(Key.P, GamepadInput.RightBumper))
                PreloadNext();

            if (Pressed(Key.S, GamepadInput.LeftBumper))
            {
                _tour.SlowSetUp = !_tour.SlowSetUp;

                // Whatever was set up ahead was set up the old way, so the next stop gets made over again. The scene
                // manager lets go of the old one by itself as soon as a different scene is preloaded or set
                _tour.Upcoming = null;
                _tour.UpcomingPreloaded = false;
                _tour.Message = _tour.SlowSetUp
                    ? $"Slow set up on: fresh scenes take {SLOW_SET_UP_MS} ms. Try 1, then P and 1"
                    : "Slow set up off";
            }

            if (Pressed(Key.T, GamepadInput.Start))
            {
                _tour.Auto = !_tour.Auto;
                _tour.TimeHere = 0.0f;
                _tour.Message = _tour.Auto ? "Auto tour back on" : "Auto tour off, you're driving";
            }
        }

        if (!_tour.Auto)
            return;

        _tour.TimeHere += dt;

        // Doing it the way you would in a game: the next stop is known well before it's needed, so it's set up ahead
        // and the swap to it costs nothing. Home's Persistent and already set up, there's nothing to preload
        if (_tour.TimeHere >= PRELOAD_AT && !_tour.UpcomingPreloaded && !Upcoming().Persistent)
            PreloadNext();

        if (_tour.TimeHere >= STOP_TIME)
        {
            GoNext(_tour.AutoTransition);
            _tour.AutoTransition = (_tour.AutoTransition + 1) % _tour.Transitions.Length;
        }
    }

    /// <summary>
    /// Helper method to go on to the next stop with one of the transitions. Simulation thread.
    /// </summary>
    private void GoNext(int transition)
    {
        var (name, cover) = _tour.Transitions[transition];
        TransitionsExample next = Upcoming();

        next._arrivedWith = transition;
        next._askedAt = Stopwatch.GetTimestamp();
        if (next.Persistent) next._visits++;

        _tour.Upcoming = null;
        _tour.UpcomingPreloaded = false;
        _tour.Next = (Stop)(((int)next._stop + 1) % Looks.Length);
        _tour.TimeHere = 0.0f;
        _tour.ActedOnTick = Engine.WindowManager.Tick;
        _tour.Message = $"Off to {next._look.Title} with {name}";

        // That's the whole API right there. Safe from any thread, and nothing happens straight away: with a hard cut
        // (null) the swap's at the start of the next frame, with a transition this scene gets covered up first, and
        // the next one is preloaded the moment the cover starts coming down (unless it was already)
        Engine.SetScene(next, cover);
    }

    /// <summary>
    /// Helper method to set the next stop up ahead of time. Simulation thread.
    /// </summary>
    private void PreloadNext()
    {
        TransitionsExample next = Upcoming();

        if (next.Persistent)
        {
            _tour.Message = $"{next._look.Title} is Persistent and set up already, nothing to preload";
            return;
        }

        if (_tour.UpcomingPreloaded)
        {
            _tour.Message = $"{next._look.Title} is already preloaded";
            return;
        }

        // Happens at the start of the next frame, render thread, simulation parked, same as any set up. Preloading
        // doesn't make setting up free, it moves the hitch to a moment you pick: behind a menu, a "get ready", while
        // the player's busy reading something. The swap itself is then instant
        Engine.SceneManager.Preload(next);

        _tour.UpcomingPreloaded = true;
        _tour.ActedOnTick = Engine.WindowManager.Tick;
        _tour.Message = _tour.SlowSetUp
            ? $"Preloaded {next._look.Title}: that hitch was its set up, out of the way now"
            : $"Preloaded {next._look.Title}, going there is instant now";
    }

    /// <summary>
    /// Helper method for the scene the tour goes to next, made the first time somebody asks. Home is always the same
    /// object, the others are new every time. Simulation thread, so nothing in the constructor may touch the GPU.
    /// </summary>
    private TransitionsExample Upcoming() =>
        _tour.Upcoming ??= _tour.Next == Stop.Home ? _tour.Home : new TransitionsExample(_tour, _tour.Next);

    /// <summary>
    /// Helper method for a key, or a button on whichever gamepad was used last, going down this update.
    /// </summary>
    private static bool Pressed(Key key, GamepadInput button) =>
        Engine.Input.Keyboard.WasPressed(key) || Engine.Input.Gamepads.LastUsed?.WasPressed(button) == true;

    /// <summary>
    /// Helper method to move everything along. Simulation thread. It all goes by the scene's own Time, which only moves
    /// while the scene is updated, so Home picks up exactly where you left it :)
    /// </summary>
    private void Animate(float time, float dt)
    {
        switch (_stop)
        {
            case Stop.Home:
                // Planets round the sun on a tilted orbit. The far side of the orbit (the top) is drawn a bit smaller
                _sun.Transform.Size = new Vector2(240.0f * (1.0f + 0.03f * MathF.Sin(time * 1.7f)));
                foreach (Mover orb in _movers)
                {
                    float angle = orb.Phase + orb.Speed * time;
                    orb.Sprite.Transform.Position = orb.Home + new Vector2(MathF.Cos(angle), 0.4f * MathF.Sin(angle)) * orb.Radius;
                    orb.Sprite.Transform.Size = new Vector2(orb.Size * (1.0f - 0.25f * MathF.Sin(angle)));
                }
                break;

            case Stop.Night:
                // Stars twinkle by fading their tint, the moon bobs about
                foreach (Mover star in _movers)
                    star.Sprite.Tint = star.Sprite.Tint with { W = 0.35f + 0.65f * (0.5f + 0.5f * MathF.Sin(time * star.Speed + star.Phase)) };

                _moon.Transform.Position = new Vector2(60.0f + 30.0f * MathF.Sin(time * 0.4f), 110.0f + 14.0f * MathF.Sin(time * 0.7f));

                foreach (Mover comet in _comets)
                {
                    var transform = comet.Sprite.Transform;
                    Vector2 position = transform.Position + new Vector2(-420.0f, -150.0f) * comet.Speed * dt;

                    if (position.X < -DesignSize.X / 2.0f - 100.0f)
                    {
                        // Back round to the top right. Snap() says it was put there, not moved there, so the frames
                        // between this tick and the last don't draw it streaking across the whole sky
                        position = new Vector2(DesignSize.X / 2.0f + 100.0f, 100.0f + _random.NextSingle() * 350.0f);
                        transform.Snap();
                    }

                    transform.Position = position;
                }
                break;

            case Stop.Workshop:
                // A grid of lights with a wave rolling through it
                foreach (Mover light in _movers)
                {
                    float wave = 0.5f + 0.5f * MathF.Sin(time * 3.0f - light.Phase);
                    light.Sprite.Transform.Size = new Vector2(light.Size * (0.45f + 0.75f * wave));
                    light.Sprite.Tint = Vector4.Lerp(_look.Main, _look.Accent, wave);
                }
                break;
        }
    }

    /// <summary>
    /// Helper method to rewrite what the panels say. Simulation thread, which is where the UI gets updated too.
    /// </summary>
    private void UpdateReadout()
    {
        string cameWith = _arrivedWith < 0
            ? "Came in with: a hard cut, the test host started the test on it"
            : $"Came in with: {Describe(_arrivedWith)}";

        string setUp;
        if (Persistent && _visits > 1)
        {
            setUp = $"Made on your first visit, in {_setUpMs:0} ms. Nothing to make since: it's Persistent, it's all still there";
        }
        else if (_askedAt != 0 && _setUpAt < _askedAt)
        {
            double ahead = (double)(_askedAt - _setUpAt) / Stopwatch.Frequency;
            setUp = $"Preloaded {ahead:0.0} s before it was needed: its {_setUpMs:0} ms of set up happened then, the swap was free";
        }
        else if (_arrivedWith >= 0 && _tour.Transitions[_arrivedWith].Transition is not null)
        {
            setUp = $"Set up on the spot in {_setUpMs:0} ms. A transition preloads it as the cover starts,\n" +
                    "so that's how long the game sat frozen right after you asked";
        }
        else
        {
            setUp = $"Set up on the spot in {_setUpMs:0} ms, with the game frozen for every one of them";
        }

        string thread = $"Initialize ran on the {_setUpOn} thread with the game stood still (IsDecoupled: {_setUpDecoupled})";

        string clock = Persistent
            ? $"Visit {_visits}, and this scene's Time reads {Time:0.0} s"
            : $"Brand new scene, its Time reads {Time:0.0} s";

        _details.Text = $"{cameWith}\n{setUp}\n{thread}\n{clock}";

        _leaving.Text = IsLeaving ? "IsLeaving: true, on its way out and ignoring the keys" : "IsLeaving: false";
        _leaving.Color = IsLeaving ? LeavingColour : CaptionColour;

        // The tour's the same whoever's on screen. A scene that isn't (one being covered up) keeps saying what it last said
        if (Engine.Scene != this)
            return;

        string next = Looks[(int)_tour.Next].Title;
        string ready = _tour.Next == Stop.Home ? "Persistent, always ready" : _tour.UpcomingPreloaded ? "preloaded" : "not set up yet";
        string auto = _tour.Auto
            ? $"Auto tour: {next} next, with {_tour.Transitions[_tour.AutoTransition].Name}, in {MathF.Max(0.0f, STOP_TIME - _tour.TimeHere):0.0} s"
            : $"Auto tour off (T): {next} next";

        _status.Text = $"{auto}\n{next} is {ready}\nSlow set up: {(_tour.SlowSetUp ? $"on, {SLOW_SET_UP_MS} ms" : "off")}\n{_tour.Message}";
    }

    /// <summary>Helper method to say what one of the tour's transitions is, timings and all.</summary>
    private string Describe(int transition)
    {
        var (name, cover) = _tour.Transitions[transition];
        return cover is null ? name : $"{name}, {cover.OutTime:0.00} s out and {cover.InTime:0.00} s in";
    }

    /// <summary>
    /// Helper method to put Home together: a sun with three rings of planets round it. Render thread.
    /// </summary>
    private void BuildHome(SpriteBatch back, SpriteBatch front, SpriteSheet sheet)
    {
        var centre = new Vector2(20.0f, 0.0f);

        CreateSprite(back, sheet, GLOW, centre, 760.0f, _look.Main with { W = 0.45f });
        _sun = CreateSprite(back, sheet, DISC, centre, 240.0f, _look.Main);

        (float Radius, int Count, float Speed)[] rings = [(190.0f, 5, 0.7f), (270.0f, 7, -0.45f), (350.0f, 10, 0.3f)];
        foreach (var (radius, count, speed) in rings)
        {
            for (int i = 0; i < count; i++)
            {
                float size = 20.0f + _random.NextSingle() * 16.0f;
                var tint = Vector4.Lerp(_look.Accent, Vector4.One, _random.NextSingle() * 0.5f);
                var orb = CreateSprite(front, sheet, DISC, centre, size, tint);

                _movers.Add(new Mover(orb, centre, radius, speed, MathF.Tau * i / count, size));
            }
        }
    }

    /// <summary>
    /// Helper method to put the Night together: a sky full of stars, a moon and a few comets. Render thread.
    /// </summary>
    private void BuildNight(SpriteBatch back, SpriteBatch front, SpriteSheet sheet)
    {
        for (int i = 0; i < 110; i++)
        {
            var position = new Vector2((_random.NextSingle() - 0.5f) * DesignSize.X, (_random.NextSingle() - 0.5f) * DesignSize.Y);
            float size = 3.0f + _random.NextSingle() * 7.0f;
            var star = CreateSprite(back, sheet, DISC, position, size, Vector4.Lerp(Vector4.One, _look.Accent, _random.NextSingle()));

            _movers.Add(new Mover(star, position, 0.0f, 1.0f + _random.NextSingle() * 2.5f, _random.NextSingle() * MathF.Tau, size));
        }

        // The moon's glow sits still in the back batch. The moon wanders about in front of it a bit, which looks
        // like the haze is lagging behind it for free
        CreateSprite(back, sheet, GLOW, new Vector2(60.0f, 110.0f), 440.0f, _look.Accent with { W = 0.3f });
        _moon = CreateSprite(front, sheet, DISC, new Vector2(60.0f, 110.0f), 170.0f, _look.Main);

        for (int i = 0; i < 3; i++)
        {
            var position = new Vector2(-300.0f + i * 500.0f, 380.0f - i * 90.0f);
            var comet = CreateSprite(front, sheet, GLOW, position, 46.0f, Vector4.One with { W = 0.9f });

            _comets.Add(new Mover(comet, position, 0.0f, 0.8f + 0.3f * i, 0.0f, 46.0f));
        }
    }

    /// <summary>
    /// Helper method to put the Workshop together: a big soft light and a grid of little ones in front of it. Render thread.
    /// </summary>
    private void BuildWorkshop(SpriteBatch back, SpriteBatch front, SpriteSheet sheet)
    {
        var glow = CreateSprite(back, sheet, GLOW, new Vector2(20.0f, -30.0f), 900.0f, _look.Main with { W = 0.25f });
        glow.Transform.Size = new Vector2(1000.0f, 560.0f);

        const int columns = 12, rows = 6;
        const float spacing = 56.0f;
        var corner = new Vector2(20.0f - (columns - 1) * spacing / 2.0f, -30.0f - (rows - 1) * spacing / 2.0f);

        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                var position = corner + new Vector2(column, row) * spacing;
                var light = CreateSprite(front, sheet, DISC, position, 40.0f, _look.Main);

                // The phase is how far behind the wave the light is, so it rolls across and up the grid
                _movers.Add(new Mover(light, position, 0.0f, 0.0f, column * 0.5f + row * 0.3f, 40.0f));
            }
        }
    }

    /// <summary>
    /// Helper method to make a sprite showing one of the two frames of the sheet, and put it in a batch. Render thread.
    /// </summary>
    private static Sprite CreateSprite(SpriteBatch batch, SpriteSheet sheet, string art, Vector2 position, float size, Vector4 tint)
    {
        // AddEntity gets it updated, Add gets it drawn. You want both
        var sprite = batch.AddEntity(new Sprite(new Vector2(size)));
        sprite.ConfigureSpriteSheet(sheet, art);

        // A one frame "animation" at the column of the sheet the art is in
        sprite.AddAnimation(art, new Vector2(art == GLOW ? 0 : 1, 0), 1);
        sprite.Transform.Position = position;
        sprite.Tint = tint;

        batch.Add(sprite);
        return sprite;
    }

    /// <summary>
    /// Helper method to put up the panel of what the scene's about in the top left, and the tour's status in the top right.
    /// </summary>
    private void BuildPanels()
    {
        var module = _ui.CreateModule();

        var panel = module.AddComponent(new StackPanel
        {
            Anchor = Origin.TopLeft,
            Position = new Vector2(24, -24),
            Color = PanelColour,
            Padding = new UIEdges(18),
            Spacing = 10
        });

        panel.Add(new Label(_look.Title) { Anchor = Origin.Left, TextScale = 0.46f, Color = _look.Main });
        panel.Add(new Label(_look.Blurb) { Anchor = Origin.Left, Align = Origin.TopLeft, TextScale = 0.25f, Color = CaptionColour });
        _details = panel.Add(new Label { Anchor = Origin.Left, Align = Origin.TopLeft, TextScale = 0.25f });
        _leaving = panel.Add(new Label { Anchor = Origin.Left, TextScale = 0.25f, Color = CaptionColour });

        var status = module.AddComponent(new StackPanel
        {
            Anchor = Origin.TopRight,
            Position = new Vector2(-24, -24),
            Color = PanelColour,
            Padding = new UIEdges(18)
        });
        _status = status.Add(new Label { Anchor = Origin.Left, Align = Origin.TopLeft, TextScale = 0.25f, Color = CaptionColour });

        UpdateReadout();
    }

    /// <summary>
    /// Helper method to paint the art: a soft glow in the left cell and a disc with a crisp (but not jaggy) edge in the
    /// right one, white on see-through.
    /// </summary>
    private static byte[] PaintArt()
    {
        var pixels = new byte[2 * CELL * CELL * 4];
        float middle = CELL / 2.0f;

        for (int y = 0; y < CELL; y++)
        {
            for (int x = 0; x < CELL; x++)
            {
                float fromMiddle = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(middle)) / middle;

                // Both fade out before the edge of their cell. The texture's smoothed, so anything touching the edge
                // bleeds into the cell next door and you get a little sliver of disc at the edge of every glow
                float glow = MathF.Pow(MathF.Max(0.0f, 1.0f - fromMiddle / 0.95f), 2.0f);
                float disc = Math.Clamp((0.94f - fromMiddle) * middle, 0.0f, 1.0f);

                Paint(x, y, glow);
                Paint(CELL + x, y, disc);
            }
        }

        return pixels;

        void Paint(int x, int y, float alpha)
        {
            int at = (y * 2 * CELL + x) * 4;
            pixels[at] = pixels[at + 1] = pixels[at + 2] = 255;
            pixels[at + 3] = (byte)(alpha * 255.0f);
        }
    }

    /// <summary>
    /// What every stop of the tour shares: the transitions, which scene's next, and the settings. Only ever touched
    /// on the simulation thread, by whichever scene is on screen (the slow set up flag is read in Initialize too,
    /// but the simulation's parked while that runs).
    /// </summary>
    private sealed class Tour
    {
        // Made once and used for every change. A transition keeps its GPU bits from one use to the next, making a new
        // one every time is a hitch right when the player pressed something
        public readonly (string Name, SceneTransition? Transition)[] Transitions =
        [
            ("FadeTransition", new FadeTransition { Color = new Vector3(0.03f, 0.02f, 0.06f), OutTime = 0.3f, InTime = 0.45f }),
            ("BlurTransition", new BlurTransition()),
            ("RotTransition", new RotTransition()),
            ("a hard cut", null)
        ];

        public TransitionsExample Home = null!;

        // Where the tour goes next, and that scene once somebody's asked for it (and whether it's been preloaded)
        public Stop Next = Stop.Night;
        public TransitionsExample? Upcoming;
        public bool UpcomingPreloaded;

        public bool Auto = true, SlowSetUp;
        public int AutoTransition;
        public float TimeHere;

        // The tick something was last done by a press or the auto tour, see Steer
        public long ActedOnTick = -1;

        public string Message = "Sit back, or grab the wheel with the keys";
    }
}
