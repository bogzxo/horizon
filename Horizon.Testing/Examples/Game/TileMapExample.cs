using System.Numerics;

using Horizon.Engine;
using Horizon.Rendering;
using Horizon.Rendering.Primitives;
using Horizon.Rendering.Tiling;
using Horizon.UI;
using Horizon.UI.Components;

using Silk.NET.Input;

namespace Horizon.Testing.Examples.Game;

/// <summary>
/// A tile map, drawn in Tiled and loaded off its file. The street in Assets/examples/world/town.tmx has one of
/// everything a map can have, layers behind that scroll slower than the camera, a layer in front of everything,
/// tiles turned every way a tile can be turned, a torch with two frames, lamps and a spawn point put down as
/// objects from templates. Open it in Tiled next to this and change something.
/// <para>
/// What to look at. <see cref="TileMap.Load"/> and adding the map to a renderer like any entity (its
/// <see cref="TileMap.Foreground"/> separately, after whatever goes in between), <see cref="TileMap.ParallaxOrigin"/>,
/// <see cref="TileMap.FindLayer"/> with <see cref="TileMapLayer.Visible"/> and <see cref="TileMapLayer.SetTile"/>,
/// <see cref="TileMap.TilesAt"/> for what is under a spot of the world, <see cref="TileMap.DispatchObjects"/> for
/// handing the objects of a map to whoever knows what to do with their kind, and <see cref="TileMap.BuildColliders"/>
/// for the boxes a physics world or a platformer wants.
/// </para>
/// <para>
/// In your own game.
/// <code>
/// // Constructor, a map is read off its file without touching the GPU
/// map = renderer.AddEntity(TileMap.Load("Assets/maps/town.tmx"));
/// renderer.AddEntity(players);                      // whatever stands between the map and its front layer
/// renderer.AddEntity(map.Foreground);
///
/// map.DispatchObjects(objects => objects
///     .OfClass("spawn", spawn => player.Position = spawn.Position)
///     .OfClass("light", lamp => lighting.AddLight(new Light2D { Position = lamp.Position })));
///
/// foreach (TileMapBox box in map.BuildColliders())
///     body.CreateRectangularFixture(box.Min, box.Size);
/// </code>
/// </para>
/// </summary>
public class TileMapExample : Scene, ITestControls
{
    private static readonly Vector2 DesignSize = new(1600, 900);

    // How many pixels of the screen a pixel of the map is
    private const float PIXEL = 3.0f;
    private const float PAN_SPEED = 180.0f;

    private static readonly Vector4 ColliderColour = new(0.3f, 1.0f, 0.5f, 0.9f);
    private static readonly Vector4 ObjectColour = new(1.0f, 0.8f, 0.3f, 1.0f);
    private static readonly Vector4 PickColour = new(1.0f, 1.0f, 1.0f, 0.9f);

    public override Camera ActiveCamera { get; protected set; }

    // Listed on screen by the test host
    public IReadOnlyList<TestControl> Controls { get; } =
    [
        new("Arrows", "move the camera, the layers follow at their own speeds"),
        new("Mouse", "point at a tile to see what it is"),
        new("Left click", "put a crate down, or take one away"),
        new("F", "the layer in front on and off"),
        new("B", "show the colliders and the objects"),
    ];

    private readonly Camera2D _camera;
    private readonly TileMap _map;
    private readonly Label _under, _objects;

    // What BuildColliders made of the map, worked out again whenever a tile changes
    private List<TileMapBox> _colliders = [];
    private readonly List<(Vector2 Position, string Name)> _marks = [];

    private Vector2 _eye, _pointer;
    private bool _showBoxes = true;

    public TileMapExample()
    {
        _camera = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize / PIXEL) { PixelSnap = 1.0f });
        ActiveCamera = _camera;

        // Reading a map is files and memory, its textures are loaded the first time it is drawn, so this is fine
        // in a constructor. The objects could be handed out right here too, Load takes the same thing
        // DispatchObjects does
        _map = TileMap.Load(World.TOWN);

        // Looking at the middle of the street to start with, and that is also where its layers line up the way
        // they were drawn in Tiled. Further from there, the further apart the slow ones have drifted
        _eye = new Vector2(_map.Min.X + _map.Size.X / 2.0f, _map.Min.Y + 10.0f * _map.TileSize.Y);
        _map.ParallaxOrigin = _eye;

        var ui = AddComponent(UICompositor.ForScreen());
        ui.DesignSize = DesignSize;

        var panel = ui.CreateModule().AddComponent(new StackPanel
        {
            Anchor = Origin.TopLeft,
            Position = new Vector2(24, -24),
            Color = new Vector4(0.1f, 0.12f, 0.17f, 0.9f),
            Radius = 10.0f,
            Padding = new UIEdges(18),
            Spacing = 8
        });
        panel.Add(new Label("town.tmx") { Anchor = Origin.Left, TextScale = 0.34f });
        panel.Add(new Label($"{_map.Width} by {_map.Height} tiles of {_map.TileSize.X:0}, {_map.Layers.Count} layers, {_map.Objects.Count} objects")
        {
            Anchor = Origin.Left,
            TextScale = 0.22f
        });
        _under = panel.Add(new Label { Anchor = Origin.Left, Align = Origin.TopLeft, TextScale = 0.22f, Size = new Vector2(420, 70) });
        _objects = panel.Add(new Label { Anchor = Origin.Left, Align = Origin.TopLeft, TextScale = 0.2f, Color = ObjectColour });

        ReadObjects();
    }

    public override void Initialize()
    {
        // Render thread. The map is an entity like any other and draws whatever of it the camera sees, in one draw.
        // In a plain renderer it is drawn as it was painted, a lit one (the lighting example) lights it
        var renderer = AddEntity(new Renderer2D((uint)DesignSize.X, (uint)DesignSize.Y)
        {
            FollowWindow = true,
            ClearColor = _map.BackgroundColor ?? new Vector4(0.1f, 0.13f, 0.24f, 1.0f)
        });

        renderer.AddEntity(_map);

        // The layers the map says are in front (a Foreground property on them) are drawn by this, so whatever is
        // added between the two ends up between them on screen. Here that is nothing, the town example has a hero
        renderer.AddEntity(_map.Foreground);

        // The boxes and the marks, over the lot
        var overlay = renderer.AddEntity(new PrimitiveRenderer());
        overlay.Describe = Describe;

        _colliders = _map.BuildColliders();

        base.Initialize();
    }

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);

        var keyboard = Engine.Input.Keyboard;
        var mouse = Engine.Input.Mouse;

        // Kept inside of the map, half a view from its edges
        Vector2 half = _camera.ViewSize / 2.0f;
        _eye += new Vector2(keyboard.Axis(Key.Left, Key.Right), keyboard.Axis(Key.Down, Key.Up)) * PAN_SPEED * dt;
        _eye = Vector2.Clamp(_eye, _map.Min + half, _map.Max - half);
        _camera.Position = new Vector3(_eye, 0.0f);

        if (keyboard.WasPressed(Key.F) && _map.FindLayer("front") is { } front)
            front.Visible = !front.Visible;

        if (keyboard.WasPressed(Key.B))
            _showBoxes = !_showBoxes;

        _pointer = _camera.ScreenToWorld(mouse.Position);
        DescribeTile();

        if (mouse.WasPressed(MouseButton.Left) && _map.FindLayer("solid") is { } solid)
        {
            // A layer is a grid of numbers and can be written to while the game runs. 13 is the crate, 0 is nothing.
            // The map uploads the change by itself, the colliders are ours to make again
            var (x, y) = _map.WorldToTile(_pointer);
            solid.SetTile(x, y, solid.GetTile(x, y) is null ? 13u : 0u);
            _colliders = _map.BuildColliders();
        }
    }

    /// <summary>Helper method to say what is under the pointer, every layer that has a tile there.</summary>
    private void DescribeTile()
    {
        var (x, y) = _map.WorldToTile(_pointer);
        string text = $"tile {x}, {y}";

        foreach (TileMapCell cell in _map.TilesAt(_pointer))
        {
            text += $"\n{cell.Layer.Name}   tile {cell.Tile.Id}";
            if (cell.Flip != TileFlip.None) text += $", turned {cell.Flip}";
            if (cell.Tile.Animation.Count > 0) text += $", {cell.Tile.Animation.Count} frames";
            if (cell.Tile.Class.Length > 0) text += $", a {cell.Tile.Class}";
        }

        _under.Text = text;
    }

    /// <summary>
    /// Helper method to hand the objects of the map out by what they are, which is how a game gets its spawn
    /// points, lamps and trigger zones out of a map without a line of the map knowing about the game.
    /// </summary>
    private void ReadObjects()
    {
        int lamps = 0, zones = 0, others = 0;
        string spawn = "nowhere";

        _map.DispatchObjects(objects => objects
            .OfClass("light", lamp =>
            {
                lamps++;
                _marks.Add((lamp.Position, lamp.Name));
            })
            .OfClass("spawn", found =>
            {
                spawn = $"{found.Position.X:0}, {found.Position.Y:0}";
                _marks.Add((found.Position, found.Name));
            })
            .OfClass("zone", zone =>
            {
                zones++;
                _marks.Add((zone.Position, zone.Name));
            })
            .Otherwise(_ => others++));

        _objects.Text = $"{lamps} lamps, {zones} zone, {others} other, the player starts at {spawn}";
    }

    /// <summary>
    /// Helper method to write down what the overlay shows, at the end of every tick (see <see cref="PrimitiveRenderer.Describe"/>).
    /// </summary>
    private void Describe(ShapeList shapes)
    {
        // The tile the pointer is on
        var (x, y) = _map.WorldToTile(_pointer);
        Vector2 centre = _map.TileToWorld(x, y);
        shapes.Rectangle(centre - _map.TileSize / 2.0f, centre + _map.TileSize / 2.0f, 1.0f, PickColour);

        if (!_showBoxes) return;

        // Solid tiles next to each other come as one box, which is a lot less for a physics world to chew on
        foreach (TileMapBox box in _colliders)
            shapes.Rectangle(box.Min, box.Min + box.Size, 1.0f, ColliderColour);

        foreach (var (position, _) in _marks)
            shapes.Circle(position, 5.0f, 1.0f, ObjectColour);
    }
}
