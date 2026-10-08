using System;
using System.IO.Compression;
using System.Numerics;

using Horizon.Engine;
using Horizon.Rendering;
using Horizon.Rendering.Tiling;
using Horizon.UI;
using Horizon.UI.Components;

using Silk.NET.Input;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Horizon.Testing.Examples.Rendering;

/// <summary>
/// The tile maps of the engine. The scene writes two small Tiled maps of its own (and the tile set, the images and
/// the object template they use), which between them have one of everything a map can have in it, loads them and
/// draws the first: tiles turned every way there is, animated ones, a layer of clouds that repeats and drifts, a
/// group that scrolls at half speed, a layer in front of everything and a tile that was put down as an object.
/// How the checks went that are made as the scene starts is on the right.
/// </summary>
public class TileMapExample : Scene, ITestControls
{
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
    private readonly string _directory;

    private TileMap _map = null!;
    private Vector2 _eye;

    public TileMapExample()
    {
        // Three pixels of the screen to one of the map
        _camera = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize / 3.0f));
        ActiveCamera = _camera;

        _directory = Path.Combine(Path.GetTempPath(), "horizon-tilemap-test");
        WriteFiles(_directory);

        _map = AddEntity(TileMap.Load(Path.Combine(_directory, "town.tmx")));

        // The layer that is marked as being in front is drawn by this, after whatever would go in between
        AddEntity(_map.Foreground);

        var viewport = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize));
        _compositor = AddComponent(new UICompositor(viewport) { DesignSize = DesignSize });
    }

    public override void PostInit()
    {
        base.PostInit();

        Engine.GL.Enable(Silk.NET.OpenGL.EnableCap.Blend);
        Engine.GL.BlendFunc(Silk.NET.OpenGL.BlendingFactor.SrcAlpha, Silk.NET.OpenGL.BlendingFactor.OneMinusSrcAlpha);
        Engine.GL.ClearColor(0.36f, 0.62f, 0.86f, 1.0f);

        // Looking at the middle of the map, which is also where its layers line up
        _eye = _map.Min + _map.Size / 2.0f;
        _map.ParallaxOrigin = _eye;
        _camera.Position = new Vector3(_eye, 0.0f);

        TestChecks checks = RunChecks(_directory);

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

    /* The files */

    // The numbers of the first tile turned every way a tile can be: not at all, then sideways, upside down, both,
    // and the same four again after turning it over along its diagonal.
    private const uint H = 0x80000000, V = 0x40000000, D = 0x20000000;
    private static readonly uint[] Turns = [1, 1 | H, 1 | V, 1 | H | V, 1 | D, 1 | D | H, 1 | D | V, 1 | D | H | V];

    private const int COLUMNS = 20, ROWS = 12;

    internal static void WriteFiles(string directory)
    {
        Directory.CreateDirectory(Path.Combine(directory, "objects"));

        WriteTiles(Path.Combine(directory, "tiles.png"));
        WriteClouds(Path.Combine(directory, "clouds.png"));

        File.WriteAllText(Path.Combine(directory, "tiles.tsx"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <tileset version="1.10" tiledversion="1.11.0" name="tiles" tilewidth="16" tileheight="16" tilecount="16" columns="4">
             <image source="tiles.png" width="64" height="64"/>
             <tile id="2">
              <animation>
               <frame tileid="2" duration="200"/>
               <frame tileid="3" duration="300"/>
              </animation>
             </tile>
             <tile id="4">
              <objectgroup draworder="index" id="2">
               <object id="1" x="0" y="8" width="16" height="8"/>
              </objectgroup>
             </tile>
             <tile id="5" type="sign">
              <properties>
               <property name="collidable" type="bool" value="true"/>
               <property name="label" value="from the tile"/>
              </properties>
             </tile>
            </tileset>
            """);

        File.WriteAllText(Path.Combine(directory, "objects", "light.tx"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <template>
             <object type="light">
              <properties>
               <property name="light_colour" type="color" value="#ffffd9a8"/>
               <property name="light_radius" type="float" value="120"/>
              </properties>
              <point/>
             </object>
            </template>
            """);

        // The layers, as rows of numbers
        var hills = new uint[COLUMNS * ROWS];
        var solid = new uint[COLUMNS * ROWS];
        var decor = new uint[COLUMNS * ROWS];
        var front = new uint[COLUMNS * ROWS];

        for (int x = 0; x < COLUMNS; x++)
        {
            if (x % 3 != 2) hills[7 * COLUMNS + x] = 2;

            // A gap in the top row of the ground, so there is something for the colliders not to join up
            if (x is not (8 or 9)) solid[9 * COLUMNS + x] = 2;
            solid[10 * COLUMNS + x] = 2;
            solid[11 * COLUMNS + x] = 2;
        }

        for (int i = 0; i < Turns.Length; i++)
            decor[8 * COLUMNS + 1 + i] = Turns[i];

        decor[8 * COLUMNS + 12] = 3;
        decor[8 * COLUMNS + 13] = 3;
        decor[8 * COLUMNS + 15] = 5;
        decor[8 * COLUMNS + 17] = 6;

        front[6 * COLUMNS + 5] = 2;
        front[6 * COLUMNS + 6] = 2;
        front[7 * COLUMNS + 5] = 2;
        front[7 * COLUMNS + 6] = 2;

        string Csv(uint[] gids) => string.Join(",\n", Enumerable.Range(0, ROWS).Select(row => string.Join(",", gids.Skip(row * COLUMNS).Take(COLUMNS))));

        File.WriteAllText(Path.Combine(directory, "town.tmx"), $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <map version="1.10" tiledversion="1.11.0" orientation="orthogonal" renderorder="right-down" width="{COLUMNS}" height="{ROWS}" tilewidth="16" tileheight="16" infinite="0" backgroundcolor="#5c9edb" nextlayerid="9" nextobjectid="7">
             <properties>
              <property name="music" value="town"/>
             </properties>
             <tileset firstgid="1" source="tiles.tsx"/>
             <imagelayer id="1" name="clouds" offsety="16" parallaxx="0.2" repeatx="1">
              <image source="clouds.png" width="64" height="32"/>
              <properties>
               <property name="Emissive" type="float" value="1"/>
               <property name="ScrollX" type="float" value="12"/>
              </properties>
             </imagelayer>
             <group id="2" name="backdrop" opacity="0.5" offsetx="8" parallaxx="0.5">
              <layer id="3" name="hills" width="{COLUMNS}" height="{ROWS}" opacity="0.5" tintcolor="#80ff80">
               <data encoding="csv">
            {Csv(hills)}
            </data>
              </layer>
             </group>
             <layer id="4" name="solid" width="{COLUMNS}" height="{ROWS}">
              <properties>
               <property name="IsCollidable" type="bool" value="true"/>
              </properties>
              <data encoding="csv">
            {Csv(solid)}
            </data>
             </layer>
             <layer id="5" name="decor" width="{COLUMNS}" height="{ROWS}">
              <data encoding="csv">
            {Csv(decor)}
            </data>
             </layer>
             <layer id="6" name="front" width="{COLUMNS}" height="{ROWS}" opacity="0.6">
              <properties>
               <property name="Foreground" type="bool" value="true"/>
              </properties>
              <data encoding="csv">
            {Csv(front)}
            </data>
             </layer>
             <objectgroup id="7" name="things">
              <object id="1" name="lamp" template="objects/light.tx" x="40" y="100">
               <properties>
                <property name="light_colour" type="color" value="#ff0000ff"/>
               </properties>
              </object>
              <object id="2" name="p1" type="spawn" x="32" y="96" width="16" height="32">
               <properties>
                <property name="player" type="int" value="1"/>
                <property name="speed" value="2.5"/>
               </properties>
              </object>
              <object id="3" name="zone" x="160" y="64">
               <polygon points="0,0 32,0 16,24"/>
              </object>
              <object id="4" name="sign" gid="6" x="200" y="128" width="32" height="32" rotation="90"/>
              <object id="5" name="marker" x="8" y="8">
               <point/>
              </object>
              <object id="6" name="note" x="96" y="16" width="80" height="20">
               <text wrap="1">a note</text>
              </object>
             </objectgroup>
            </map>
            """);

        // A map without edges, its tiles packed the way Tiled packs them when asked to: one of them up and to the left
        // of where the map started out, one down and to the right
        var far = new uint[16 * 16];
        var near = new uint[16 * 16];
        far[14 * 16 + 13] = 2;
        near[1 * 16 + 1] = 2 | H;

        File.WriteAllText(Path.Combine(directory, "endless.tmx"), $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <map version="1.10" tiledversion="1.11.0" orientation="orthogonal" renderorder="right-down" width="30" height="20" tilewidth="16" tileheight="16" infinite="1" nextlayerid="2" nextobjectid="1">
             <tileset firstgid="1" source="tiles.tsx"/>
             <layer id="1" name="ground" width="30" height="20">
              <data encoding="base64" compression="zlib">
               <chunk x="-16" y="-16" width="16" height="16">
               {Pack(far)}
               </chunk>
               <chunk x="0" y="0" width="16" height="16">
               {Pack(near)}
               </chunk>
              </data>
             </layer>
            </map>
            """);

        File.WriteAllText(Path.Combine(directory, "slanted.tmx"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <map version="1.10" tiledversion="1.11.0" orientation="isometric" renderorder="right-down" width="4" height="4" tilewidth="32" tileheight="16" infinite="0" nextlayerid="1" nextobjectid="1">
            </map>
            """);
    }

    private static string Pack(uint[] gids)
    {
        var bytes = new byte[gids.Length * 4];
        Buffer.BlockCopy(gids, 0, bytes, 0, bytes.Length);

        using var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(bytes);

        return Convert.ToBase64String(packed.ToArray());
    }

    /// <summary>
    /// Sixteen tiles in four rows. The first is an arrow that points up and has a dot in its top left corner, so
    /// every way of turning it over looks different from every other.
    /// </summary>
    private static void WriteTiles(string path)
    {
        using var image = new Image<Rgba32>(64, 64);

        void Fill(int tile, Rgba32 colour, Func<int, int, bool>? where = null)
        {
            int left = tile % 4 * 16, top = tile / 4 * 16;
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    if (where is null || where(x, y))
                        image[left + x, top + y] = colour;
                }
            }
        }

        var paper = new Rgba32(244, 236, 214);
        var ink = new Rgba32(40, 44, 72);

        Fill(0, paper);
        Fill(0, ink, (x, y) => x is 0 or 15 || y is 0 or 15);
        Fill(0, new Rgba32(214, 60, 70), (x, y) => (x is 7 or 8 && y is >= 3 and <= 12) || (y is >= 3 and <= 7 && Math.Abs(x - 7.5f) <= y - 2.5f));
        Fill(0, new Rgba32(40, 120, 220), (x, y) => x is >= 2 and <= 4 && y is >= 2 and <= 4);

        // Stone, with a lighter top edge
        Fill(1, new Rgba32(92, 96, 122));
        Fill(1, new Rgba32(140, 146, 176), (_, y) => y < 3);
        Fill(1, new Rgba32(70, 74, 98), (x, y) => (x + y * 3) % 7 == 0 && y > 4);

        // Two frames of something that blinks
        Fill(2, new Rgba32(250, 204, 80), (x, y) => Math.Abs(x - 7.5f) + Math.Abs(y - 7.5f) < 7);
        Fill(3, new Rgba32(255, 120, 60), (x, y) => Math.Abs(x - 7.5f) + Math.Abs(y - 7.5f) < 5);

        // A step: only its lower half is there
        Fill(4, new Rgba32(120, 90, 70), (_, y) => y >= 8);
        Fill(4, new Rgba32(170, 130, 96), (_, y) => y is 8 or 9);

        // A sign
        Fill(5, new Rgba32(150, 104, 60), (x, y) => y is >= 2 and <= 10 || x is 7 or 8);
        Fill(5, new Rgba32(236, 214, 160), (x, y) => y is >= 4 and <= 8 && x is >= 2 and <= 13 && (x + y) % 3 != 0);

        image.SaveAsPng(path);
    }

    private static void WriteClouds(string path)
    {
        using var image = new Image<Rgba32>(64, 32);

        foreach (var (cx, cy, radius) in new[] { (14, 18, 9), (26, 14, 12), (40, 18, 10), (52, 20, 7) })
        {
            for (int y = 0; y < 32; y++)
            {
                for (int x = 0; x < 64; x++)
                {
                    if ((x - cx) * (x - cx) + (y - cy) * (y - cy) * 2 < radius * radius && y < 24)
                        image[x, y] = new Rgba32(255, 255, 255, 230);
                }
            }
        }

        image.SaveAsPng(path);
    }

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
            string.Join(" ", map.Layers.Select(layer => layer.Name)) == "clouds hills solid decor front things"
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
                && map.FindObject("note") is { Shape: TileMapShape.Text, Text: "a note" } && map.Objects.Count == 6;
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
                .Otherwise(found => seen.Add($"left:{found.Name}")));

            return string.Join(" ", seen.Where(entry => entry != "layer")) == "light:lamp spawn:p1 shape:zone named:sign"
                && seen.Count(entry => entry == "layer") == 6;
        });

        checks.Check("what is left over goes to whoever takes the rest", () =>
        {
            var left = new List<string>();
            map.DispatchObjects(objects => objects.OfClass("spawn", _ => { }).Otherwise(found => left.Add(found.Name)));
            return string.Join(" ", left) == "lamp zone sign marker note";
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

        checks.Check("the tiles that block light are the ones of the layers that say so", () =>
            map.ShadowCasters().Count() == 58 && map.ShadowCasters().All(centre => centre.Y < 48));

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
