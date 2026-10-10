using System;
using System.Numerics;

using Horizon.Graphics;

using Horizon.Engine;
using Horizon.Rendering;
using Horizon.Rendering.Tiling;
using Horizon.UI;
using Horizon.UI.Components;

using Silk.NET.Input;


namespace Horizon.Testing.Checks;

/// <summary>
/// Checks the tile maps of the engine against three small maps in Assets/examples/checks/tilemap that were made
/// to be awkward. Between them they have one of everything a map can have in it, tiles turned every way there
/// is, animated ones, a layer of clouds that repeats and drifts, a group that scrolls at half speed, a layer in
/// front of everything, a tile put down as an object, a map without edges and one the engine can't draw at all.
/// The first is drawn, and how the checks went is on the right. The numbers in the checks are the numbers in
/// those files, change one and the other wants changing too. The example of tile maps, for reading, is
/// TileMapExample.
/// </summary>
internal sealed class TileMapChecks : Scene, ITestControls, ISelfCheck
{
    private const string DIRECTORY = "Assets/examples/checks/tilemap";

    private static readonly Vector2 DesignSize = new(1600, 900);

    private const float PAN_SPEED = 160.0f;

    public override Camera ActiveCamera { get; protected set; }

    // Listed on screen by the test host
    public IReadOnlyList<TestControl> Controls { get; } =
    [
        new("Arrows", "move the camera, the layers follow at their own speeds"),
        new("F", "hide and show the layer in front"),
        new("T", "put a tile down where the camera is")
    ];

    private readonly Camera2D _camera;
    private readonly UICompositor _compositor;
    private TestChecks? _checks;

    public bool Finished => _checks is not null;
    public int Failed => _checks is null ? 0 : _checks.Count - _checks.Passed;
    public int Count => _checks?.Count ?? 0;

    private TileMap _map = null!;
    private Vector2 _eye;

    public TileMapChecks()
    {
        // Three pixels of the screen to one of the map
        _camera = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize / 3.0f));
        ActiveCamera = _camera;

        _map = AddEntity(TileMap.Load(Path.Combine(DIRECTORY, "town.tmx")));

        // The layer that is marked as being in front is drawn by this, after whatever would go in between
        AddEntity(_map.Foreground);

        var viewport = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize));
        _compositor = AddComponent(new UICompositor(viewport) { DesignSize = DesignSize });
    }

    public override void PostInit()
    {
        base.PostInit();

        RenderState.Blend = true;
        RenderState.BlendMode = BlendMode.Alpha;
        Engine.Graphics.ClearColor = new Vector4(0.36f, 0.62f, 0.86f, 1.0f);

        // Looking at the middle of the map, which is also where its layers line up
        _eye = _map.Min + _map.Size / 2.0f;
        _map.ParallaxOrigin = _eye;
        _camera.Position = new Vector3(_eye, 0.0f);

        TestChecks checks = RunChecks(DIRECTORY);
        _checks = checks;

        var module = _compositor.CreateModule();
        var panel = module.AddComponent(new StackPanel
        {
            Anchor = Origin.TopRight,
            Position = new Vector2(-20, -50),
            Color = new Vector4(0.1f, 0.12f, 0.17f, 0.92f),
            Padding = new UIEdges(16),
            Spacing = 8
        });
        panel.Add(new Label("Checked as the scene started") { TextScale = 0.26f });
        panel.Add(new Label(checks.Describe())
        {
            Align = Origin.TopLeft,
            TextScale = 0.17f,
            Color = checks.Passed == checks.Count ? new Vector4(0.6f, 0.95f, 0.65f, 1.0f) : new Vector4(1.0f, 0.5f, 0.45f, 1.0f)
        });
    }

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);

        var keyboard = Engine.Input.Keyboard;

        Vector2 pan = new(
            (keyboard.IsDown(Key.Right) ? 1 : 0) - (keyboard.IsDown(Key.Left) ? 1 : 0),
            (keyboard.IsDown(Key.Up) ? 1 : 0) - (keyboard.IsDown(Key.Down) ? 1 : 0));

        _eye += pan * PAN_SPEED * dt;
        _camera.Position = new Vector3(MathF.Round(_eye.X), MathF.Round(_eye.Y), 0.0f);

        if (keyboard.WasPressed(Key.F) && _map.FindLayer("front") is { } front)
            front.Visible = !front.Visible;

        if (keyboard.WasPressed(Key.T) && _map.FindLayer("decor") is { } decor)
        {
            var (x, y) = _map.WorldToTile(_eye);
            decor.SetTile(x, y, 2);
        }
    }

    // How big town.tmx is, in tiles
    private const int COLUMNS = 20, ROWS = 12;

    /* The checks */

    private TestChecks RunChecks(string directory)
    {
        var checks = new TestChecks("Tile map test");
        TileMap map = _map;

        bool Near(float a, float b) => MathF.Abs(a - b) < 0.01f;
        bool At(Vector2 a, float x, float y) => Vector2.Distance(a, new Vector2(x, y)) < 0.01f;

        checks.Check("the map is as big as it says, laid out up and to the right of its origin", () =>
            map is { Width: COLUMNS, Height: ROWS, Left: 0, Top: 0 } && map.TileSize == new Vector2(16) && map.Size == new Vector2(320, 192)
            && map.Max == map.Origin + map.Size && map.Properties.GetString("music") == "town" && map.BackgroundColor is { Z: > 0.8f });

        checks.Check("layers come in their order, the ones of a group among them", () =>
            string.Join(" ", map.Layers.Select(layer => layer.Name)) == "clouds hills solid decor front things pillars"
            && map.FindLayer("hills") is { Group: "backdrop", Kind: TileMapLayerKind.Tiles }
            && map.FindLayer("clouds")!.Kind == TileMapLayerKind.Image && map.FindLayer("things")!.Kind == TileMapLayerKind.Objects);

        checks.Check("what a group says is worked into what is in it", () =>
            map.FindLayer("hills") is { } hills && Near(hills.Opacity, 0.25f) && hills.WorldOffset == new Vector2(8, 0)
            && hills.Parallax == new Vector2(0.5f, 1.0f) && Near(hills.Tint.X, 128 / 255.0f) && hills.Tint.Y == 1.0f);

        checks.Check("the properties of a layer the map goes by are read off it", () =>
            map.FindLayer("solid") is { IsCollidable: true, CastsShadows: true, IsForeground: false }
            && map.FindLayer("front") is { IsForeground: true, IsCollidable: false } front && Near(front.Opacity, 0.6f)
            && map.FindLayer("clouds") is { Emissive: 1.0f } clouds && clouds.Scroll == new Vector2(12, 0) && clouds.WorldOffset == new Vector2(0, -16));

        checks.Check("a tile is found by where it is, and says how it is turned over", () =>
        {
            TileMapLayer decor = map.FindLayer("decor")!;
            TileFlip[] expected =
            [
                TileFlip.None, TileFlip.Horizontal, TileFlip.Vertical, TileFlip.Horizontal | TileFlip.Vertical,
                TileFlip.Diagonal, TileFlip.Diagonal | TileFlip.Horizontal, TileFlip.Diagonal | TileFlip.Vertical,
                TileFlip.Diagonal | TileFlip.Horizontal | TileFlip.Vertical
            ];

            return Enumerable.Range(0, 8).All(i => decor.GetTile(1 + i, 8) is { Tile.Id: 0 } cell && cell.Flip == expected[i])
                && decor.GetTile(0, 8) is null && decor.GetTile(-1, 8) is null && decor.GetTile(99, 99) is null;
        });

        checks.Check("the world and the map agree on where everything is", () =>
        {
            Vector2 centre = map.TileToWorld(3, 8);
            return At(centre, 3 * 16 + 8, (ROWS - 1 - 8) * 16 + 8) && map.WorldToTile(centre) == (3, 8)
                && At(map.TiledToWorld(new Vector2(40, 100)), 40, 92) && At(map.WorldToTiled(new Vector2(40, 92)), 40, 100)
                && map.Contains(0, 0) && !map.Contains(COLUMNS, 0)
                && map.TilesAt(map.TileToWorld(3, 10)).Single().Layer.Name == "solid";
        });

        checks.Check("moving the map moves everything that is asked of it", () =>
        {
            Vector2 before = map.FindObject("marker")!.Position;
            map.Origin = new Vector2(100, -50);
            bool moved = At(map.FindObject("marker")!.Position, before.X + 100, before.Y - 50) && At(map.TileToWorld(0, ROWS - 1), 108, -42);
            map.Origin = Vector2.Zero;
            return moved && At(map.FindObject("marker")!.Position, 8, 184);
        });

        checks.Check("a tile brings what its tile set says about it", () =>
        {
            TileMapTile blinking = map.FindLayer("decor")!.GetTile(12, 8)!.Value.Tile;
            TileMapTile sign = map.ResolveTile(6)!;
            TileMapTile step = map.ResolveTile(5)!;

            return blinking.Animation.Count == 2 && blinking.Animation[0].Tile == blinking && blinking.Animation[1].Tile.Id == 3
                && Near(blinking.Animation[0].Duration, 0.2f) && Near(blinking.Animation[1].Duration, 0.3f)
                && sign is { Class: "sign", Gid: 6, Id: 5 } && sign.Properties.GetBool("collidable") && sign.Size == new Vector2(16)
                && MathF.Abs(sign.Source.X - 16) < 0.05f && MathF.Abs(sign.Source.W - 32) < 0.05f && sign.ImagePath.EndsWith("tiles.png")
                && step.Collision is [{ } shape] && shape.Min == new Vector2(0, 0) && shape.Size == new Vector2(16, 8)
                && map.ResolveTile(0) is null && map.ResolveTile(999) is null;
        });

        checks.Check("objects come in the world's own units", () =>
        {
            TileMapObject spawn = map.FindObject("p1")!, zone = map.FindObject("zone")!, marker = map.FindObject(5)!;

            return spawn is { Shape: TileMapShape.Rectangle, Class: "spawn", Id: 2 } && At(spawn.Position, 40, 80) && spawn.Size == new Vector2(16, 32) && At(spawn.Min, 32, 64)
                && zone.Shape == TileMapShape.Polygon && At(zone.Position, 160, 128) && zone.Points.Count == 3 && At(zone.Points[2], 176, 104)
                && marker is { Shape: TileMapShape.Point, Name: "marker" } && At(marker.Position, 8, 184) && marker.Size == Vector2.Zero
                && map.FindObject("note") is { Shape: TileMapShape.Text, Text: "a note" } && map.Objects.Count == 7;
        });

        checks.Check("an object made from a template has what the template says, and its own on top", () =>
        {
            TileMapObject lamp = map.FindObject("lamp")!;
            return lamp is { Shape: TileMapShape.Point, Class: "light" } && At(lamp.Position, 40, 92)
                && lamp.Properties.GetFloat("light_radius") == 120.0f
                && lamp.Properties.GetColor("light_colour", Vector4.Zero) == new Vector4(0, 0, 1, 1);
        });

        checks.Check("a tile put down as an object is turned around where it stands, and has its tile's properties", () =>
        {
            TileMapObject sign = map.FindObject("sign")!;
            return sign is { Shape: TileMapShape.Tile, Class: "sign", Rotation: -90.0f, Tile.Id: 5 } && At(sign.Position, 216, 48) && sign.Size == new Vector2(32)
                && sign.Properties.GetString("label") == "from the tile" && sign.Properties.GetBool("collidable");
        });

        checks.Check("properties read as what is asked for, whatever they were typed in as", () =>
        {
            TileMapProperties properties = map.FindObject("p1")!.Properties;
            return properties.GetInt("player") == 1 && properties.GetFloat("PLAYER") == 1.0f && properties.GetString("player") == "1"
                && properties.GetFloat("speed") == 2.5f && properties.GetFloat("missing", 7) == 7 && !properties.Has("missing")
                && properties.TryGetFloat("speed", out float speed) && speed == 2.5f && !properties.TryGetFloat("nothing", out _);
        });

        checks.Check("objects are handed to whoever asked for their kind, the rest to whoever takes the rest", () =>
        {
            var seen = new List<string>();
            map.DispatchObjects(objects => objects
                .OfClass("SPAWN", found => seen.Add($"spawn:{found.Name}"))
                .WithProperty("light_radius", found => seen.Add($"light:{found.Name}"))
                .OfShape(TileMapShape.Polygon, found => seen.Add($"shape:{found.Name}"))
                .Named("sign", found => seen.Add($"named:{found.Name}"))
                .InLayer("things", found => seen.Add("layer"))
                .InLayer("pillars", found => seen.Add("layer"))
                .Otherwise(found => seen.Add($"left:{found.Name}")));

            return string.Join(" ", seen.Where(entry => entry != "layer")) == "light:lamp spawn:p1 shape:zone named:sign"
                && seen.Count(entry => entry == "layer") == 7;
        });

        checks.Check("what is left over goes to whoever takes the rest", () =>
        {
            var left = new List<string>();
            map.DispatchObjects(objects => objects.OfClass("spawn", _ => { }).Otherwise(found => left.Add(found.Name)));
            return string.Join(" ", left) == "lamp zone sign marker note pillar";
        });

        checks.Check("solid tiles next to each other are one box, and a tile can be solid in part", () =>
        {
            List<TileMapBox> boxes = map.BuildColliders();
            bool Has(float x, float y, float width, float height) =>
                boxes.Any(box => At(box.Min, x, y) && At(box.Size, width, height));

            // The two halves of the top row of the ground, the two rows under it as one, the lower half of the
            // step and the sign, which its tile set says is solid
            return boxes.Count == 5 && Has(0, 32, 128, 16) && Has(160, 32, 160, 16) && Has(0, 0, 320, 32) && Has(240, 48, 16, 8) && Has(272, 48, 16, 16);
        });

        checks.Check("whoever asks decides what is solid instead", () =>
            map.BuildColliders(cell => cell.Layer.Name == "front") is [{ } box] && At(box.Min, 80, 64) && At(box.Size, 32, 32));

        checks.Check("the tiles that block light are the ones of the layers that say so, and the objects of the layers that block it", () =>
        {
            // The solid tiles are all below 48, the pillar is an object 16 by 32 standing on cell (4, 2), two cells of it
            var casters = map.ShadowCasters().ToList();
            return casters.Count == 60 && casters.Count(centre => centre.Y >= 48) == 1
                && casters.Contains(new Vector2(72, 40)) && casters.Contains(new Vector2(72, 56));
        });

        checks.Check("a tile that is put down is there to be found, and one that is taken away isn't", () =>
        {
            TileMapLayer decor = map.FindLayer("decor")!;
            bool put = decor.SetTile(0, 0, 2, TileFlip.Horizontal) && decor.GetTile(0, 0) is { Tile.Id: 1, Flip: TileFlip.Horizontal };
            bool taken = decor.SetTile(0, 0, 0) && decor.GetTile(0, 0) is null;
            return put && taken && !decor.SetTile(-1, 0, 2) && !map.FindLayer("things")!.SetTile(0, 0, 2);
        });

        checks.Check("a map without edges is as big as what is in it, wherever that is", () =>
        {
            TileMap endless = TileMap.Load(Path.Combine(directory, "endless.tmx"));
            TileMapLayer ground = endless.Layers[0];

            return endless is { Left: -16, Top: -16, Width: 32, Height: 32 }
                && ground.GetTile(-3, -2) is { Tile.Id: 1, Flip: TileFlip.None } far && At(far.Centre, 216, 280)
                && ground.GetTile(1, 1) is { Flip: TileFlip.Horizontal } && ground.Tiles().Count() == 2 && ground.GetTile(0, 0) is null;
        });

        checks.Check("a map that can't be read says so rather than half loading", () =>
        {
            bool missing = !TileMap.TryLoad(Path.Combine(directory, "nowhere.tmx"), out TileMap? none) && none is null;

            bool slanted;
            try
            {
                TileMap.Load(Path.Combine(directory, "slanted.tmx"));
                slanted = false;
            }
            catch (NotSupportedException)
            {
                slanted = true;
            }

            return missing && slanted;
        });

        checks.Report();
        return checks;
    }
}
