using System.Numerics;
using System.Text;

using Horizon.Engine;
using Horizon.Input;
using Horizon.Rendering;
using Horizon.Rendering.Spriting;
using Horizon.UI;
using Horizon.UI.Components;

using Silk.NET.Input;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Horizon.Testing.Examples.Input;

/// <summary>
/// The keyboard and the mouse. A strip of keys lights up while you hold them and flashes when they go down and up,
/// a ring chases your pointer around a world the camera drifts over, clicks leave ripples, the wheel is counted, and
/// a log says what happened on which tick. Leave it alone for a few seconds and a ghost has a go on it for you.
/// <para>
/// What to look at: <see cref="InputManager"/> (it's <c>Engine.Input</c>), its <see cref="Horizon.Input.Keyboard"/> with
/// <see cref="Horizon.Input.Keyboard.IsDown"/>, <see cref="Horizon.Input.Keyboard.WasPressed"/>,
/// <see cref="Horizon.Input.Keyboard.WasReleased"/> and <see cref="Horizon.Input.Keyboard.AnyPressed"/>, and its
/// <see cref="Horizon.Input.Mouse"/> with the same three for buttons plus <see cref="Horizon.Input.Mouse.Position"/>,
/// <see cref="Horizon.Input.Mouse.Delta"/> and <see cref="Horizon.Input.Mouse.Scroll"/>. The pointer goes into the
/// world through <see cref="Camera.ScreenToWorld"/>. Gamepads are in <c>Engine.Input.Gamepads</c>, and they've got an
/// example of their own: <see cref="GamepadExample"/>.
/// </para>
/// <para>
/// In your own game:
/// <code>
/// // UpdateState, simulation thread. Input has already moved on for this update by the time you get here
/// var keyboard = Engine.Input.Keyboard;
/// if (keyboard.WasPressed(Key.Space)) Jump();                 // true for exactly one update per press
/// float steer = keyboard.Axis(Key.A, Key.D);                  // -1, 0 or 1
///
/// var mouse = Engine.Input.Mouse;
/// Vector2 aim = camera.ScreenToWorld(mouse.Position);         // window pixels -> the world
/// if (mouse.WasPressed(MouseButton.Left)) Shoot(aim);
/// camera.Zoom -= mouse.Scroll * 0.1f;                         // how far the wheel went THIS update
/// </code>
/// </para>
/// </summary>
public class KeyboardMouseExample : Scene, ITestControls
{
    // The UI is laid out for this, and the world camera sees exactly this much of the world
    private static readonly Vector2 DesignSize = new(1600, 900);

    private static readonly Vector4 Background = new(0.09f, 0.1f, 0.14f, 1.0f);
    private static readonly Vector4 PanelColour = new(0.13f, 0.15f, 0.21f, 0.94f);
    private static readonly Vector4 TextColour = new(0.93f, 0.95f, 1.0f, 0.9f);
    private static readonly Vector4 DimColour = new(0.93f, 0.95f, 1.0f, 0.55f);

    // What a key looks like sitting there, held down, just pressed and just let go of
    private static readonly Vector4 KeyIdle = new(0.21f, 0.24f, 0.32f, 1.0f);
    private static readonly Vector4 KeyHeld = new(1.0f, 0.7f, 0.25f, 1.0f);
    private static readonly Vector4 KeyPressFlash = new(1.0f, 1.0f, 1.0f, 1.0f);
    private static readonly Vector4 KeyReleaseFlash = new(0.35f, 0.7f, 1.0f, 1.0f);

    // A ripple's colour says which button made it
    private static readonly Vector4 LeftColour = new(1.0f, 0.72f, 0.3f, 1.0f);
    private static readonly Vector4 RightColour = new(0.35f, 0.85f, 1.0f, 1.0f);
    private static readonly Vector4 MiddleColour = new(1.0f, 0.45f, 0.72f, 1.0f);

    // How big a key is on screen, in units of the design
    private const float KEY = 50.0f;

    // How fast the follower closes the gap to the pointer, higher is snappier
    private const float FOLLOW = 14.0f;

    // How many ripples there are to go round, and how long one lasts in seconds
    private const int RIPPLES = 24;
    private const float RIPPLE_LIFE = 1.0f;

    // How many seconds of nobody touching anything before the ghost has a go
    private const float GHOST_AFTER = 5.0f;

    // How many lines the log keeps
    private const int LOG_LINES = 10;

    // The strip: one row of keys per line, with a width for the ones that aren't square
    private static readonly (Key Key, string Text, float Width)[][] Rows =
    [
        [.. "QWERTYUIOP".Select(Letter)],
        [.. "ASDFGHJKL".Select(Letter)],
        [(Key.ShiftLeft, "Shift", 84), .. "ZXCVBNM".Select(Letter), (Key.Up, "^", KEY)],
        [(Key.ControlLeft, "Ctrl", 70), (Key.AltLeft, "Alt", 60), (Key.Space, "Space", 236), (Key.Left, "<", KEY), (Key.Down, "v", KEY), (Key.Right, ">", KEY)]
    ];

    public override Camera ActiveCamera { get; protected set; }

    // Listed on screen by the test host
    public IReadOnlyList<TestControl> Controls { get; } =
    [
        new("Keys on the strip", "light up, and go in the log"),
        new("Mouse", "the ring chases it"),
        new("Left / right / middle", "ripples, a colour each"),
        new("Wheel", "counted"),
        new("Backspace", "clear the log")
    ];

    private readonly Camera2D _camera;
    private readonly UICompositor _ui;
    private readonly string _artDirectory;
    private readonly SpriteSheetDefinition _art;
    private readonly TextureAtlas _atlas = new(256, 256);

    // The keys on the strip, and the mouse buttons in the corner, which light up exactly the same way
    private readonly List<(Key Key, KeyCap Cap)> _keys = [];
    private readonly (MouseButton Button, KeyCap Cap, Vector4 Colour)[] _buttons;

    private Sprite _follower = null!, _pointerDot = null!;
    private readonly Sprite[] _ripples = new Sprite[RIPPLES];
    private readonly float[] _rippleAge = new float[RIPPLES];
    private readonly Vector4[] _rippleColour = new Vector4[RIPPLES];
    private int _nextRipple;
    private float _followerPunch;

    private Label _pointer = null!, _scrollTotal = null!, _scrollNow = null!, _log = null!;
    private Panel _ghostBanner = null!;
    private float _scrolled, _scrollPunch;

    private readonly List<string> _logLines = [];
    private bool _logChanged = true;

    // Leave it alone and the ghost comes out to play. Starts out playing, so there's something to look at straight away
    private readonly Ghost _ghost = new();
    private float _idle = GHOST_AFTER;
    private bool _ghosting;

    public KeyboardMouseExample()
    {
        // The world camera. It drifts about on its own in UpdateState, purely so turning the pointer into the world
        // has something to get right
        _camera = AddEntity(new Camera2D(DesignSize));
        ActiveCamera = _camera;

        // The UI gets a camera of its own (ForScreen), so it stays put on screen while the world one wanders off.
        // UI components own nothing on the GPU, so the whole lot can be built right here in the constructor
        _ui = AddComponent(UICompositor.ForScreen());
        _ui.DesignSize = DesignSize;

        // Painting the art is files and memory, no GL, so it's fine here too. Only the atlas's texture waits for a frame
        _artDirectory = Path.Combine(Path.GetTempPath(), "horizon-keyboard-mouse-example");
        Directory.CreateDirectory(_artDirectory);
        PaintArt(Path.Combine(_artDirectory, "art.png"));
        File.WriteAllText(Path.Combine(_artDirectory, "art.hor"), ArtDefinition);
        _art = SpriteSheetDefinition.Load(_artDirectory, "art.hor");

        BuildIntro();
        BuildKeyStrip();
        _buttons = BuildMousePanel();
        BuildLog();
    }

    public override void Initialize()
    {
        // Render thread, simulation parked. The renderer is GPU stuff, so it's made here and not in the constructor.
        // Batches draw in the order they went in: the grid at the back, ripples over it, the follower on top
        var renderer = AddEntity(new Renderer2D((uint)DesignSize.X, (uint)DesignSize.Y) { ClearColor = Background });
        var grid = renderer.AddEntity(new SpriteBatch());
        var ripples = renderer.AddEntity(new SpriteBatch());
        var front = renderer.AddEntity(new SpriteBatch());

        BuildGrid(grid);

        for (int i = 0; i < RIPPLES; i++)
        {
            // The whole pool is made up front and recycled. Making sprites mid game is allowed, but a click shouldn't
            // cost a fresh entity (and a rendezvous with the render thread to set it up) every single time
            _ripples[i] = CreateArt(ripples, "ring", Vector2.Zero, new Vector2(16));
            _ripples[i].Tint = Vector4.Zero;
            _rippleAge[i] = RIPPLE_LIFE;
        }

        _follower = CreateArt(front, "ring", Vector2.Zero, new Vector2(64));
        _pointerDot = CreateArt(front, "disc", Vector2.Zero, new Vector2(12));

        base.Initialize();
    }

    public override void UpdateState(float dt)
    {
        // Simulation thread. The InputManager lives on the engine and moves on BEFORE anything of the game is updated,
        // so everything in this update sees the same keyboard, and a WasPressed is true for exactly this one update.
        // Read it from the updates, never while drawing: the render thread would catch it halfway through moving on
        var keyboard = Engine.Input.Keyboard;
        var mouse = Engine.Input.Mouse;

        // The tick this happened on. With the default rates (logic and physics both at 120 Hz) there's one update a
        // tick, so a press and its release one tick apart is as short as a key can be held
        long tick = Engine.WindowManager.Tick;

        // Somebody touched something: the ghost buggers off straight away, and comes back after a bit of peace
        bool touched = keyboard.AnyPressed || mouse.Delta != Vector2.Zero || mouse.Scroll != 0.0f
            || mouse.WasPressed(MouseButton.Left) || mouse.WasPressed(MouseButton.Right) || mouse.WasPressed(MouseButton.Middle);

        _idle = touched ? 0.0f : _idle + dt;
        bool ghosting = _idle >= GHOST_AFTER;
        if (ghosting && !_ghosting)
            _ghost.Restart();

        _ghosting = ghosting;
        if (_ghosting)
            _ghost.Update(dt);

        DriftCamera();

        // The keyboard. These three are all you'll ever really need:
        //   IsDown      - held right now, true every update for as long as it's held (movement, charging a shot)
        //   WasPressed  - went down since the last update, true ONCE per press (jump, menus, anything that's a "do it")
        //   WasReleased - came up since the last update, true once (letting go of a charged shot)
        // Keys are heard as they happen on the window thread, not polled once a tick, so a tap that goes down and up
        // between two updates still shows as pressed (and down) for one update and released the next. No lost jumps :)
        foreach (var (key, cap) in _keys)
        {
            Feel(cap, tick, keyboard.IsDown(key), keyboard.WasPressed(key), keyboard.WasReleased(key), ghost: false);

            if (_ghosting)
                Feel(cap, tick, _ghost.IsDown(key), _ghost.WasPressed(key), _ghost.WasReleased(key), ghost: true);

            cap.Animate(dt);
        }

        // Mouse buttons are asked exactly the same three questions, and get the same help with quick clicks
        Vector2 aim = mouse.Position;
        Vector2 world = _camera.ScreenToWorld(aim);
        Vector2 target = _ghosting ? _ghost.Pointer : world;

        foreach (var (button, cap, colour) in _buttons)
        {
            bool pressed = mouse.WasPressed(button);
            Feel(cap, tick, mouse.IsDown(button), pressed, mouse.WasReleased(button), ghost: false);
            if (pressed)
                Ripple(world, colour);

            if (_ghosting)
            {
                bool ghostPressed = _ghost.WasPressed(button);
                Feel(cap, tick, _ghost.IsDown(button), ghostPressed, _ghost.WasReleased(button), ghost: true);
                if (ghostPressed)
                    Ripple(_ghost.Pointer, colour);
            }

            cap.Animate(dt);
        }

        // Scroll is how far the wheel went since the LAST update, not a running total. Add it up yourself if you want one.
        // A notch is usually 1, but a trackpad hands you little fractions of one all day long, so don't count notches
        float scroll = mouse.Scroll + (_ghosting ? _ghost.Scroll : 0.0f);
        if (scroll != 0.0f)
        {
            _scrolled += scroll;
            _scrollPunch = 1.0f;
            _scrollTotal.Text = $"{_scrolled:+0.#;-0.#;0}";
            _scrollNow.Text = $"Scroll this update: {scroll:+0.##;-0.##}";
        }

        if (keyboard.WasPressed(Key.Backspace))
        {
            _logLines.Clear();
            _logChanged = true;
        }

        UpdateFollower(target, dt);
        UpdateRipples(dt);
        UpdateReadouts(aim, world, dt);

        // Children and components LAST for once. The UI compositor lays out and paints in its own UpdateState, so
        // everything we just changed on the UI makes it on screen this tick instead of the next
        base.UpdateState(dt);
    }

    /// <summary>
    /// Helper method to light a key up for what it's doing this update, and log presses and releases. Simulation thread.
    /// </summary>
    private void Feel(KeyCap cap, long tick, bool down, bool pressed, bool released, bool ghost)
    {
        if (down)
            cap.Held = true;

        string who = ghost ? "   (ghost)" : string.Empty;

        if (pressed)
        {
            cap.Press(tick);
            Log($"tick {tick}   {cap.Name} pressed{who}");
        }

        if (released)
        {
            long held = tick - cap.PressedOn;
            cap.Release();

            // It can't be any shorter than one tick. A tap that came AND went entirely between two updates is held
            // down for the update that hears of it and let go of at the next, so it still counts. A keyboard that was
            // only looked at once a tick would never have seen it at all
            string how = held <= 1 ? "1 tick, a tap, still caught!" : $"held {held} ticks";
            Log($"tick {tick}   {cap.Name} released, {how}{who}");
        }
    }

    /// <summary>
    /// Helper method to sway the world camera about, slowly. Simulation thread.
    /// </summary>
    private void DriftCamera()
    {
        float t = (float)Time;
        _camera.Position = new Vector3(90.0f * MathF.Sin(t * 0.23f), 50.0f * MathF.Sin(t * 0.31f), _camera.Position.Z);
    }

    /// <summary>
    /// Helper method to ease the follower towards wherever the pointer is in the world. Simulation thread.
    /// </summary>
    private void UpdateFollower(Vector2 target, float dt)
    {
        // The dot sits exactly on the target. Mouse.Position is window pixels from the top left with Y going DOWN,
        // the world is Y up and wherever the camera happens to be. ScreenToWorld sorts all of that out. It goes by the
        // camera as it was at the end of the last tick, so it's a tick behind a camera you've just moved; at 120 Hz
        // nobody on earth will ever notice
        _pointerDot.Transform.Position = target;

        // Exponential smoothing, the same however long dt is. Set the position every tick and let the batch slide it
        // between ticks for the frames in between, there's no need to do anything per frame yourself
        var transform = _follower.Transform;
        transform.Position += (target - transform.Position) * (1.0f - MathF.Exp(-FOLLOW * dt));

        _followerPunch = MathF.Max(0.0f, _followerPunch - dt * 4.0f);
        transform.Size = new Vector2(64.0f * (1.0f + 0.4f * _followerPunch * _followerPunch));
        _follower.Tint = _ghosting ? new Vector4(1.0f, 1.0f, 1.0f, 0.55f) : Vector4.One;
    }

    /// <summary>
    /// Helper method to send a ripple out from a point in the world, recycling the oldest one. Simulation thread.
    /// </summary>
    private void Ripple(Vector2 at, Vector4 colour)
    {
        int i = _nextRipple;
        _nextRipple = (_nextRipple + 1) % RIPPLES;

        // Out of the pool, so it was PUT here, not moved here. Without Snap() the frames between this tick and the last
        // would draw it sliding across the screen from wherever it went off last time. Any teleport wants one of these
        _ripples[i].Transform.Position = at;
        _ripples[i].Transform.Snap();

        _rippleAge[i] = 0.0f;
        _rippleColour[i] = colour;
        _followerPunch = 1.0f;
    }

    /// <summary>
    /// Helper method to grow and fade every ripple that's still going. Simulation thread.
    /// </summary>
    private void UpdateRipples(float dt)
    {
        for (int i = 0; i < RIPPLES; i++)
        {
            if (_rippleAge[i] >= RIPPLE_LIFE)
                continue;

            _rippleAge[i] = MathF.Min(RIPPLE_LIFE, _rippleAge[i] + dt);
            float t = _rippleAge[i] / RIPPLE_LIFE;
            float grown = 1.0f - (1.0f - t) * (1.0f - t) * (1.0f - t);

            _ripples[i].Transform.Size = new Vector2(20.0f + 300.0f * grown);
            _ripples[i].Tint = _rippleColour[i] with { W = (1.0f - t) * (1.0f - t) };
        }
    }

    /// <summary>
    /// Helper method to keep the numbers in the mouse panel and the log up to date. Simulation thread, which is
    /// where UI gets changed too.
    /// </summary>
    private void UpdateReadouts(Vector2 aim, Vector2 world, float dt)
    {
        _pointer.Text = $"Position  {aim.X:0}, {aim.Y:0}  (window pixels, y down)\nIn the world  {world.X:0}, {world.Y:0}  (y up)";

        _scrollPunch = MathF.Max(0.0f, _scrollPunch - dt * 5.0f);
        _scrollTotal.VisualScale = new Vector2(1.0f + 0.35f * _scrollPunch);

        _ghostBanner.Visible = _ghosting;
        _ghostBanner.Opacity = 0.8f + 0.2f * MathF.Sin((float)Time * 3.0f);

        if (!_logChanged)
            return;

        _logChanged = false;
        _log.Text = _logLines.Count == 0 ? "nothing yet, go on, press something" : string.Join('\n', _logLines);
    }

    /// <summary>
    /// Helper method to put a line at the top of the log, dropping the oldest. Simulation thread.
    /// </summary>
    private void Log(string line)
    {
        _logLines.Insert(0, line);
        if (_logLines.Count > LOG_LINES)
            _logLines.RemoveAt(_logLines.Count - 1);

        _logChanged = true;
    }

    /// <summary>
    /// Helper method to make a key for the strip out of a letter, which is just its name in Silk's Key.
    /// </summary>
    private static (Key Key, string Text, float Width) Letter(char letter) => (Enum.Parse<Key>(letter.ToString()), letter.ToString(), KEY);

    /// <summary>
    /// Helper method to make a sprite out of the atlas and put it in a batch. Doesn't need the GL context: the atlas has
    /// nothing on the GPU until the batch first draws it.
    /// </summary>
    private Sprite CreateArt(SpriteBatch batch, string art, Vector2 position, Vector2 size)
    {
        // AddEntity gets it updated, Add gets it drawn. You want both
        var sprite = batch.AddEntity(new Sprite(size));
        sprite.ConfigureAtlas(_atlas, _art, art);
        sprite.Transform.Position = position;

        // The art's painted smooth, and gets drawn at all sorts of sizes, so blend its pixels rather than going blocky
        sprite.Smooth = true;

        batch.Add(sprite);
        return sprite;
    }

    /// <summary>
    /// Helper method to lay a grid of faint lines over the world, with the axes a bit brighter, so you can see the
    /// camera drifting over it. Render thread.
    /// </summary>
    private void BuildGrid(SpriteBatch batch)
    {
        const float STEP = 100.0f, WIDE = 1000.0f, HIGH = 600.0f;

        for (float x = -WIDE; x <= WIDE; x += STEP)
            CreateArt(batch, "pixel", new Vector2(x, 0), new Vector2(2, HIGH * 2)).Tint = new Vector4(1, 1, 1, x == 0 ? 0.16f : 0.05f);

        for (float y = -HIGH; y <= HIGH; y += STEP)
            CreateArt(batch, "pixel", new Vector2(0, y), new Vector2(WIDE * 2, 2)).Tint = new Vector4(1, 1, 1, y == 0 ? 0.16f : 0.05f);
    }

    /// <summary>
    /// Helper method to put up the panel in the top left that says what this is all about.
    /// </summary>
    private void BuildIntro()
    {
        var module = _ui.CreateModule();

        var panel = module.AddComponent(new StackPanel
        {
            Anchor = Origin.TopLeft,
            Position = new Vector2(24, -24),
            Color = PanelColour,
            Radius = 10,
            Padding = new UIEdges(18),
            Spacing = 10
        });
        panel.Add(new Label("Keyboard and mouse") { Anchor = Origin.Left, TextScale = 0.36f });
        panel.Add(new Label(
            "Engine.Input.Keyboard and Engine.Input.Mouse, read in\n" +
            "UpdateState. Hold keys on the strip, click and scroll\n" +
            "anywhere. The camera drifts on purpose: the ring still\n" +
            "lands under your pointer thanks to ScreenToWorld.\n" +
            "Pads live in Engine.Input.Gamepads, see the Gamepads test.")
        {
            Anchor = Origin.Left,
            Align = Origin.TopLeft,
            TextScale = 0.21f,
            Color = TextColour
        });

        _ghostBanner = module.AddComponent(new Panel
        {
            Anchor = Origin.Top,
            Position = new Vector2(40, -24),
            Color = PanelColour,
            Radius = 10,
            Padding = new UIEdges(16, 12)
        });
        _ghostBanner.Add(new Label("Nobody's touching anything, so the ghost's having a go.\nPress a key or wiggle the mouse to take over.")
        {
            TextScale = 0.22f,
            Color = new Vector4(0.75f, 0.85f, 1.0f, 1.0f)
        });
    }

    /// <summary>
    /// Helper method to build the strip of keys along the bottom, and the legend for its colours.
    /// </summary>
    private void BuildKeyStrip()
    {
        var module = _ui.CreateModule();

        // Bottom left, high enough to clear the test host's bar underneath it
        var strip = module.AddComponent(new StackPanel
        {
            Anchor = Origin.BottomLeft,
            Position = new Vector2(24, 100),
            Color = PanelColour,
            Radius = 10,
            Padding = new UIEdges(16),
            Spacing = 6
        });

        foreach (var row in Rows)
        {
            var line = strip.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 6 });
            foreach (var (key, text, width) in row)
                _keys.Add((key, CreateCap(line, key.ToString(), text, width)));
        }

        var legend = strip.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8, Padding = new UIEdges(0, 8, 0, 0) });
        Swatch(legend, KeyHeld, "IsDown");
        Swatch(legend, KeyPressFlash, "WasPressed");
        Swatch(legend, KeyReleaseFlash, "WasReleased");

        static void Swatch(StackPanel legend, Vector4 colour, string text)
        {
            legend.Add(new Panel { Size = new Vector2(16), Color = colour, Radius = 4 });
            legend.Add(new Label(text + "   ") { TextScale = 0.2f, Color = TextColour });
        }
    }

    /// <summary>
    /// Helper method to build the mouse panel in the top right: a cap per button, the scroll counter and where the
    /// pointer is.
    /// </summary>
    private (MouseButton, KeyCap, Vector4)[] BuildMousePanel()
    {
        var module = _ui.CreateModule();

        var panel = module.AddComponent(new StackPanel
        {
            Anchor = Origin.TopRight,
            Position = new Vector2(-24, -24),
            Color = PanelColour,
            Radius = 10,
            Padding = new UIEdges(18),
            Spacing = 10
        });
        panel.Add(new Label("Mouse") { Anchor = Origin.Left, TextScale = 0.32f });

        var row = panel.Add(new StackPanel { Anchor = Origin.Left, Direction = UIDirection.Horizontal, Spacing = 8 });
        (MouseButton, KeyCap, Vector4)[] buttons =
        [
            (MouseButton.Left, CreateCap(row, "Left click", "Left", 84, LeftColour), LeftColour),
            (MouseButton.Middle, CreateCap(row, "Middle click", "Mid", 64, MiddleColour), MiddleColour),
            (MouseButton.Right, CreateCap(row, "Right click", "Right", 84, RightColour), RightColour)
        ];

        // The wheel: a running total that we add up ourselves, and what the last update that saw any of it got
        var wheel = row.Add(new StackPanel { Spacing = 2, Padding = new UIEdges(14, 0, 0, 0) });
        wheel.Add(new Label("Wheel") { TextScale = 0.18f, Color = DimColour });
        _scrollTotal = wheel.Add(new Label("0") { TextScale = 0.4f });

        _scrollNow = panel.Add(new Label("Scroll this update: none yet") { Anchor = Origin.Left, TextScale = 0.2f, Color = DimColour });
        _pointer = panel.Add(new Label { Anchor = Origin.Left, Align = Origin.TopLeft, Size = new Vector2(440, 28), TextScale = 0.2f, Color = TextColour });

        return buttons;
    }

    /// <summary>
    /// Helper method to build the log in the bottom right.
    /// </summary>
    private void BuildLog()
    {
        var module = _ui.CreateModule();

        var panel = module.AddComponent(new StackPanel
        {
            Anchor = Origin.BottomRight,
            Position = new Vector2(-24, 24),
            Color = PanelColour,
            Radius = 10,
            Padding = new UIEdges(18),
            Spacing = 10
        });
        panel.Add(new Label("Presses and releases, newest first") { Anchor = Origin.Left, TextScale = 0.24f });

        // A fixed size, or the whole panel would grow and shrink with every line that comes and goes
        _log = panel.Add(new Label { Anchor = Origin.Left, Align = Origin.TopLeft, Size = new Vector2(520, 128), TextScale = 0.21f, Color = TextColour });
    }

    /// <summary>
    /// Helper method to make one key (or mouse button) on screen: a rounded box with its name in it.
    /// </summary>
    private static KeyCap CreateCap(StackPanel row, string name, string text, float width, Vector4? held = null)
    {
        var panel = row.Add(new Panel { Size = new Vector2(width, KEY), Color = KeyIdle, Radius = 8 });
        var label = panel.Add(new Label(text) { TextScale = 0.21f, Color = TextColour });
        return new KeyCap(name, panel, label, held ?? KeyHeld);
    }

    /// <summary>
    /// One key on screen. Held while it's down, a white flash when it goes down, a blue blip when it comes up.
    /// Only ever touched on the simulation thread.
    /// </summary>
    private sealed class KeyCap(string name, Panel panel, Label label, Vector4 heldColour)
    {
        /// <summary>What the log calls it.</summary>
        public string Name { get; } = name;

        /// <summary>Whether anything says it's down this update. Cleared again by <see cref="Animate"/>.</summary>
        public bool Held;

        /// <summary>The tick it last went down on, so the log can say how long it was held.</summary>
        public long PressedOn { get; private set; }

        private float _pressFlash, _releaseFlash;

        public void Press(long tick)
        {
            PressedOn = tick;
            _pressFlash = 1.0f;
        }

        public void Release() => _releaseFlash = 1.0f;

        /// <summary>
        /// Helper method to work out its colour and size from what it's doing and fade the flashes out.
        /// </summary>
        public void Animate(float dt)
        {
            Vector4 colour = Held ? heldColour : KeyIdle;
            colour = Vector4.Lerp(colour, KeyReleaseFlash, _releaseFlash * 0.85f);
            colour = Vector4.Lerp(colour, KeyPressFlash, _pressFlash * 0.9f);

            panel.Color = colour;
            panel.VisualScale = new Vector2(1.0f + 0.18f * _pressFlash * _pressFlash - (Held ? 0.06f : 0.0f));
            label.Color = Held || _pressFlash > 0.4f ? new Vector4(0.08f, 0.09f, 0.12f, 1.0f) : TextColour;

            _pressFlash = MathF.Max(0.0f, _pressFlash - dt * 5.0f);
            _releaseFlash = MathF.Max(0.0f, _releaseFlash - dt * 4.0f);
            Held = false;
        }
    }

    /// <summary>
    /// Somebody to play with it while you're not. It answers the same three questions the keyboard and mouse do,
    /// worked out the simple way: down now, and was it down the update before. It's a toy for this example, not the
    /// way to fake input: for that there's InputScript, which drives a gamepad (HORIZON_INPUT_SCRIPT).
    /// </summary>
    private sealed class Ghost
    {
        // What it types, over and over. A capital is Shift held round the first letter, so there's some overlap
        private static readonly string[] Words = ["Horizon", "Hello", "Wasd"];

        // Up up down down left right left right B A. Obviously
        private static readonly Key[] Konami = [Key.Up, Key.Up, Key.Down, Key.Down, Key.Left, Key.Right, Key.Left, Key.Right, Key.B, Key.A];

        // Which button it clicks, one after another, and how often
        private static readonly MouseButton[] Clicks = [MouseButton.Left, MouseButton.Left, MouseButton.Right, MouseButton.Left, MouseButton.Middle];
        private const float CLICK_EVERY = 1.1f, CLICK_HOLD = 0.14f, SCROLL_EVERY = 1.7f;

        private readonly List<(float At, Key Key, bool Down)> _typing = [];
        private readonly float _typingLength;

        private readonly HashSet<Key> _keys = [], _keysBefore = [];
        private readonly HashSet<MouseButton> _buttons = [], _buttonsBefore = [];

        private float _time, _typingTime;
        private int _next, _scrolls;

        /// <summary>Where its pointer is, already in the world.</summary>
        public Vector2 Pointer { get; private set; }

        /// <summary>How far it turned the wheel this update.</summary>
        public float Scroll { get; private set; }

        public Ghost()
        {
            float t = 0.3f;
            foreach (string word in Words)
            {
                for (int i = 0; i < word.Length; i++)
                {
                    var key = Enum.Parse<Key>(char.ToUpperInvariant(word[i]).ToString());

                    if (i == 0) _typing.Add((t - 0.06f, Key.ShiftLeft, true));
                    _typing.Add((t, key, true));
                    _typing.Add((t + 0.11f, key, false));
                    if (i == 0) _typing.Add((t + 0.16f, Key.ShiftLeft, false));

                    t += 0.18f;
                }

                _typing.Add((t, Key.Space, true));
                _typing.Add((t + 0.14f, Key.Space, false));
                t += 0.7f;
            }

            foreach (Key key in Konami)
            {
                _typing.Add((t, key, true));
                _typing.Add((t + 0.12f, key, false));
                t += 0.2f;
            }

            _typing.Sort((a, b) => a.At.CompareTo(b.At));
            _typingLength = t + 1.5f;
        }

        public bool IsDown(Key key) => _keys.Contains(key);
        public bool WasPressed(Key key) => _keys.Contains(key) && !_keysBefore.Contains(key);
        public bool WasReleased(Key key) => !_keys.Contains(key) && _keysBefore.Contains(key);

        public bool IsDown(MouseButton button) => _buttons.Contains(button);
        public bool WasPressed(MouseButton button) => _buttons.Contains(button) && !_buttonsBefore.Contains(button);
        public bool WasReleased(MouseButton button) => !_buttons.Contains(button) && _buttonsBefore.Contains(button);

        /// <summary>
        /// Helper method to start over with nothing held, for when it comes back out.
        /// </summary>
        public void Restart()
        {
            _time = _typingTime = 0.0f;
            _next = _scrolls = 0;
            _keys.Clear();
            _keysBefore.Clear();
            _buttons.Clear();
            _buttonsBefore.Clear();
        }

        /// <summary>
        /// Helper method to move it along an update: what it types, where its pointer is, what it clicks and scrolls.
        /// </summary>
        public void Update(float dt)
        {
            _keysBefore.Clear();
            _keysBefore.UnionWith(_keys);
            _buttonsBefore.Clear();
            _buttonsBefore.UnionWith(_buttons);

            _time += dt;
            _typingTime += dt;

            // Everything that's due by now. Unlike the real keyboard, a key that went down and up inside one update
            // would be lost here; its keys are held for a dozen ticks or so, so it never comes up
            while (_next < _typing.Count && _typing[_next].At <= _typingTime)
            {
                var (_, key, down) = _typing[_next++];
                if (down) _keys.Add(key);
                else _keys.Remove(key);
            }

            if (_typingTime >= _typingLength)
            {
                _typingTime = 0.0f;
                _next = 0;
            }

            // A lazy figure of eight round the middle of the world
            Pointer = new Vector2(90.0f + 300.0f * MathF.Sin(_time * 0.7f), 80.0f + 130.0f * MathF.Sin(_time * 1.4f));

            int click = (int)(_time / CLICK_EVERY);
            _buttons.Clear();
            if (_time - click * CLICK_EVERY < CLICK_HOLD)
                _buttons.Add(Clicks[click % Clicks.Length]);

            // A notch of the wheel every so often, three up then three down
            int scrolls = (int)(_time / SCROLL_EVERY);
            Scroll = scrolls != _scrolls ? (scrolls % 6 < 3 ? 1.0f : -1.0f) : 0.0f;
            _scrolls = scrolls;
        }
    }

    // Where each piece of art is in art.png, in pixels from its top left
    private const string ArtDefinition = """
        let art = {
            file: "art.png",
            sprites: {
                ring:  { x: 0, y: 0, w: 128, h: 128 },
                disc:  { x: 128, y: 0, w: 64, h: 64 },
                pixel: { x: 194, y: 2, w: 4, h: 4 }
            }
        };

        let sheet = {
            images: { art }
        };
        """;

    /// <summary>
    /// Helper method to paint the art: a ring and a disc, both white so they can be tinted any colour and soft round
    /// the edges so they don't look like arse when they're scaled, plus a solid block for the grid lines.
    /// Where each one is has to match <see cref="ArtDefinition"/>.
    /// </summary>
    private static void PaintArt(string path)
    {
        using var image = new Image<Rgba32>(256, 128);

        for (int y = 0; y < 128; y++)
        {
            for (int x = 0; x < 128; x++)
            {
                // A ring 7 pixels thick just inside the edge, with a pixel of fade either side
                float distance = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(64.0f));
                float ring = Math.Clamp(1.0f - (MathF.Abs(distance - 58.0f) - 3.5f), 0.0f, 1.0f);
                image[x, y] = new Rgba32(255, 255, 255, (byte)(ring * 255.0f));
            }
        }

        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                float distance = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(32.0f));
                float disc = Math.Clamp(30.0f - distance, 0.0f, 1.0f);
                image[128 + x, y] = new Rgba32(255, 255, 255, (byte)(disc * 255.0f));
            }
        }

        // The block is 8 square, and only the middle 4 of it are used, so blending at the edges never picks up nothing
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                image[192 + x, y] = new Rgba32(255, 255, 255, 255);

        image.SaveAsPng(path);
    }
}
