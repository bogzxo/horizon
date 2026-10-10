using System.Numerics;

using Horizon.Core.Components;
using Horizon.Engine;
using Horizon.Input;
using Horizon.Graphics;
using Horizon.Rendering;
using Horizon.Rendering.Spriting;
using Horizon.UI;
using Horizon.UI.Components;

using Silk.NET.Input;


namespace Horizon.Testing.Examples.FirstSteps;

/// <summary>
/// Cameras. A little bloke wanders round a pixel art island on his own and a <see cref="Camera2D"/> chases him about,
/// it follows smoothly, zooms in and out, only ever shows the world on whole pixels, and every so often he gets
/// teleported and the camera hard cuts after him. Everything the camera knows is printed in the corner, and the
/// minimap shows the bit of the world it can see.
/// <para>
/// What to look at: <see cref="Camera2D"/> with its <see cref="Camera2D.ViewSize"/> and <see cref="Camera2D.Zoom"/>,
/// <see cref="Camera.Position"/>, <see cref="Camera.PixelSnap"/> and <see cref="Camera.PixelSnapAnchor"/> (why the
/// anchor stops the thing you follow wobbling), <see cref="Camera.Snap"/> and <see cref="TransformComponent2D.Snap"/>
/// for a cut, <see cref="Camera.ScreenToWorld"/> for what the mouse is pointing at, <see cref="Camera.Bounds"/> for
/// what's on screen. The minimap is a <see cref="SpriteBatch"/> drawn through a
/// camera of its own (<see cref="SpriteBatch.CustomCamera"/>), the same one the <see cref="UICompositor"/> uses.
/// </para>
/// <para>
/// In your own game:
/// <code>
/// // Constructor: there's no GL in a camera, so make it whenever you like
/// _camera = AddEntity(new Camera2D(new Vector2(400, 225)));  // sees 400 x 225 of the world, 4 pixels a unit at 1600 x 900
/// ActiveCamera = _camera;
/// _camera.PixelSnap = 1.0f;                                  // pixel art: only ever shown on whole pixels
///
/// // UpdateState, simulation thread
/// _eye = Vector2.Lerp(_eye, player.Position, 1.0f - MathF.Exp(-4.0f * dt));
/// _camera.Position = new Vector3(_eye, 0.0f);
/// _camera.PixelSnapAnchor = player.Position;                 // the player holds still, the world steps under them
/// Vector2 aim = _camera.ScreenToWorld(Engine.Input.Mouse.Position);
///
/// // Respawned on the other side of the map? Cut, don't swoop
/// _eye = player.Position;
/// _camera.Position = new Vector3(_eye, 0.0f);
/// _camera.Snap();
/// </code>
/// </para>
/// </summary>
public class CameraExample : Scene, ITestControls
{
    // The UI (and the minimap) are laid out against this, and it's what the renderer draws into
    private static readonly Vector2 DesignSize = new(1600, 900);

    // How many pixels of the screen one pixel of the art is, at a zoom of 1. The camera sees DesignSize / PIXEL of
    // the world, and a unit of the world is a pixel of the art
    private const float PIXEL = 4.0f;

    // The island: TILE units a tile, COLUMNS by ROWS of them, from (0, 0) at the bottom left
    private const int TILE = 16;
    private const int COLUMNS = 96;
    private const int ROWS = 64;
    private static readonly Vector2 MapSize = new(COLUMNS * TILE, ROWS * TILE);

    // Zooms the keys step through. They're all a whole number of screen pixels per art pixel (8, 6, 4, 3 and 2), which
    // keeps the art crisp. Anything in between and some art pixels come out a screen pixel fatter than the rest
    private static readonly float[] ZoomLevels = [0.5f, 2.0f / 3.0f, 1.0f, 4.0f / 3.0f, 2.0f];

    // Until you touch the zoom it does a lap of these by itself, one every ZOOM_EVERY seconds
    private static readonly int[] AutoZooms = [2, 1, 2, 3];
    private const float ZOOM_EVERY = 5.0f;

    // How quickly the camera catches up with the hero and with its zoom, roughly "how many times a second"
    private const float FOLLOW_RATE = 3.0f;
    private const float ZOOM_RATE = 4.0f;

    // The hero walks this fast (units, so art pixels, a second), and has a breather at every spot he walks to
    private const float WALK_SPEED = 46.0f;
    private const float REST_TIME = 0.8f;

    // He gets teleported somewhere at least CUT_DISTANCE away this often, if you don't do it first
    private const float CUT_EVERY = 9.0f;
    private const float CUT_DISTANCE = 520.0f;

    private const int CRITTERS = 60;

    // The minimap is the island painted at a pixel a tile, with a frame MINIMAP_FRAME pixels thick round it, and
    // it's drawn MINIMAP_SCALE HUD units a pixel
    private const int MINIMAP_FRAME = 2;
    private const float MINIMAP_SCALE = 3.0f;
    private static readonly Vector2 MinimapPixels = new(COLUMNS + 2 * MINIMAP_FRAME, ROWS + 2 * MINIMAP_FRAME);
    private static readonly Vector2 MinimapSize = MinimapPixels * MINIMAP_SCALE;
    private static readonly Vector2 MinimapCentre = DesignSize / 2.0f - new Vector2(24.0f) - MinimapSize / 2.0f;

    // The readout is rewritten this often. Ten times a second is plenty for numbers you read
    private const float READOUT_EVERY = 0.1f;

    private static readonly Vector4 SeaColour = new(0.2f, 0.42f, 0.75f, 1.0f);
    private static readonly Vector4 PanelColour = new(0.1f, 0.12f, 0.17f, 0.88f);
    private static readonly Vector4 CaptionColour = new(1.0f, 1.0f, 1.0f, 0.9f);
    private static readonly Vector4 NumbersColour = new(0.75f, 0.9f, 1.0f, 1.0f);
    private static readonly Vector4 WaypointColour = new(1.0f, 0.85f, 0.3f, 1.0f);
    private static readonly Vector4 BoxColour = new(1.0f, 1.0f, 1.0f, 0.95f);
    private static readonly Vector4 HeroDotColour = new(1.0f, 0.3f, 0.35f, 1.0f);

    /// <summary>What a tile of the island is.</summary>
    private enum Terrain { Grass, Path, Sand, Water }

    /// <summary>A bit of scenery: which cell of the props sheet, and where it stands.</summary>
    private readonly record struct Prop(string Art, Vector2 Position);

    /// <summary>A critter running round in little circles about its home.</summary>
    private sealed record Critter(Sprite Sprite, Vector2 Home, float Radius, float Speed, float Phase);

    public override Camera ActiveCamera { get; protected set; }

    // Listed on screen by the test host
    public IReadOnlyList<TestControl> Controls { get; } =
    [
        new("Q / E, wheel, LB / RB", "zoom out / in"),
        new("P / X", "pixel snapping on and off"),
        new("H / Y", "anchor the snapping to the hero"),
        new("Space / A", "teleport the hero, the camera cuts"),
        new("Left click", "send the hero walking there")
    ];

    private readonly Camera2D _camera, _hud;
    private readonly UICompositor _ui;
    private const string ART_DIRECTORY = "Assets/examples/camera";
    private readonly List<Prop> _props = [];
    private readonly Random _random = new(1234);

    private Sprite _hero = null!, _waypoint = null!, _cursor = null!, _heroDot = null!;
    private readonly Sprite[] _box = new Sprite[4];
    private readonly List<Critter> _critters = [];
    private Label _readout = null!;

    // Where the camera wants to be: chasing the hero, before it's kept off the edges of the map
    private Vector2 _eye;
    private int _zoomLevel = 2, _autoZoom;
    private bool _zoomTouched, _anchored = true, _boxNeedsSnap;
    private float _zoomTimer, _cutTimer, _readoutTimer, _restLeft, _time;

    private Vector2 _target, _mouseWorld;
    private int _critterCount, _cuts;

    public CameraExample()
    {
        // The world camera. It sees DesignSize / PIXEL of the world, so with the renderer drawing 1600 x 900 every art
        // pixel is PIXEL screen pixels. Made in the constructor because a camera is just matrices, no GL at all
        _camera = AddEntity(new Camera2D(DesignSize / PIXEL) { PixelSnap = 1.0f });
        ActiveCamera = _camera;

        // A second camera that never moves, for everything that sits on the screen rather than in the world: the UI
        // and the minimap. Added to the scene so frames read it the same way they read the world camera
        _hud = AddEntity(new Camera2D(DesignSize));
        _ui = AddComponent(new UICompositor(_hud) { DesignSize = DesignSize });

        // Where the trees and rocks stand is just numbers, so it's fine in here. The art is three pictures in
        // Assets/examples/camera, and turning those into textures waits for Initialize
        PlaceProps();

        BuildPanel();
    }

    public override void Initialize()
    {
        // Render thread, simulation parked: textures, renderers and batches all get made in here
        var renderer = AddEntity(new Renderer2D((uint)DesignSize.X, (uint)DesignSize.Y) { ClearColor = SeaColour });

        // Batches draw in the order they went in, so back to front: the ground, the scenery, the hero and his
        // markers, then the minimap and what's marked on it. Inside one batch there's no order you can count on
        var ground = renderer.AddEntity(new SpriteBatch());
        var scenery = renderer.AddEntity(new SpriteBatch());
        var actors = renderer.AddEntity(new SpriteBatch());

        // Every batch draws through the scene's ActiveCamera unless it's told otherwise. The minimap sits still on
        // the screen, so it goes through the HUD camera instead, the same as the UI
        var minimap = renderer.AddEntity(new SpriteBatch { CustomCamera = _hud });
        var marks = renderer.AddEntity(new SpriteBatch { CustomCamera = _hud });

        var island = LoadSheet("island.png", MapSize);
        var small = LoadSheet("minimap.png", MinimapPixels);
        var props = LoadSheet("props.png", new Vector2(TILE));

        // The whole island is one big sprite, a unit of the world to a pixel of the picture. It sits with its bottom
        // left corner on (0, 0) so a spot in the world is the same spot in the picture
        CreateSprite(ground, island, "island", MapSize / 2.0f, MapSize);

        // Origin.Bottom on anything that stands on the ground, so its Position is where it touches it
        foreach (Prop prop in _props)
            CreateSprite(scenery, props, prop.Art, prop.Position, new Vector2(TILE)).Transform.Origin = Origin.Bottom;

        for (int i = 0; i < CRITTERS; i++)
        {
            Vector2 home = RandomLand(48.0f);
            var sprite = CreateSprite(scenery, props, "critter", home, new Vector2(TILE));

            float radius = 6.0f + _random.NextSingle() * 22.0f, speed = 0.6f + _random.NextSingle() * 1.4f;
            _critters.Add(new Critter(sprite, home, radius, speed, _random.NextSingle() * MathF.Tau));
        }

        _waypoint = CreateSprite(actors, props, "cross", Vector2.Zero, new Vector2(TILE));
        _waypoint.Tint = WaypointColour;
        _cursor = CreateSprite(actors, props, "cross", Vector2.Zero, new Vector2(TILE));

        _hero = CreateSprite(actors, props, "walk", RandomLand(96.0f), new Vector2(TILE), "idle");
        _hero.Transform.Origin = Origin.Bottom;
        _target = RandomLand(96.0f);

        // The minimap, in HUD units (the middle of the screen is (0, 0)), and on top of it the box that's
        // camera.Bounds and a dot for the hero
        CreateSprite(minimap, small, "minimap", MinimapCentre, MinimapSize);
        for (int i = 0; i < _box.Length; i++)
        {
            _box[i] = CreateSprite(marks, props, "dot", MinimapCentre, Vector2.One);
            _box[i].Tint = BoxColour;
        }
        _heroDot = CreateSprite(marks, props, "dot", MinimapCentre, new Vector2(6));
        _heroDot.Tint = HeroDotColour;

        // Start the camera right on him, no swooping in from the corner of the map
        _eye = _hero.Transform.Position;
        _camera.Position = new Vector3(Clamped(_eye, _camera.Zoom), 0.0f);

        base.Initialize();
    }

    public override void UpdateState(float dt)
    {
        // Simulation thread. Children first, which is where the sprites play their animations and tweens
        base.UpdateState(dt);
        _time += dt;

        // Read the camera before you move it. ScreenToWorld and Bounds go by its matrices, which are worked out at
        // the end of every tick (and whenever the lens changes), so up here they're the camera as it was when the
        // tick started. That's the picture the player is looking at, which is what the mouse is pointing at
        ReadCamera();

        HandleInput();

        // The autopilot, so there's something to watch with your hands off
        _zoomTimer += dt;
        if (!_zoomTouched && _zoomTimer >= ZOOM_EVERY)
        {
            _zoomTimer = 0.0f;
            _autoZoom = (_autoZoom + 1) % AutoZooms.Length;
            _zoomLevel = AutoZooms[_autoZoom];
        }

        _cutTimer += dt;
        if (_cutTimer >= CUT_EVERY)
            Cut(FarFrom(_hero.Transform.Position));

        UpdateHero(dt);
        UpdateCritters();
        UpdateCamera(dt);

        _heroDot.Transform.Position = ToMinimap(_hero.Transform.Position);

        _readoutTimer += dt;
        if (_readoutTimer >= READOUT_EVERY)
        {
            _readoutTimer = 0.0f;
            UpdateReadout();
        }
    }

    /// <summary>
    /// Helper method to see what the camera sees: where the mouse is in the world, how many critters are on screen and
    /// where the box on the minimap goes. Simulation thread.
    /// </summary>
    private void ReadCamera()
    {
        // Window pixels from the top left in, world units out. It works out the window size itself, and it goes by the
        // camera's zoom and snapping, so it's right however zoomed in you are
        _mouseWorld = _camera.ScreenToWorld(Engine.Input.Mouse.Position);
        _cursor.Transform.Position = _mouseWorld;

        // What the camera sees, in world units. Mind it's a RectangleF and the world is Y up: its Y is the BOTTOM edge,
        // whatever RectangleF.Top would have you believe
        var bounds = _camera.Bounds;

        _critterCount = 0;
        foreach (Critter critter in _critters)
        {
            if (bounds.Contains(critter.Sprite.Transform.Position.X, critter.Sprite.Transform.Position.Y))
                _critterCount++;
        }

        Vector2 min = ToMinimap(new Vector2(bounds.X, bounds.Y));
        Vector2 max = ToMinimap(new Vector2(bounds.X + bounds.Width, bounds.Y + bounds.Height));
        Vector2 middle = (min + max) / 2.0f, size = max - min;

        SetLine(_box[0], new Vector2(middle.X, min.Y), new Vector2(size.X, 2));
        SetLine(_box[1], new Vector2(middle.X, max.Y), new Vector2(size.X, 2));
        SetLine(_box[2], new Vector2(min.X, middle.Y), new Vector2(2, size.Y));
        SetLine(_box[3], new Vector2(max.X, middle.Y), new Vector2(2, size.Y));

        // The camera cut last tick, and its Bounds only caught up with that at the end of it. So the box (and the
        // cursor, which the world just jumped out from under) jump now, and they get their Snap() now
        if (_boxNeedsSnap)
        {
            _boxNeedsSnap = false;
            _cursor.Transform.Snap();
            foreach (Sprite line in _box)
                line.Transform.Snap();
        }
    }

    /// <summary>
    /// Helper method to see to the keys, the wheel, the mouse and the buttons. Simulation thread.
    /// </summary>
    private void HandleInput()
    {
        var mouse = Engine.Input.Mouse;
        int zoom = 0;

        // A bigger Zoom sees MORE of the world: it multiplies how much the camera sees, so 2 is zoomed out. Yeah, I know
        if (Pressed(Key.Q, GamepadInput.LeftBumper) || mouse.Scroll < 0.0f) zoom = 1;
        if (Pressed(Key.E, GamepadInput.RightBumper) || mouse.Scroll > 0.0f) zoom = -1;

        if (zoom != 0)
        {
            _zoomTouched = true;
            _zoomLevel = Math.Clamp(_zoomLevel + zoom, 0, ZoomLevels.Length - 1);
        }

        // 1 is a unit of the world, which is a pixel of the art. 0 is off: the camera is shown exactly where it is
        if (Pressed(Key.P, GamepadInput.X))
            _camera.PixelSnap = _camera.PixelSnap > 0.0f ? 0.0f : 1.0f;

        if (Pressed(Key.H, GamepadInput.Y))
            _anchored = !_anchored;

        if (Pressed(Key.Space, GamepadInput.A))
            Cut(FarFrom(_hero.Transform.Position));

        // A click on the panel is the panel's, not a place to walk to
        if (mouse.WasPressed(MouseButton.Left) && !_ui.IsPointerOverUI)
        {
            _target = Vector2.Clamp(_mouseWorld, Vector2.Zero, MapSize);
            _restLeft = 0.0f;
        }
    }

    /// <summary>
    /// Helper method to walk the hero towards where he's going, and pick somewhere new once he's had a rest.
    /// Simulation thread.
    /// </summary>
    private void UpdateHero(float dt)
    {
        Vector2 position = _hero.Transform.Position;
        Vector2 toGo = _target - position;
        float distance = toGo.Length();

        if (_restLeft > 0.0f)
        {
            _restLeft -= dt;
            if (_restLeft <= 0.0f)
                _target = RandomLand(64.0f);
        }
        else if (distance <= WALK_SPEED * dt)
        {
            _hero.Transform.Position = _target;
            _hero.SetAnimation("idle");
            _restLeft = REST_TIME;
        }
        else
        {
            Vector2 step = toGo / distance * WALK_SPEED * dt;
            _hero.Transform.Position = position + step;
            _hero.SetAnimation("walk");

            // The art faces right. Flipping is a jump, the batch never draws him halfway round
            if (MathF.Abs(step.X) > 0.001f)
                _hero.Flipped = step.X < 0.0f;
        }

        // The yellow cross is where he's off to, bobbing a bit so you can spot it
        _waypoint.Transform.Position = _target + new Vector2(0, 2.0f * MathF.Sin(_time * 5.0f));
    }

    /// <summary>
    /// Helper method to run the critters round their little circles. Simulation thread.
    /// </summary>
    private void UpdateCritters()
    {
        foreach (Critter critter in _critters)
        {
            float angle = critter.Phase + _time * critter.Speed;
            critter.Sprite.Transform.Position = critter.Home + new Vector2(MathF.Cos(angle), MathF.Sin(angle) * 0.6f) * critter.Radius;
        }
    }

    /// <summary>
    /// Helper method to move the camera after the hero and ease the zoom along. Simulation thread: set the camera
    /// whenever you like in a tick, it's published at the end of it and every frame draws it between the last two ticks.
    /// </summary>
    private void UpdateCamera(float dt)
    {
        // Ease the zoom towards the level it's on. The projection is blended between ticks too, so it glides however
        // many frames there are. Only set it when it changes: every set works the matrices out again
        float zoom = ZoomLevels[_zoomLevel];
        if (MathF.Abs(_camera.Zoom - zoom) > 0.0005f)
            _camera.Zoom += (zoom - _camera.Zoom) * (1.0f - MathF.Exp(-ZOOM_RATE * dt));
        else if (_camera.Zoom != zoom)
            _camera.Zoom = zoom;

        // Follow. 1 - exp(-rate * dt) takes the same fraction of the gap every second however long a tick is, so this
        // doesn't care if somebody changes the tick rate. A plain "move a tenth of the way every tick" would
        _eye = Vector2.Lerp(_eye, _hero.Transform.Position, 1.0f - MathF.Exp(-FOLLOW_RATE * dt));
        _camera.Position = new Vector3(Clamped(_eye, _camera.Zoom), 0.0f);

        // PixelSnap rounds where the camera is SHOWN (never Position itself) to whole steps, counted from the anchor.
        // From (0, 0) the world lands on whole pixels, but the hero is somewhere in between them, so he wobbles a
        // pixel back and forth against the steps the camera takes, which looks like shit. Anchored to him, the
        // camera's always a whole number of pixels from him: he holds dead still on screen and the world takes the
        // odd bit of a pixel instead. Your eyes are on him, so that's nearly always the one you want. Turn the anchor
        // off with H and watch him shuffle
        _camera.PixelSnapAnchor = _anchored ? _hero.Transform.Position : Vector2.Zero;
    }

    /// <summary>
    /// Helper method to teleport the hero and cut the camera to him. Simulation thread.
    /// </summary>
    private void Cut(Vector2 to)
    {
        _cutTimer = 0.0f;
        _cuts++;

        _hero.Transform.Position = to;
        _target = RandomLand(64.0f);
        _restLeft = 0.0f;

        // Every frame is drawn between the last two ticks. Without these, the frames that show the moment between this
        // tick and the last would draw him (and the whole bloody world, through the camera) halfway across the map,
        // smeared mid swoosh. Snap() says "he was put here, don't show him getting here"
        _hero.Transform.Snap();
        _heroDot.Transform.Snap();

        // Same for the camera: put it straight there (no following) and Snap() it. Anything that jumps gets a Snap
        _eye = to;
        _camera.Position = new Vector3(Clamped(_eye, _camera.Zoom), 0.0f);
        _camera.Snap();

        _hero.Flash(Vector4.One, 0.5f);
        _boxNeedsSnap = true;
    }

    /// <summary>
    /// Helper method to keep the camera from showing the sea off the edge of the map. The camera sees ViewSize * Zoom
    /// of the world, so its middle has to stay half of that in from every edge. A map smaller than that is centred.
    /// </summary>
    private Vector2 Clamped(Vector2 eye, float zoom)
    {
        Vector2 half = _camera.ViewSize * zoom / 2.0f;

        return new Vector2(
            half.X * 2.0f >= MapSize.X ? MapSize.X / 2.0f : Math.Clamp(eye.X, half.X, MapSize.X - half.X),
            half.Y * 2.0f >= MapSize.Y ? MapSize.Y / 2.0f : Math.Clamp(eye.Y, half.Y, MapSize.Y - half.Y));
    }

    /// <summary>
    /// Helper method to write everything the camera knows into the panel. Simulation thread, where the UI is updated.
    /// </summary>
    private void UpdateReadout()
    {
        var bounds = _camera.Bounds;
        Vector3 position = _camera.Position;
        float pixels = DesignSize.X / (_camera.ViewSize.X * _camera.Zoom);

        string snap = _camera.PixelSnap > 0.0f ? $"{_camera.PixelSnap:0}, from {(_anchored ? "the hero" : "(0, 0)")}" : "off";
        string zoom = _zoomTouched ? string.Empty : "  (on autopilot)";

        _readout.Text =
            $"Position  {position.X:0.0}, {position.Y:0.0}\n" +
            $"Zoom  {_camera.Zoom:0.00}, {pixels:0.0} screen pixels an art pixel{zoom}\n" +
            $"PixelSnap  {snap}\n" +
            $"PixelSnapAnchor  {_camera.PixelSnapAnchor.X:0.0}, {_camera.PixelSnapAnchor.Y:0.0}\n" +
            $"Bounds  {bounds.X:0}, {bounds.Y:0}, {bounds.Width:0} x {bounds.Height:0}\n" +
            $"Mouse in the world  {_mouseWorld.X:0}, {_mouseWorld.Y:0}\n" +
            $"Critters on screen  {_critterCount} of {CRITTERS}\n" +
            $"Cuts  {_cuts}, the next in {CUT_EVERY - _cutTimer:0} s";
    }

    /// <summary>
    /// Helper method to turn a picture we painted into a sprite sheet with cells of a size. Render thread.
    /// </summary>
    private SpriteSheet LoadSheet(string file, Vector2 cell)
    {
        if (!Engine.ObjectManager.Textures.TryCreate(
                new TextureDescription
                {
                    Paths = [Path.Combine(ART_DIRECTORY, file)],

                    // Nearest, or the pixel art comes out as mush
                    Definition = TextureDefinition.RgbaUnsignedByteNearest
                },
                out var texture))
        {
            throw new Exception($"Couldn't make a texture out of {file}: {texture.Message}");
        }

        return SpriteSheet.FromTexture(texture.Asset, cell);
    }

    /// <summary>
    /// Helper method to make a sprite showing one bit of a sheet (see <see cref="Cells"/>), and put it in a batch.
    /// Render thread.
    /// </summary>
    /// <param name="art">What it shows to begin with.</param>
    /// <param name="more">Anything else it can be switched to with SetAnimation.</param>
    private static Sprite CreateSprite(SpriteBatch batch, SpriteSheet sheet, string art, Vector2 position, Vector2 size, params string[] more)
    {
        // AddEntity gets it updated (animations and tweens), Add gets it drawn
        var sprite = batch.AddEntity(new Sprite(size));
        sprite.Transform.Position = position;
        sprite.ConfigureSpriteSheet(sheet, art);

        // Only the animations it uses: the animation manager moves every one it has along every tick
        foreach (var (name, column, row, frames, frameTime) in Cells)
        {
            if (name == art || more.Contains(name))
                sprite.AddAnimation(name, new Vector2(column, row), frames, frameTime);
        }

        batch.Add(sprite);
        return sprite;
    }

    // What's where in the sheets: the column and row of the first frame (in cells), how many frames run right from
    // there and how long each is up. The island and the minimap are a sheet of one cell each
    private static readonly (string Name, int Column, int Row, uint Frames, float FrameTime)[] Cells =
    [
        ("island", 0, 0, 1, 0.0f),
        ("minimap", 0, 0, 1, 0.0f),
        ("tree", 0, 0, 1, 0.0f),
        ("bush", 1, 0, 1, 0.0f),
        ("rock", 2, 0, 1, 0.0f),
        ("flower", 3, 0, 2, 0.4f),
        ("cross", 5, 0, 1, 0.0f),
        ("dot", 6, 0, 1, 0.0f),
        ("walk", 0, 1, 4, 0.12f),
        ("idle", 4, 1, 2, 0.4f),
        ("critter", 0, 2, 2, 0.18f)
    ];

    /// <summary>
    /// Helper method to put a line of the minimap's box somewhere.
    /// </summary>
    private static void SetLine(Sprite line, Vector2 position, Vector2 size)
    {
        line.Transform.Position = position;
        line.Transform.Size = size;
    }

    /// <summary>Helper method to turn a spot in the world into a spot on the minimap, in HUD units.</summary>
    private static Vector2 ToMinimap(Vector2 world) => MinimapCentre + (world - MapSize / 2.0f) * (MINIMAP_SCALE / TILE);

    /// <summary>
    /// Helper method for a key, or a button on whichever gamepad was used last, going down this update.
    /// </summary>
    private static bool Pressed(Key key, GamepadInput button) =>
        Engine.Input.Keyboard.WasPressed(key) || Engine.Input.Gamepads.LastUsed?.WasPressed(button) == true;

    /// <summary>Helper method to pick a random spot on dry land, at least margin in from the edges.</summary>
    private Vector2 RandomLand(float margin)
    {
        while (true)
        {
            var spot = new Vector2(
                margin + _random.NextSingle() * (MapSize.X - 2.0f * margin),
                margin + _random.NextSingle() * (MapSize.Y - 2.0f * margin));

            if (TerrainAt(spot) is not Terrain.Water)
                return spot;
        }
    }

    /// <summary>Helper method to pick a random spot on dry land a good long way from somewhere.</summary>
    private Vector2 FarFrom(Vector2 from)
    {
        while (true)
        {
            Vector2 spot = RandomLand(96.0f);
            if (Vector2.Distance(spot, from) >= CUT_DISTANCE)
                return spot;
        }
    }

    /// <summary>
    /// Helper method to put up the panel in the corner, and a caption for the minimap.
    /// </summary>
    private void BuildPanel()
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
        panel.Add(new Label("Cameras") { Anchor = Origin.Left, TextScale = 0.36f });
        panel.Add(new Label(
            "One Camera2D following the hero about. It zooms,\n" +
            "only ever shows the art on whole pixels, and cuts\n" +
            "straight to him when he's teleported. The UI and the\n" +
            "minimap go through a second camera that never moves.")
        {
            Anchor = Origin.Left,
            Align = Origin.TopLeft,
            TextScale = 0.22f,
            Color = CaptionColour
        });

        _readout = panel.Add(new Label(string.Empty)
        {
            Anchor = Origin.Left,
            Align = Origin.TopLeft,
            TextScale = 0.2f,
            Color = NumbersColour
        });

        var caption = module.AddComponent(new StackPanel
        {
            Anchor = Origin.TopRight,
            Position = new Vector2(-24, -24 - MinimapSize.Y - 8),
            Color = PanelColour,
            Padding = new UIEdges(10, 6)
        });
        caption.Add(new Label("The white box is camera.Bounds") { TextScale = 0.2f, Color = CaptionColour });
    }

    // Everything from here down is where the land of the island is and what stands on it. Nothing to do with
    // cameras, so if that's what you came for you can stop reading here :)
    // island.png and minimap.png were painted from these very rules, once, and are files now. Change the island
    // in here and the pictures want painting again

    /// <summary>
    /// Helper method to pick what's on a tile. Column and row are counted from the bottom left, like the world.
    /// A few waves added up make a lump of an island, and two wiggly paths cross it.
    /// </summary>
    private static Terrain TerrainAt(int column, int row)
    {
        float x = column / (float)COLUMNS * 2.0f - 1.0f, y = row / (float)ROWS * 2.0f - 1.0f;

        float land = 1.0f - (x * x * 0.9f + y * y)
            + 0.18f * MathF.Sin(column * 0.31f + 1.0f) * MathF.Cos(row * 0.27f)
            + 0.12f * MathF.Sin((column + row) * 0.19f + 2.0f)
            - 0.35f * MathF.Exp(-((x + 0.25f) * (x + 0.25f) + (y - 0.2f) * (y - 0.2f)) * 30.0f);

        if (land < 0.18f) return Terrain.Water;
        if (land < 0.27f) return Terrain.Sand;

        float road = ROWS * 0.45f + 6.0f * MathF.Sin(column * 0.12f);
        float lane = COLUMNS * 0.62f + 4.0f * MathF.Sin(row * 0.17f + 1.0f);
        if (MathF.Abs(row - road) < 1.0f || MathF.Abs(column - lane) < 1.0f)
            return Terrain.Path;

        return Terrain.Grass;
    }

    /// <summary>Helper method to see what's on the tile under a spot in the world.</summary>
    private static Terrain TerrainAt(Vector2 world) => TerrainAt((int)(world.X / TILE), (int)(world.Y / TILE));

    /// <summary>
    /// Helper method to scatter the scenery over the grass. Just numbers, the sprites are made in Initialize.
    /// </summary>
    private void PlaceProps()
    {
        string[] kinds = ["tree", "tree", "tree", "bush", "bush", "rock", "flower", "flower"];

        for (int row = 0; row < ROWS; row++)
        {
            for (int column = 0; column < COLUMNS; column++)
            {
                if (TerrainAt(column, row) is not Terrain.Grass || _random.NextSingle() > 0.07f)
                    continue;

                // Somewhere in the tile, on a whole unit so the art lines up with the ground
                var position = new Vector2(
                    column * TILE + 2 + _random.Next(TILE - 4),
                    row * TILE + 2 + _random.Next(TILE - 4));

                _props.Add(new Prop(kinds[_random.Next(kinds.Length)], position));
            }
        }
    }
}
