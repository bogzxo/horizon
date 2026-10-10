using System.Numerics;

using Horizon.Core.Tweening;
using Horizon.Engine;
using Horizon.Input;
using Horizon.Physics;
using Horizon.Physics.Simulation;
using Horizon.Rendering;
using Horizon.Rendering.Lighting;
using Horizon.Rendering.Particles;
using Horizon.Rendering.PostProcessing;
using Horizon.Rendering.Spriting;
using Horizon.Rendering.Tiling;
using Horizon.UI;
using Horizon.UI.Components;

using Silk.NET.Input;

namespace Horizon.Testing.Examples.Showcase;

/// <summary>
/// Everything the other examples show, in one scene, the way a game has it. The street of the tile map example
/// with its lamps lit and its walls throwing shadows, a blob you walk and jump along it who throws one of his own,
/// rain that lands on the roofs, the ground and him, a HUD out of a layout file, and the glass of an old telly
/// over the world. There is nothing new in here, which is the point, it is the other examples added to one another
/// and each part is a few lines because the engine does the rest.
/// <para>
/// How it is put together, from the outside in. A <see cref="Renderer2D"/> is the screen and has the tube on it.
/// In it a <see cref="DeferredRenderer2D"/> is the world, lit. In that go the map, a batch with the blob, the rain
/// and the map's front layer, in the order they are drawn. The HUD is the scene's own and drawn after the screen,
/// so it is in front of the glass. A tube bends and smears whatever is behind it, which suits a street at night
/// and makes small print something to squint at. The map says where the player starts, where the lamps hang and
/// what its signs read, and its solid layer is the walls the lamps are blocked by and, as one body in a
/// <see cref="PhysicsWorld"/>, what the blob stands on and the rain lands on.
/// </para>
/// <para>
/// The blob is a body in that world as well, pushed about by a <see cref="CharacterController2D"/>. The keys don't
/// set how fast he goes, they push, so he gets up to speed, slows to a stop and skids when he turns round, and
/// his jump is as high as the button is held for. The world does the rest, stops him at the walls, lands him on
/// the roofs and has the rain bounce off his head.
/// </para>
/// </summary>
public class TownExample : Scene, ITestControls
{
    private const string HUD = "Assets/examples/ui/town_hud.hor";

    // How many pixels of the screen a pixel of the art is
    private const float PIXEL = 3.0f;

    // How the blob gets about, in units (art pixels) a second, and how hard the street pulls him down. How quickly
    // he gets up to that speed and back down from it is the controller's to say, see CharacterController2D
    private const float WALK_SPEED = 88.0f;
    private const float JUMP_SPEED = 225.0f;
    private const float GRAVITY = 620.0f;

    // How fast he has to come down for it to show
    private const float HARD_LANDING = 150.0f;

    // The box of him that bumps into things, a bit narrower than he is drawn
    private static readonly Vector2 Body = new(10.0f, 10.0f);

    // Where his lantern is, from his feet, which is his middle
    private static readonly Vector2 LanternHold = new(0.0f, 6.0f);

    private const float RAIN_RATE = 260.0f;     // drops a second
    private const float SIGN_REACH = 20.0f;

    public override Camera ActiveCamera { get; protected set; }

    // Listed on screen by the test host
    public IReadOnlyList<TestControl> Controls { get; } =
    [
        new("A / D, arrows, stick", "walk"),
        new("Space", "jump, held for a higher one"),
        new("L", "the lantern he carries"),
        new("F", "path traced lighting"),
        new("R", "rain"),
        new("C", "the picture tube"),
    ];

    private readonly Camera2D _camera, _screenCamera;
    private readonly TileMap _map;

    private Renderer2D _screen = null!;
    private DeferredRenderer2D _world = null!;
    private OcclusionMap2D? _occlusion;
    private CrtEffect _tube = null!;
    private Sprite _hero = null!;
    private CharacterController2D _walker = null!;
    private Vector2 _heroSize;
    private Light2D _lantern = null!;
    private ParticleRenderer2D _rain = null!;
    private ProgressBar _street = null!;
    private Label _notice = null!;

    private readonly List<(Vector2 Min, Vector2 Max, string Says)> _zones = [];
    private readonly List<(Vector2 Position, string Says)> _signs = [];

    private Vector2 _position, _eye;
    private bool _raining = true;
    private float _rainDue;

    public TownExample()
    {
        Vector2 window = Engine.WindowManager.ViewportSize;

        // The world's camera. It glides, a pixel of the art is three of the screen and a camera that only ever
        // stands on whole art pixels (PixelSnap, see the camera example) moves in steps of three, which reads as
        // a stutter at walking pace
        _camera = AddEntity(new Camera2D(window / PIXEL));
        ActiveCamera = _camera;

        // And one that never moves, for what sits on the screen
        _screenCamera = AddEntity(new Camera2D(window));

        _map = TileMap.Load(World.TOWN);

        // The map says where things are, the game says what they mean. A spawn point, zones that have something
        // to say when they are walked into, and signs, which are tiles put down as objects and bring the
        // properties their tile has in the tile set
        _map.DispatchObjects(objects => objects
            .OfClass("spawn", spawn => _position = spawn.Position)
            .OfClass("zone", zone => _zones.Add((zone.Min, zone.Min + zone.Size, $"under the {zone.Name}, out of the rain")))
            .OfClass("sign", sign => _signs.Add((sign.Position, sign.Properties.GetString("label", "a sign")))));

        _eye = _position;
        _map.ParallaxOrigin = _position;
    }

    public override void Initialize()
    {
        Vector2 window = Engine.WindowManager.ViewportSize;
        uint width = (uint)window.X, height = (uint)window.Y;
        Vector4 sky = _map.BackgroundColor ?? new Vector4(0.1f, 0.13f, 0.24f, 1.0f);

        /* The screen, and the glass over it */

        _screen = AddEntity(new Renderer2D(width, height) { ClearColor = sky });
        _tube = _screen.PostProcessing.Add(new CrtEffect { PixelSize = PIXEL, Warp = new Vector2(1.0f / 64.0f, 1.0f / 48.0f) });

        /* The world, lit */

        _world = _screen.AddEntity(new DeferredRenderer2D(width, height)
        {
            ClearColor = sky,
            Ambient = World.AmbientOf(_map),

            // The art is a unit a pixel, and it is lit a pixel of the art at a time
            LightingPixelSize = 1.0f
        });
        _world.AmbientOcclusion.Enabled = true;

        _world.AddEntity(_map);

        var actors = _world.AddEntity(new SpriteBatch());
        _hero = World.CreateBlob(actors);
        _hero.CastsShadows = true;
        _heroSize = _hero.Transform.Size;

        // One layer of the map, two jobs. What the lamps are blocked by...
        _world.Occlusion = _occlusion = _map.CreateOcclusion();

        // ...and, as one body in a physics world, what the blob stands on and the rain lands on. The street pulls
        // down on whatever is a body in it, the rain has a gravity of its own because rain is a lot floatier
        var physics = AddComponent<PhysicsWorld>();
        physics.Gravity = new Vector2(0.0f, -GRAVITY);

        var ground = physics.CreateBody(PhysicsBodySimulationType.Static);
        foreach (TileMapBox box in _map.BuildColliders())
            ground.CreateRectangularFixture(box.Min, box.Size);

        // The two ends of the street, walls nobody sees and a good way taller than anybody jumps
        var wall = new Vector2(16.0f, _map.Size.Y + 600.0f);
        ground.CreateRectangularFixture(new Vector2(_map.Min.X - wall.X, _map.Min.Y - 200.0f), wall);
        ground.CreateRectangularFixture(new Vector2(_map.Max.X, _map.Min.Y - 200.0f), wall);

        // And the blob, a body in the same world with somebody steering it. He is squashed as he lands and
        // stretched as he jumps, which is the two lines that make a box with a face on it look like it is alive
        _walker = AddComponent(new CharacterController2D(physics, _position, Body) { MaxSpeed = WALK_SPEED, JumpSpeed = JUMP_SPEED });
        _walker.Jumped += () => Squash(new Vector2(0.8f, 1.22f), 0.24f);
        _walker.Landed += speed =>
        {
            if (speed < HARD_LANDING) return;

            Squash(new Vector2(1.28f, 0.72f), 0.3f);

            // What he lands in when it is raining, out to both sides
            if (_raining) _rain.AddCone(_walker.Position + new Vector2(0.0f, 1.0f), Vector2.UnitY, MathF.PI * 0.8f, 10, 70);
        };

        _rain = _world.AddEntity(new ParticleRenderer2D(4096, new PhysicsParticleSimulator2D(physics) { Radius = 1.0f, Restitution = 0.25f, Friction = 6.0f })
        {
            StartColor = new Vector3(0.75f, 0.85f, 1.0f),
            EndColor = new Vector3(0.3f, 0.4f, 0.6f),
            ParticleSize = 1.0f,
            MaxAge = 2.6f,
            Gravity = new Vector2(0, -500)
        });

        // In front of the blob and the rain, the vines over the porch
        _world.AddEntity(_map.Foreground);

        World.AddLights(_map, _world);
        _lantern = _world.AddLight(new Light2D
        {
            Color = new Vector3(1.0f, 0.85f, 0.6f),
            Radius = 70.0f,
            Intensity = 0.9f,
            Size = 3.0f,

            // So there is something to see where it hangs, the light itself is not a thing that is drawn
            Glow = 0.35f,

            // A light can be told to throw no shadows at all, and one that is carried about wants to be. It is
            // right up against him and an arm's length off every wall he walks past, shadows from there are
            // huge, swing about with every step and tell nobody anything. The lamps of the street do the shadows
            CastsShadows = false
        });

        /* The HUD, in front of the glass. It is the scene's and not the screen's, so it is drawn after the screen
           is, tube and all, straight onto the frame. Behind the glass it was scan lines and a bend through every
           letter, and a HUD nobody can read is decoration */

        var hud = AddComponent(new UICompositor(_screenCamera));
        var layout = hud.CreateModule().LoadLayout(HUD);
        _street = layout.Get<ProgressBar>("street");
        _notice = layout.Get<Label>("notice");

        base.Initialize();
    }

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);

        var keyboard = Engine.Input.Keyboard;
        var pad = Engine.Input.Gamepads.LastUsed;

        if (keyboard.WasPressed(Key.F)) _world.Lighting = _world.Lighting == LightingMode.Direct ? LightingMode.PathTraced : LightingMode.Direct;
        if (keyboard.WasPressed(Key.C)) _tube.Enabled = !_tube.Enabled;
        if (keyboard.WasPressed(Key.R)) _raining = !_raining;
        if (keyboard.WasPressed(Key.L)) _lantern.Enabled = !_lantern.Enabled;

        // What the player wants, which is all the keys are. A stick held halfway is a walk at half the speed
        float walk = keyboard.Axis(Key.A, Key.D) + keyboard.Axis(Key.Left, Key.Right) + (pad?.LeftStick.X ?? 0.0f)
            + (pad?.IsDown(GamepadInput.DPadRight) == true ? 1.0f : 0.0f) - (pad?.IsDown(GamepadInput.DPadLeft) == true ? 1.0f : 0.0f);

        _walker.Move = new Vector2(Math.Clamp(walk, -1.0f, 1.0f), 0.0f);
        _walker.HoldingJump = keyboard.IsDown(Key.Space) || keyboard.IsDown(Key.Up) || keyboard.IsDown(Key.W) || pad?.IsDown(GamepadInput.A) == true;

        if (keyboard.WasPressed(Key.Space) || keyboard.WasPressed(Key.Up) || keyboard.WasPressed(Key.W) || pad?.WasPressed(GamepadInput.A) == true)
            _walker.Jump();

        ShowHero();
        FollowHero(dt);
        Rain(dt);
        ReadTheRoom();
    }

    /// <summary>
    /// Helper method to put the sprite where the body is. The world moved it, in its physics step, all there is
    /// left to do is look the part.
    /// </summary>
    private void ShowHero()
    {
        _position = _walker.Position;

        // The sprite's position is where his feet are (Origin.Bottom) and so is the body's, and the art faces right
        _hero.Transform.Position = _position;
        _hero.Flipped = _walker.Facing.X < 0.0f;

        // Stood still is on the ground and all but stopped. Sliding to a halt is still hopping, he has not got there yet
        bool still = _walker.Grounded && MathF.Abs(_walker.Velocity.X) < 8.0f;
        _hero.SetAnimation(still ? "idle" : "hop");

        // In the middle of him. It throws no shadows, so being inside the sprite that carries it is no trouble
        _lantern.Position = _position + LanternHold;
    }

    /// <summary>
    /// Helper method to pull the blob out of shape and let him spring back to the size he is. Out of his own size
    /// every time and not out of the one he has right now, or a hop straight after a landing settles a bit fatter
    /// than he started and ten hops on he is a pancake.
    /// </summary>
    private void Squash(Vector2 by, float duration)
    {
        _hero.Transform.Size = _heroSize * by;
        _hero.TweenSize(_heroSize, duration).SetEasing(Easing.OutBack);
    }

    /// <summary>Helper method to have the camera go after him, smoothly, and never look past the edge of the map.</summary>
    private void FollowHero(float dt)
    {
        // A bit above him, there is more to see up there than in the ground
        Vector2 target = _position + new Vector2(0.0f, 40.0f);
        _eye = Vector2.Lerp(_eye, target, 1.0f - MathF.Exp(-5.0f * dt));

        Vector2 half = _camera.ViewSize / 2.0f;
        _eye = Vector2.Clamp(_eye, _map.Min + half, _map.Max - half);
        _camera.Position = new Vector3(_eye, 0.0f);
    }

    /// <summary>Helper method to let it rain over whatever the camera sees, from just above the top of the picture.</summary>
    private void Rain(float dt)
    {
        if (!_raining) return;

        _rainDue += RAIN_RATE * dt;
        Vector2 half = _camera.ViewSize / 2.0f;

        while (_rainDue >= 1.0f)
        {
            _rainDue -= 1.0f;

            float x = _eye.X + (Random.Shared.NextSingle() * 2.0f - 1.0f) * (half.X + 40.0f);
            _rain.AddCone(new Vector2(x, _eye.Y + half.Y + 8.0f), new Vector2(-0.15f, -1.0f), 0.04f, 1, 260);
        }
    }

    /// <summary>Helper method to put what the map has to say about where he is on the HUD.</summary>
    private void ReadTheRoom()
    {
        _street.Progress = (_position.X - _map.Min.X) / _map.Size.X;

        string says = string.Empty;

        foreach (var (min, max, text) in _zones)
        {
            if (_position.X > min.X && _position.X < max.X && _position.Y >= min.Y - 1.0f && _position.Y < max.Y) says = text;
        }

        foreach (var (position, text) in _signs)
        {
            if (Vector2.Distance(position, _position) < SIGN_REACH) says = $"the sign says \"{text}\"";
        }

        _notice.Text = says;
    }

    protected override void DisposeOther()
    {
        _occlusion?.Dispose();
        base.DisposeOther();
    }
}
