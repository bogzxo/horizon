using System.Numerics;

using Horizon.Engine;
using Horizon.Graphics;
using Horizon.Input;
using Horizon.Physics;
using Horizon.Physics.Fixtures;
using Horizon.Physics.Simulation;
using Horizon.Rendering;
using Horizon.Rendering.Lighting;
using Horizon.Rendering.Particles;
using Horizon.Rendering.Spriting;
using Horizon.Rendering.Tiling;
using Horizon.UI;
using Horizon.UI.Components;

using Silk.NET.Input;

namespace Horizon.Testing.Examples.Showcase;

/// <summary>
/// A dungeon seen from above, and the second game in here. The blob is down in the dark with a lantern, three
/// crystals are somewhere in five rooms, and the way out is behind a gate that wants all three. He throws sparks,
/// which light braziers, send slimes on their way and shove the crates about.
/// <para>
/// It is here for the path traced lighting, which a street at night only gets so much out of. Down here nearly
/// nothing is lit by a light somebody put down. The lava lights its room red because it is drawn on a layer that
/// glows, and so do the mushrooms of the cave, the signs cut into the floor and the pool of light at the end.
/// The crystals glow in their own colours and go on glowing once he carries them, so he ends up a lamp of three
/// colours. Slimes glow, a spark lights the passage it flies down and the embers it bursts into each light a bit
/// of floor. All of that is <see cref="LightingMode.PathTraced"/> finding whatever glows and working out what it
/// lights and what the walls throw back, F switches it off to see what is left (the lantern and the braziers)
/// and B shows what the tracer found by itself.
/// </para>
/// <para>
/// The tracer is told how much work to do the way a game's options would tell it, Q goes round how many cascades
/// it works the light out with (<see cref="PathTracedLighting2D.MaxCascades"/>), all of them, four or three, three
/// being about half the cost. What keeps three looking like all of them is the light being laid over the frames
/// before it with the rays turned a little every frame (<see cref="PathTracedLighting2D.Accumulation"/>), which T
/// switches off, to see the flicker it takes out with the camera creeping along and the blotches with fewer cascades.
/// </para>
/// <para>
/// And for the physics. Everything that moves is a body in one <see cref="PhysicsWorld"/> with no gravity, it is
/// the floor that is being looked at. The blob is a <see cref="CharacterController2D"/> seen from above, slimes
/// are bodies that hop by giving themselves a kick, crates are bodies that get going when somebody leans on them
/// (<see cref="PhysicsWorld.BodiesPush"/>), a spark going off is <see cref="PhysicsWorld.ApplyRadialImpulse"/>
/// and shoves the lot, and the embers are particles the world stops at the walls and the bodies.
/// </para>
/// <para>
/// The rest of it is the map (Assets/examples/dungeon/dungeon.tmx, open it in Tiled). Its walls and its lava are
/// what is solid, its walls and pillars are what blocks light, and where the crystals, the slimes, the braziers,
/// the crates, the gate and the way out are is where the map has an object saying so. The parts are in files of
/// their own, <c>DungeonExample.Things.cs</c> for what stands about and <c>DungeonExample.Sparks.cs</c> for what
/// gets thrown.
/// </para>
/// </summary>
public partial class DungeonExample : Scene, ITestControls
{
    private const string MAP = "Assets/examples/dungeon/dungeon.tmx";
    private const string SPRITES = "Assets/examples/dungeon/sprites.png";
    private const string HUD = "Assets/examples/ui/dungeon_hud.hor";

    // How many pixels of the screen a pixel of the art is, and how big a cell of the sprite sheet is
    private const float PIXEL = 3.0f;
    private const int CELL = 16;

    // The blob. How fast he gets about, how many times he can be bitten and how long he is left alone after one
    private const float WALK_SPEED = 76.0f;
    private const int MAX_HEALTH = 5;
    private const float MERCY = 1.1f;

    // The box of him that bumps into things, around his middle, and where his feet are from there
    private static readonly Vector2 HeroBox = new(10.0f, 8.0f);
    private static readonly Vector2 Feet = new(0.0f, -5.0f);

    private static readonly Vector3 Warm = new(1.0f, 0.82f, 0.55f);

    public override Camera ActiveCamera { get; protected set; }

    // Listed on screen by the test host
    public IReadOnlyList<TestControl> Controls { get; } =
    [
        new("W A S D, arrows, stick", "walk"),
        new("Space, left click", "throw a spark, a click throws it at the pointer"),
        new("L", "the lantern"),
        new("F", "path traced lighting on and off"),
        new("B", "what the path tracer found, on its own"),
        new("Q", "how many cascades the light is worked out with, fewer is cheaper"),
        new("T", "the light settling over a few frames, off shows the flicker it takes out"),
    ];

    // What Q goes round, the cascades the light is worked out with. Six is as many as a picture this size takes,
    // under three the far cascade has too few rays for its distance and no amount of frames smooths that over
    private static readonly int[] CascadeChoices = [6, 4, 3];
    private int _cascadeChoice;

    private readonly Camera2D _camera, _screenCamera;
    private readonly TileMap _map;

    // Where the map says things are, read as the scene is made and turned into things once there is a GPU to make them on
    private readonly List<(Vector2 Position, Vector4 Colour)> _crystalSpots = [];
    private readonly List<(Vector2 Position, bool Lit)> _brazierSpots = [];
    private readonly List<Vector2> _slimeSpots = [], _crateSpots = [];
    private readonly List<(Vector2 Min, Vector2 Max, string Says)> _zones = [];
    private (Vector2 Min, Vector2 Size) _gateSpot;
    private (Vector2 Min, Vector2 Max) _exit;
    private Vector2 _spawn;

    private DeferredRenderer2D _world = null!;
    private OcclusionMap2D? _occlusion;
    private PhysicsWorld _physics = null!;
    private PhysicsBodyComponent2D _ground = null!;
    private SpriteSheet _sheet = null!;
    private SpriteBatch _props = null!, _actors = null!;
    private ParticleRenderer2D _embers = null!, _goo = null!, _motes = null!;

    private Sprite _hero = null!;
    private CharacterController2D _walker = null!;
    private Light2D _lantern = null!;

    private ProgressBar _healthBar = null!;
    private Label _found = null!, _notice = null!, _banner = null!;

    private Vector2 _eye;
    private float _time, _mercy, _saidFor;
    private int _health = MAX_HEALTH, _shownCrystals = -1, _shownLit = -1;
    private string _said = string.Empty;
    private bool _won;

    public DungeonExample()
    {
        Vector2 window = Engine.WindowManager.ViewportSize;

        _camera = AddEntity(new Camera2D(window / PIXEL));
        ActiveCamera = _camera;

        // And one that never moves, for what sits on the screen
        _screenCamera = AddEntity(new Camera2D(window));

        _map = TileMap.Load(MAP);

        // The map says where things are, by what kind of thing its objects say they are. What a crystal or a
        // slime is, is this example's business and further down
        _map.DispatchObjects(objects => objects
            .OfClass("spawn", found => _spawn = found.Position)
            .OfClass("crystal", found => _crystalSpots.Add((found.Position, found.Properties.GetColor("colour", Vector4.One))))
            .OfClass("brazier", found => _brazierSpots.Add((found.Position, found.Properties.GetBool("lit"))))
            .OfClass("slime", found => _slimeSpots.Add(found.Position))
            .OfClass("crate", found => _crateSpots.Add(found.Position))
            .OfClass("gate", found => _gateSpot = (found.Min, found.Size))
            .OfClass("exit", found => _exit = (found.Min, found.Min + found.Size))
            .OfClass("zone", found => _zones.Add((found.Min, found.Min + found.Size, found.Properties.GetString("says")))));

        _eye = _spawn;
    }

    public override void Initialize()
    {
        Vector2 window = Engine.WindowManager.ViewportSize;

        /* The world, lit by whatever in it glows */

        _world = AddEntity(new DeferredRenderer2D((uint)window.X, (uint)window.Y)
        {
            FollowWindow = true,
            ClearColor = _map.BackgroundColor ?? new Vector4(0.01f, 0.01f, 0.02f, 1.0f),

            // Next to none. What can be seen down here is what something lights
            Ambient = World.AmbientOf(_map),
            Lighting = LightingMode.PathTraced,

            // The art is a unit a pixel, and it is lit a pixel of the art at a time
            LightingPixelSize = 1.0f
        });
        _world.AmbientOcclusion.Enabled = true;

        // A wall keeps most of what falls on it and throws it back, which is what carries the red of the lava
        // round a corner and up a passage. And four times the traced light it comes out with by itself. A glow
        // is at most as bright as white paint as far as the picture goes, and lava is a good deal brighter than
        // paint, so the difference is made up here, where it is light and not a colour on the screen yet
        _world.PathTracing.Bounce = 0.85f;
        _world.PathTracing.Strength = 4.0f;

        _world.AddEntity(_map);

        // What blocks light, the walls and the pillars by the shape they are drawn (a pillar is round)
        _world.Occlusion = _occlusion = _map.CreateOcclusion();

        /* The physics, one world for everything and nothing pulling on any of it */

        _physics = AddComponent<PhysicsWorld>();
        _physics.BodiesPush = true;

        _ground = _physics.CreateBody(PhysicsBodySimulationType.Static);
        foreach (TileMapBox box in _map.BuildColliders())
            _ground.CreateRectangularFixture(box.Min, box.Size);

        /* What stands about, then who walks about, then what flies about, in the order they are drawn */

        if (!Engine.ObjectManager.Textures.TryCreate(
                new TextureDescription { Paths = [SPRITES], Definition = TextureDefinition.RgbaUnsignedByteNearest },
                out var texture))
        {
            throw new Exception($"Couldn't make a texture out of {SPRITES}: {texture.Message}");
        }

        _sheet = SpriteSheet.FromTexture(texture.Asset, new Vector2(CELL));

        _props = _world.AddEntity(new SpriteBatch());
        _actors = _world.AddEntity(new SpriteBatch());

        MakeBraziers();
        MakeGate();
        MakeCrystals();
        MakeCrates();
        MakeSlimes();
        MakeHero();
        MakeSparks();

        /* The HUD, the scene's own and so over everything */

        var hud = AddComponent(new UICompositor(_screenCamera));
        // hud.Retained
        var layout = hud.CreateModule().LoadLayout(HUD);
        _healthBar = layout.Get<ProgressBar>("health");
        _found = layout.Get<Label>("found");
        _notice = layout.Get<Label>("notice");
        _banner = layout.Get<Label>("banner");

        base.Initialize();
    }

    /// <summary>
    /// Helper method to make the blob, a body in the world with somebody steering it from above, and the lantern he
    /// carries. The lantern is one of the few lights down here that is a light, and it throws shadows, the pillars
    /// swing theirs round as he walks past, which is the look of the thing.
    /// </summary>
    private void MakeHero()
    {
        // He throws no shadow, and it is not for the look of it. What blocks light is also what throws light back,
        // and something a hand away from a lantern throws back a great deal from very little room, which the
        // tracer draws as a star of spokes round him wherever he goes
        _hero = World.CreateBlob(_actors);

        _walker = AddComponent(new CharacterController2D(_physics, _spawn, HeroBox, topDown: true)
        {
            MaxSpeed = WALK_SPEED,
            Acceleration = 520.0f,
            Deceleration = 600.0f
        });

        _lantern = _world.AddLight(new Light2D
        {
            Color = Warm,
            Radius = 96.0f,
            Intensity = 0.9f,
            Size = 3.0f,
            Flicker = 0.06f,

            // The walls and the pillars throw shadows from it and the sprites do not. It is in the middle of the
            // one that carries it, and a slime or a crate an arm away would black out half the room
            SpriteShadow = 0.0f
        });
    }

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);

        _time += dt;
        _mercy = MathF.Max(0.0f, _mercy - dt);

        var keyboard = Engine.Input.Keyboard;
        var mouse = Engine.Input.Mouse;
        var pad = Engine.Input.Gamepads.LastUsed;

        if (keyboard.WasPressed(Key.F)) _world.Lighting = _world.Lighting == LightingMode.Direct ? LightingMode.PathTraced : LightingMode.Direct;
        if (keyboard.WasPressed(Key.B)) _world.ShowTracedLight = !_world.ShowTracedLight;
        if (keyboard.WasPressed(Key.L)) _lantern.Enabled = !_lantern.Enabled;

        if (keyboard.WasPressed(Key.Q))
        {
            _cascadeChoice = (_cascadeChoice + 1) % CascadeChoices.Length;
            _world.PathTracing.MaxCascades = CascadeChoices[_cascadeChoice];
            Say(_cascadeChoice == 0 ? "the light is worked out with every cascade it takes" : $"the light is worked out with {CascadeChoices[_cascadeChoice]} cascades", 2.5f);
        }

        if (keyboard.WasPressed(Key.T))
        {
            bool settling = _world.PathTracing.Accumulation <= 0.0f;
            _world.PathTracing.Accumulation = settling ? 0.1f : 0.0f;
            Say(settling ? "the light settles over a tenth of a second" : "every frame's light on its own", 2.5f);
        }

        // Which way he is being steered, from whatever is being held. The controller makes a walk of it
        Vector2 steer = new(
            keyboard.Axis(Key.A, Key.D) + keyboard.Axis(Key.Left, Key.Right),
            keyboard.Axis(Key.S, Key.W) + keyboard.Axis(Key.Down, Key.Up));
        if (pad is not null)
        {
            steer += pad.LeftStick;
            steer.X += (pad.IsDown(GamepadInput.DPadRight) ? 1.0f : 0.0f) - (pad.IsDown(GamepadInput.DPadLeft) ? 1.0f : 0.0f);
            steer.Y += (pad.IsDown(GamepadInput.DPadUp) ? 1.0f : 0.0f) - (pad.IsDown(GamepadInput.DPadDown) ? 1.0f : 0.0f);
        }

        _walker.Move = Vector2.Clamp(steer, -Vector2.One, Vector2.One);

        // A click throws it at the pointer, a key throws it the way he is facing
        _sparkDue = MathF.Max(0.0f, _sparkDue - dt);
        if (mouse.WasPressed(MouseButton.Left))
            Throw(ActiveCamera.ScreenToWorld(mouse.Position) - _walker.Position);
        else if (keyboard.WasPressed(Key.Space) || pad?.WasPressed(GamepadInput.X) == true || pad?.WasPressed(GamepadInput.A) == true)
            Throw(_walker.Facing);

        ShowHero();
        MoveSparks(dt);
        MoveSlimes(dt);
        ShowCrates();
        CarryCrystals(dt);
        TendBraziers(dt);
        WatchTheGate(dt);
        FollowHero(dt);
        ShowHud(dt);
    }

    /// <summary>Helper method to put the sprite and the lantern where the body is. The world moved it, this only looks the part.</summary>
    private void ShowHero()
    {
        Vector2 middle = _walker.Position;

        // The sprite stands on its position (Origin.Bottom), the body is around his middle
        _hero.Transform.Position = middle + Feet;
        if (MathF.Abs(_walker.Facing.X) > 0.3f) _hero.Flipped = _walker.Facing.X < 0.0f;
        _hero.SetAnimation(_walker.Velocity.LengthSquared() > 36.0f ? "hop" : "idle");

        // See through while nothing can bite him, which is how everybody knows it
        _hero.Tint = new Vector4(1.0f, 1.0f, 1.0f, _mercy > 0.0f && (int)(_time * 14.0f) % 2 == 0 ? 0.45f : 1.0f);

        _lantern.Position = middle + new Vector2(0.0f, 3.0f);
    }

    /// <summary>
    /// Helper method for the blob being bitten. He is knocked off the way he was bitten from, which is a shove like
    /// any other to the body, and left alone for a moment. Bitten enough he wakes up where he started with what
    /// he had found, this is an example and not a punishment.
    /// </summary>
    private void Hurt(Vector2 from)
    {
        if (_mercy > 0.0f || _won)
            return;

        _mercy = MERCY;
        _health--;

        Vector2 away = _walker.Position - from;
        _walker.Push(away.LengthSquared() > 0.01f ? Vector2.Normalize(away) * 150.0f : Vector2.UnitY * 150.0f);
        _hero.Flash(new Vector4(1.0f, 0.3f, 0.3f, 1.0f), 0.45f);
        _goo.AddBurst(_walker.Position, 14, 60);

        if (_health > 0)
            return;

        _health = MAX_HEALTH;
        _walker.Teleport(_spawn);
        _eye = _spawn;
        Say("the dark got him, and here he is again", 4.0f);
    }

    /// <summary>Helper method to have the camera go after him, smoothly, and never look past the edge of the map.</summary>
    private void FollowHero(float dt)
    {
        _eye = Vector2.Lerp(_eye, _walker.Position, 1.0f - MathF.Exp(-6.0f * dt));

        Vector2 half = _camera.ViewSize / 2.0f;
        _eye = Vector2.Clamp(_eye, _map.Min + half, _map.Max - half);
        _camera.Position = new Vector3(_eye, 0.0f);
    }

    /// <summary>Helper method to have something said along the top for a while, over whatever the room says.</summary>
    private void Say(string what, float seconds)
    {
        _said = what;
        _saidFor = seconds;
    }

    /// <summary>
    /// Helper method to keep the HUD up to date. A text is only written when it is another text, a label that is
    /// told the same thing a hundred and twenty times a second makes a hundred and twenty strings of it.
    /// </summary>
    private void ShowHud(float dt)
    {
        _healthBar.Progress = _health / (float)MAX_HEALTH;

        int carried = Carried(), lit = Lit();
        if (carried != _shownCrystals || lit != _shownLit)
        {
            (_shownCrystals, _shownLit) = (carried, lit);
            _found.Text = $"crystals {carried} of {_crystals.Count}    braziers {lit} of {_braziers.Count}";
        }

        // What was said lately, or what the room he is in has to say
        string says = string.Empty;
        _saidFor -= dt;
        if (_saidFor > 0.0f)
        {
            says = _said;
        }
        else
        {
            Vector2 at = _walker.Position;
            foreach (var (min, max, text) in _zones)
            {
                if (at.X >= min.X && at.X <= max.X && at.Y >= min.Y && at.Y <= max.Y) says = text;
            }
        }

        if (!ReferenceEquals(_notice.Text, says)) _notice.Text = says;
    }

    protected override void DisposeOther()
    {
        // The occlusion map belongs to whoever asked for it
        _occlusion?.Dispose();
        base.DisposeOther();
    }
}
