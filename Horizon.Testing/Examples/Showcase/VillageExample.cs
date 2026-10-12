using System.Numerics;

using Horizon.Engine;
using Horizon.Graphics;
using Horizon.Input;
using Horizon.Physics;
using Horizon.Physics.Simulation;
using Horizon.Rendering;
using Horizon.Rendering.Lighting;
using Horizon.Rendering.PostProcessing;
using Horizon.Rendering.Spriting;
using Horizon.Rendering.Tiling;
using Horizon.UI;
using Horizon.UI.Components;

using Silk.NET.Input;

namespace Horizon.Testing.Examples.Showcase;

/// <summary>
/// A small town seen from above, the way the old pocket monster games had them, with a sun that goes round and
/// buildings you can walk into. Two houses, the professor's lab and a cave up in the rocks, a few villagers
/// wandering the paths with something to say, and a day that goes by in a few minutes, the shadows of the pines
/// and the houses swinging round with it, the windows coming on at dusk and the fireflies coming out over the pond.
/// <para>
/// The thing to look at is <see cref="TileMapRooms"/>. The town is one map (Assets/examples/village/village.tmx)
/// and every building's inside is a map of its own, and all a door is is an object on the map with a file
/// property saying which map is behind it. Step into one and the rooms place the map behind it so that its
/// doormat lies where you are standing, fade it up over the street and fade the street down to a shadow of itself
/// underneath, and hand its walls to the lighting and the physics. The camera never cuts and there is no loading
/// screen, you are simply indoors now. Walk down off the doormat and you are out again. Everything the scene has
/// to do about it is in <see cref="Arrive"/>, which is told who went through which door.
/// </para>
/// <para>
/// The lighting is the path traced kind from the start (<see cref="LightingMode.PathTraced"/>). The sun is one
/// <see cref="LightType.Directional"/> light turned by the clock (see <c>VillageExample.Sky.cs</c>), long shadows
/// at either end of the day and short ones at noon, the moon takes over at night, and the windows are point
/// lights with a glow of their own that only come on in the dark. Indoors the lamp of the room is all there is
/// and its walls are what blocks it, the tracer bounces it round the furniture. Everybody throws a shadow as
/// sharp as their pixels (<see cref="Sprite.CastsShadows"/>), a short one, things have a height here
/// (<see cref="DeferredRenderer2D.WallHeight"/> and <see cref="DeferredRenderer2D.SpriteHeight"/>) and a lamp
/// hangs over them. The light is in the air as well as on things, a haze of what the tracer finds and the lamps'
/// glow spread through it, shadowed, so a window throws a soft shaft (<see cref="PathTracedLighting2D.Fog"/>
/// and <see cref="PathTracedLighting2D.GlowSpread"/>, H). The finished picture goes through a
/// <see cref="BloomEffect"/> (V), a glow round whatever the lamps drive past white, and an
/// <see cref="ExposureEffect"/> (G), a gentle gamma and an eye that gets used to the dark some of the way, which
/// is what keeps a lit window at night from being a white hole in a black street.
/// </para>
/// <para>
/// The villagers (<c>VillageExample.People.cs</c>) walk a tile at a time, the way they always have, and are
/// bodies in the physics world while they are in the same room as you, so you bump into them and they stop for
/// you. The art is two packs off itch.io, see the README next to the maps for who made what.
/// </para>
/// </summary>
public partial class VillageExample : Scene, ITestControls
{
    private const string VILLAGE = "Assets/examples/village/village.tmx";
    private const string HERO = "Assets/examples/village/hero.png";
    private const string VILLAGERS = "Assets/examples/village/npc.png";
    private const string HUD = "Assets/examples/ui/village_hud.hor";

    // How many pixels of the screen a pixel of the art is, and how big a cell of the sprite sheets is
    private const float PIXEL = 4.0f;
    private const int CELL = 16;

    // How fast the hero gets about, in units (art pixels) a second, a tile every quarter of a second or so
    private const float WALK_SPEED = 64.0f;

    // The box of him that bumps into things, around his middle, and where his feet are from there
    private static readonly Vector2 HeroBox = new(10.0f, 8.0f);
    private static readonly Vector2 Feet = new(0.0f, -6.0f);

    // How near somebody has to be to be talked to, and how long what they say stays up
    private const float EARSHOT = 22.0f;
    private const float SAID_FOR = 4.5f;

    public override Camera ActiveCamera { get; protected set; }

    // Listed on screen by the test host
    public IReadOnlyList<TestControl> Controls { get; } =
    [
        new("W A S D, arrows, stick", "walk, into a door to go in"),
        new("E, Space, pad A", "talk to whoever is next to you"),
        new("T, pad Y", "the day goes by twelve times as fast"),
        new("N, pad X", "skip to the next part of the day"),
        new("F, pad Start", "path traced lighting on and off"),
        new("B, pad Back", "what the path tracer found, on its own"),
        new("O, right bumper", "ambient occlusion on and off"),
        new("G, left bumper", "the exposure and gamma on and off, the eye getting used to the dark"),
        new("V, left stick", "the bloom on and off"),
        new("H, right stick", "the light in the air on and off, the haze and the lamps' glow through it"),
    ];

    private readonly Camera2D _camera, _screenCamera;
    private readonly TileMapRooms _rooms;

    private DeferredRenderer2D _world = null!;
    private ExposureEffect _exposure = null!;
    private BloomEffect _bloom = null!;
    private PhysicsWorld _physics = null!;
    private PhysicsBodyComponent2D _ground = null!;
    private SpriteBatch _actors = null!;

    private Label _placeName = null!, _clock = null!, _notice = null!;

    private Vector2 _spawn, _eye;
    private float _saidFor;
    private string _said = string.Empty;
    private TileMapRoom? _shownRoom;

    public VillageExample()
    {
        Vector2 window = Engine.WindowManager.ViewportSize;

        _camera = AddEntity(new Camera2D(window / PIXEL));
        ActiveCamera = _camera;

        // And one that never moves, for what sits on the screen
        _screenCamera = AddEntity(new Camera2D(window));

        // The town and everything behind its doors, read in one go. Where the hero starts is an object of the town
        _rooms = new TileMapRooms(VILLAGE) { FadeTime = 0.45f, Dim = 0.22f };
        _rooms.Outside.Map.DispatchObjects(objects => objects.OfClass("spawn", found => _spawn = found.Position));
        _rooms.Entered += Arrive;

        _eye = _spawn;
    }

    public override void Initialize()
    {
        Vector2 window = Engine.WindowManager.ViewportSize;

        /* The world, lit the fancy way from the off */

        _world = AddEntity(new DeferredRenderer2D((uint)window.X, (uint)window.Y)
        {
            FollowWindow = true,
            ClearColor = _rooms.Outside.Map.BackgroundColor ?? new Vector4(0.04f, 0.1f, 0.04f, 1.0f),
            Lighting = LightingMode.PathTraced,

            // The art is a unit a pixel, and it is lit a pixel of the art at a time
            LightingPixelSize = 1.0f,

            // The edges of the shadows, in world units. Pixel art wants them tighter than the default
            ShadowSoftness = 5.0f,

            // Seen from above things have a height, and a lamp hangs over them. A house is taller than a
            // person by a good bit, so the houses throw long shadows under the low sun and the villagers short
            // ones, and a ceiling lamp throws a step of shadow at your feet and not one across the room. What
            // the walls of a room are is the map's to say (WallHeight), a table is nothing like a house
            WallHeight = WallHeightOf(_rooms.Outside),
            SpriteHeight = 14.0f
        });
        _world.AmbientOcclusion.Enabled = true;
        _world.PathTracing.Bounce = 0.7f;
        _world.PathTracing.Strength = 1.3f;

        // From above a person is not a wall to the bounced light, they stand under it like the floor does. With
        // this on everybody trailed a shadow of it the length of the room
        _world.PathTracing.SpritesBlock = false;

        // The air. Some of the light the tracer finds hangs in the air as a haze whatever is under it, and the
        // lamps' glow is worked out shadowed and smeared through the air by the tracer rather than put down hard
        // edged by the deferred pass, so a window throws a soft shaft and a ceiling lamp a soft pool
        _world.PathTracing.Fog = 0.25f;
        _world.PathTracing.GlowSpread = 12.0f;

        // The picture brought to a brightness before it is shown, twice over. A glow round whatever the lamps
        // drive past white (a lit window, a screen), then a gentle gamma that lifts the dark end so a lit window
        // at night sits on a street you can see rather than on black, and an eye that gets used to the dark
        // some of the way (never all of it, a night is a night), over a second or so. See BloomEffect and
        // ExposureEffect, the bloom goes first so its glow is rolled off with the rest
        _bloom = _world.PostProcessing.Add(new BloomEffect { Threshold = 0.7f, Intensity = 0.4f, Levels = 5 });
        _exposure = _world.PostProcessing.Add(new ExposureEffect
        {
            Gamma = 1.12f,
            Knee = 0.8f,
            Adaptation = 0.35f,
            MostAdapted = 1.35f
        });

        /* The physics, one world from above, nothing pulls on anything */

        _physics = AddComponent<PhysicsWorld>();
        _ground = _physics.CreateBody(PhysicsBodySimulationType.Static);

        // The rooms are drawn first, then whoever walks about them, then whatever the rooms have in front
        _rooms.Lighting = _world;
        _rooms.Ground = _ground;
        _world.AddEntity(_rooms);
        _actors = _world.AddEntity(new SpriteBatch());
        _world.AddEntity(_rooms.Foreground);

        MakeSky();
        MakePeople();
        MakeFireflies();

        /* The HUD, the scene's own and so over everything */

        var hud = AddComponent(new UICompositor(_screenCamera));
        var layout = hud.CreateModule().LoadLayout(HUD);
        _placeName = layout.Get<Label>("place_name");
        _clock = layout.Get<Label>("clock");
        _notice = layout.Get<Label>("notice");

        Say("W A S D to walk, E to talk, the doors are the dark bits at the bottom of the buildings", 7.0f);
        base.Initialize();
    }

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);

        var keyboard = Engine.Input.Keyboard;
        var pad = Engine.Input.Gamepads.LastUsed;

        if (keyboard.WasPressed(Key.F) || pad?.WasPressed(GamepadInput.Start) == true) _world.Lighting = _world.Lighting == LightingMode.Direct ? LightingMode.PathTraced : LightingMode.Direct;
        if (keyboard.WasPressed(Key.B) || pad?.WasPressed(GamepadInput.Back) == true) _world.ShowTracedLight = !_world.ShowTracedLight;
        if (keyboard.WasPressed(Key.O) || pad?.WasPressed(GamepadInput.RightBumper) == true) _world.AmbientOcclusion.Enabled = !_world.AmbientOcclusion.Enabled;
        if (keyboard.WasPressed(Key.G) || pad?.WasPressed(GamepadInput.LeftBumper) == true) _exposure.Enabled = !_exposure.Enabled;
        if (keyboard.WasPressed(Key.V) || pad?.WasPressed(GamepadInput.LeftStick) == true) _bloom.Enabled = !_bloom.Enabled;
        if (keyboard.WasPressed(Key.H) || pad?.WasPressed(GamepadInput.RightStick) == true)
        {
            bool haze = _world.PathTracing.Fog > 0.0f;
            _world.PathTracing.Fog = haze ? 0.0f : 0.25f;
            _world.PathTracing.GlowSpread = haze ? 0.0f : 12.0f;
        }
        if (keyboard.WasPressed(Key.T) || pad?.WasPressed(GamepadInput.Y) == true) _hurry = !_hurry;
        if (keyboard.WasPressed(Key.N) || pad?.WasPressed(GamepadInput.X) == true) SkipAhead();

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

        if (keyboard.WasPressed(Key.E) || keyboard.WasPressed(Key.Space) || pad?.WasPressed(GamepadInput.A) == true)
            Talk();

        // The rooms are told where he is every tick, and see to the doors themselves
        _rooms.Walk(_walker.Position);

        PassTheTime(dt);
        ShowHero();
        MovePeople(dt);
        LightTheSky();
        TendFireflies(dt);
        FollowHero(dt);
        ShowHud(dt);
    }

    /// <summary>
    /// What the scene does about a door being gone through, which the rooms have done the rest of by the time
    /// this is called. The hero is stood just inside the far door (the rooms placed the room round him, so he
    /// hasn't moved, unless he came out of a building by a door other than the one he went in by) facing away
    /// from it, and the villagers of the room he left step out of the physics world while the ones of the room
    /// he is in step into it. The lamps follow the rooms by themselves, see LightTheSky.
    /// </summary>
    private void Arrive(TileMapRoom into, TileMapRoom left, TileMapDoor through)
    {
        TileMapDoor arrival = through.Other!;
        _walker.Teleport(arrival.Landing);
        if (arrival.Direction != Vector2.Zero) _heroFacing = -arrival.Direction;

        // What blocks light in here is as tall as the map says, a table is lower than a house
        _world.WallHeight = WallHeightOf(into);

        foreach (Villager villager in _villagers)
        {
            if (villager.Room == into) Wake(villager);
            else if (villager.Room == left) Park(villager);
        }

        Say(into == _rooms.Outside ? "out again" : $"inside {into.Name.ToLowerInvariant()}", 2.0f);
    }

    /// <summary>How tall what blocks light in a room stands, a WallHeight property of its map, the height of a house if it doesn't say.</summary>
    private static float WallHeightOf(TileMapRoom room) => room.Map.Properties.GetFloat("WallHeight", 40.0f);

    /// <summary>Helper method to have the camera go after him, smoothly, and never look past the edge of the town.</summary>
    private void FollowHero(float dt)
    {
        _eye = Vector2.Lerp(_eye, _walker.Position, 1.0f - MathF.Exp(-7.0f * dt));

        // The rooms lie over the town, so the town's edges are the edges wherever he is
        TileMap town = _rooms.Outside.Map;
        Vector2 half = _camera.ViewSize / 2.0f;
        _eye = Vector2.Clamp(_eye, town.Min + half, town.Max - half);
        _camera.Position = new Vector3(_eye, 0.0f);
    }

    /// <summary>Helper method to have something said along the bottom for a while.</summary>
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
        if (_shownRoom != _rooms.Current)
        {
            _shownRoom = _rooms.Current;
            _placeName.Text = _shownRoom.Name;
        }

        int minute = (int)(_hour * 60.0f) % (24 * 60);
        if (minute != _shownMinute)
        {
            _shownMinute = minute;
            _clock.Text = $"{minute / 60:00}:{minute % 60:00}  {Daytime()}";
        }

        _saidFor -= dt;
        string says = _saidFor > 0.0f ? _said : string.Empty;
        if (!ReferenceEquals(_notice.Text, says)) _notice.Text = says;
    }

    private int _shownMinute = -1;
}
