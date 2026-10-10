using System.Numerics;

using Horizon.Logging;

using DotTiled;
using DotTiled.Serialization;

using Horizon.Core.Threading;
using Horizon.Engine;
using Horizon.Graphics;

using TiledObject = DotTiled.Object;

namespace Horizon.Rendering.Tiling;

/// <summary>
/// A map made in Tiled, in the world. Orthogonal maps, read with DotTiled, with everything such a map can have in
/// it. Maps with and without edges, layers of tiles, of objects and of images, groups, tiles that are turned over,
/// bigger than the grid or animated, tile sets cut out of one image or made of many, and layers that are tinted,
/// see-through, moved, repeated or scrolling at a speed of their own.
/// <code>
/// var map = TileMap.Load("Assets/maps/town.tmx", objects => objects
///     .OfClass("spawn", spawn => spawns.Add(spawn.Position))
///     .WithProperty("light_radius", light => AddLight(light)));
///
/// renderer.AddEntity(map);                // everything behind the players
/// renderer.AddEntity(players);
/// renderer.AddEntity(map.Foreground);     // the layers marked Foreground, in front of them
///
/// foreach (TileMapBox box in map.BuildColliders())
///     body.CreateRectangularFixture(box.Min, box.Size);
/// </code>
/// The map is laid out in the world's units, one to a pixel of its tiles, with Y going up. Its bottom left corner
/// is at <see cref="Origin"/> and it reaches up and to the right from there. Whatever is asked of it (where an
/// object is, where a tile is) is answered in those, Tiled's own way of counting stays in the file.
/// See <see cref="TileMapLayer"/> for the custom properties of a layer the map goes by.
/// </summary>
public sealed class TileMap : GameObject
{
    // How many tiles to a side are drawn in one go, which is also what is left out in one go when it is out of view
    private const int BATCH_TILES = 32;

    // How far inside of its edges a tile is cut out of its image, so its neighbours in the image never show at its seams
    private const float SOURCE_INSET = 0.01f;

    // The map on the GPU, every layer and tile of it in one draw
    private readonly TileMapGpu gpu = new();
    private readonly TileMapGeometryOcclusion geometry = new();
    private readonly TileMapSilhouettes silhouettes = new();
    private readonly List<(TileMapLayer Layer, TileMapGpu.Layer Settings, bool Foreground)> shown = [];

    private readonly string directory;
    private readonly List<TileMapLayer> layers = [];
    private readonly List<TileMapObject> objects = [];

    // The tile sets in the order of their first numbers, with where their images are looked for and their tiles by number
    private readonly List<(uint First, Tileset Set, string Directory, Dictionary<uint, Tile> Tiles)> tilesets = [];
    private readonly Dictionary<uint, TileMapTile?> tiles = [];
    private readonly Dictionary<string, TileMapTexture> textures = [];

    private float time;

    /// <summary>What a layer looked like at the end of a tick, for frames that are drawn alongside the simulation.</summary>
    private struct CapturedLayer
    {
        public bool Visible, IsForeground;
        public float Opacity, Emissive;
        public Vector3 Tint;
        public Vector2 WorldOffset, Parallax, Scroll, Drift;
        public int Version;
        public uint[] Gids;
        public TileFlip[] Flips;
    }

    /// <summary>The whole map at the end of a tick.</summary>
    private sealed class CapturedMap
    {
        public float Time;
        public Vector2 Origin, ParallaxOrigin;
        public bool SnapParallax;
        public CapturedLayer[] Layers = [];
    }

    private readonly SnapshotBuffer<CapturedMap> captured = new(static () => new CapturedMap());

    /// <summary>The map as DotTiled read it, for whatever isn't offered here.</summary>
    public Map Data { get; }

    /// <summary>The file the map was read from.</summary>
    public string Path { get; }

    /// <summary>Where the bottom left corner of the map is in the world.</summary>
    public Vector2 Origin { get; set; }

    /// <summary>The size of the map in tiles. For a map without edges (infinite) that is the box around everything in it.</summary>
    public int Width { get; }

    /// <inheritdoc cref="Width"/>
    public int Height { get; }

    /// <summary>
    /// The column and the row the map starts at, the way Tiled counts them. Zero for a map with edges, one without
    /// can have tiles to the left of and above where it started out, which makes these negative.
    /// </summary>
    public int Left { get; }

    /// <inheritdoc cref="Left"/>
    public int Top { get; }

    /// <summary>The size of a cell of the grid.</summary>
    public Vector2 TileSize { get; }

    /// <summary>The size of the whole map in the world.</summary>
    public Vector2 Size => new Vector2(Width, Height) * TileSize;

    /// <summary>The bottom left corner of the map in the world, which is its <see cref="Origin"/>.</summary>
    public Vector2 Min => Origin;

    /// <summary>The top right corner of the map in the world.</summary>
    public Vector2 Max => Origin + Size;

    /// <summary>The colour the map has behind everything in Tiled, null if it has none.</summary>
    public Vector4? BackgroundColor { get; }

    public TileMapProperties Properties { get; }

    /// <summary>Every layer from the back to the front. Groups are gone, what a group says about what is in it is worked into its layers.</summary>
    public IReadOnlyList<TileMapLayer> Layers => layers;

    /// <summary>Every object of every object layer, see also <see cref="DispatchObjects"/>.</summary>
    public IReadOnlyList<TileMapObject> Objects => objects;

    /// <summary>
    /// The point of the world at which every layer is where it was drawn, however it scrolls. The further the camera is
    /// from it, the further a layer with a <see cref="TileMapLayer.Parallax"/> other than 1 has moved. Starts out as
    /// what the map says in Tiled, which unless somebody changed it there is its top left corner.
    /// </summary>
    public Vector2 ParallaxOrigin { get; set; }

    /// <summary>
    /// Whether layers are only ever moved by whole units when they scroll at a speed of their own. Pixel art that is
    /// moved by half a pixel shimmers.
    /// </summary>
    public bool SnapParallax { get; set; } = false;

    /// <summary>
    /// Whether the map darkens what is drawn next to its own geometry, the wall behind a floor going darker
    /// towards the floor, the corner a crate makes with the ground. Off unless asked, or the map says so itself
    /// with a <c>GeometryOcclusion</c> property (bool, with <c>GeometryOcclusionReach</c> and
    /// <c>GeometryOcclusionStrength</c> next to it if it wants). The geometry is what blocks light
    /// (<see cref="ShadowCasters"/>, the layers with <c>CastsShadows</c> and the objects of the ones with
    /// <c>BlocksLight</c>), it is worked out once and again whenever a tile of those changes, and every other
    /// layer reads it where it ends up on screen. It goes the same way the occlusion maps of the tile sets do,
    /// so it shows when the renderer has its <c>AmbientOcclusion</c> on and counts by its <c>BakedStrength</c>.
    /// It is the cheap way to the same corners the marched occlusion finds, with that one's <c>Strength</c> at 0
    /// the map still has its corners dark and nothing is marched at all, with both on the corners get both.
    /// </summary>
    public bool GeometryOcclusion { get; set; }

    /// <summary>
    /// How much the occlusion maps of the tile sets count on this map (the <c>_ao</c> image next to a tile set's
    /// picture), 0 to leave them out, 1 to take them as painted. Apart from <see cref="GeometryOcclusionStrength"/>
    /// on purpose, the one is what the artist painted into the material and the other what the map is shaped like,
    /// and how much of each a game wants is a matter of taste. The renderer's <c>BakedStrength</c> scales the two
    /// together afterwards. A map property of the same name sets it too.
    /// </summary>
    public float OcclusionMapStrength { get; set; } = 1.0f;

    /// <summary>How far from the geometry the darkening reaches, in world units. See <see cref="GeometryOcclusion"/>.</summary>
    public float GeometryOcclusionReach { get; set; } = 24.0f;

    /// <summary>How dark it gets right up against a flat piece of the geometry, 0 not at all, 1 black as far as the ambient light goes.</summary>
    public float GeometryOcclusionStrength { get; set; } = 0.9f;

    /// <summary>
    /// Draws the layers that are marked as being in the foreground (see <see cref="TileMapLayer.IsForeground"/>).
    /// Add it to whatever draws the map after everything that goes in between, the players for one. Left alone, the
    /// map draws those layers itself, in their place among the others.
    /// </summary>
    public TileMapForeground Foreground { get; }

    private TileMap(Map data, string path)
    {
        Name = "Tile Map";
        Data = data;
        Path = path;
        directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? string.Empty;

        TileSize = new Vector2(data.TileWidth, data.TileHeight);
        Properties = new TileMapProperties(data.Properties, directory);
        GeometryOcclusion = Properties.GetBool("GeometryOcclusion");
        GeometryOcclusionReach = Properties.GetFloat("GeometryOcclusionReach", GeometryOcclusionReach);
        GeometryOcclusionStrength = Properties.GetFloat("GeometryOcclusionStrength", GeometryOcclusionStrength);
        OcclusionMapStrength = Properties.GetFloat("OcclusionMapStrength", OcclusionMapStrength);
        BackgroundColor = data.BackgroundColor is { A: > 0 } background ? TileMapProperties.ToVector(background) : null;
        Foreground = new TileMapForeground(this);

        foreach (Tileset set in data.Tilesets)
        {
            // The images of a tile set in a file of its own are named from where that file is
            string setDirectory = set.Source.HasValue
                ? System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, set.Source.Value))) ?? directory
                : directory;

            tilesets.Add((set.FirstGID.GetValueOr(1u), set, setDirectory, set.Tiles.ToDictionary(tile => tile.ID)));
        }
        tilesets.Sort((a, b) => a.First.CompareTo(b.First));

        (Left, Top, Width, Height) = Measure(data);

        ParallaxOrigin = TiledToWorld(new Vector2(data.ParallaxOriginX, data.ParallaxOriginY));

        AddLayers(data.Layers, string.Empty, true, 1.0f, Vector3.One, Vector2.Zero, Vector2.One);
    }

    /* Loading */

    /// <summary>
    /// Reads a map.
    /// </summary>
    /// <param name="objects">
    /// Says who gets which of the map's objects, see <see cref="TileMapObjectDispatcher"/>. They are handed out before
    /// this returns. The map can be asked for them later as well (<see cref="Objects"/>, <see cref="DispatchObjects"/>).
    /// </param>
    /// <param name="origin">Where the bottom left corner of the map goes in the world.</param>
    /// <exception cref="Exception">The file isn't there, isn't an orthogonal map, or names a file that isn't there.</exception>
    public static TileMap Load(string path, Action<TileMapObjectDispatcher>? objects = null, Vector2 origin = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"The map '{path}' doesn't exist.");

        Map data;
        try
        {
            data = Loader.Default().LoadMap(path);
        }
        catch (Exception e)
        {
            throw new Exception($"The map '{path}' can't be read: {e.Message}", e);
        }

        if (data.Orientation != MapOrientation.Orthogonal)
            throw new NotSupportedException($"The map '{path}' is {data.Orientation}, only orthogonal maps are supported.");

        var map = new TileMap(data, path) { Origin = origin };
        map.ParallaxOrigin += origin;

        if (objects is not null)
            map.DispatchObjects(objects);

        return map;
    }

    /// <summary>
    /// Reads a map, see <see cref="Load"/>. What went wrong with one that can't be read goes into the log.
    /// </summary>
    public static bool TryLoad(string path, out TileMap? map, Action<TileMapObjectDispatcher>? objects = null, Vector2 origin = default)
    {
        try
        {
            map = Load(path, objects, origin);
            return true;
        }
        catch (Exception e)
        {
            Log.Error($"[TileMap] {e.Message}");
            map = null;
            return false;
        }
    }

    /// <summary>The box around everything a map without edges has in it. A map with edges is as big as it says.</summary>
    private static (int Left, int Top, int Width, int Height) Measure(Map data)
    {
        if (!data.Infinite)
            return (0, 0, (int)data.Width, (int)data.Height);

        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;

        void Visit(IEnumerable<BaseLayer> found)
        {
            foreach (BaseLayer layer in found)
            {
                if (layer is Group group)
                    Visit(group.Layers);

                if (layer is not TileLayer { Data.HasValue: true } tileLayer || !tileLayer.Data.Value.Chunks.HasValue)
                    continue;

                foreach (Chunk chunk in tileLayer.Data.Value.Chunks.Value)
                {
                    left = Math.Min(left, chunk.X);
                    top = Math.Min(top, chunk.Y);
                    right = Math.Max(right, chunk.X + (int)chunk.Width);
                    bottom = Math.Max(bottom, chunk.Y + (int)chunk.Height);
                }
            }
        }

        Visit(data.Layers);

        // Nothing in it at all
        return left > right ? (0, 0, 1, 1) : (left, top, right - left, bottom - top);
    }

    private void AddLayers(IEnumerable<BaseLayer> found, string group, bool visible, float opacity, Vector3 tint, Vector2 offset, Vector2 parallax)
    {
        foreach (BaseLayer source in found)
        {
            // What the layer says for itself, on top of what the groups it is in say
            bool ownVisible = visible && source.Visible;
            float ownOpacity = opacity * source.Opacity;
            Vector2 ownOffset = offset + new Vector2(source.OffsetX, source.OffsetY);
            Vector2 ownParallax = parallax * new Vector2(source.ParallaxX, source.ParallaxY);

            Vector3 ownTint = tint;
            if (source.TintColor.HasValue)
            {
                Vector4 colour = TileMapProperties.ToVector(source.TintColor.Value);
                ownTint *= new Vector3(colour.X, colour.Y, colour.Z);
                ownOpacity *= colour.W;
            }

            if (source is Group inner)
            {
                AddLayers(inner.Layers, group.Length > 0 ? $"{group}/{inner.Name}" : inner.Name, ownVisible, ownOpacity, ownTint, ownOffset, ownParallax);
                continue;
            }

            var layer = new TileMapLayer(this, source, group, new TileMapProperties(source.Properties, directory))
            {
                Visible = ownVisible,
                Opacity = ownOpacity,
                Tint = ownTint,
                Parallax = ownParallax,

                // Tiled counts down, the world counts up
                WorldOffset = new Vector2(ownOffset.X, -ownOffset.Y)
            };
            layers.Add(layer);

            switch (source)
            {
                case TileLayer tileLayer:
                    ReadTiles(layer, tileLayer);
                    break;

                case ObjectLayer objectLayer:
                    foreach (TiledObject found2 in objectLayer.Objects)
                        objects.Add(ReadObject(layer, found2));
                    break;
            }
        }
    }

    private void ReadTiles(TileMapLayer layer, TileLayer source)
    {
        layer.Gids = new uint[Width * Height];
        layer.Flips = new TileFlip[Width * Height];

        if (!source.Data.HasValue)
            return;

        void Place(uint[] gids, FlippingFlags[]? flags, int x, int y, int width, int height)
        {
            for (int row = 0; row < height; row++)
            {
                for (int column = 0; column < width; column++)
                {
                    int from = row * width + column;
                    int to = IndexOf(x + column, y + row);

                    if (to < 0 || from >= gids.Length)
                        continue;

                    layer.Gids[to] = gids[from];
                    layer.Flips[to] = flags is not null && from < flags.Length ? ToFlip(flags[from]) : TileFlip.None;
                }
            }
        }

        Data data = source.Data.Value;

        if (data.Chunks.HasValue)
        {
            foreach (Chunk chunk in data.Chunks.Value)
                Place(chunk.GlobalTileIDs, chunk.FlippingFlags, chunk.X, chunk.Y, (int)chunk.Width, (int)chunk.Height);
        }
        else if (data.GlobalTileIDs.HasValue)
        {
            Place(data.GlobalTileIDs.Value, data.FlippingFlags.HasValue ? data.FlippingFlags.Value : null, 0, 0, (int)source.Width, (int)source.Height);
        }
    }

    private static TileFlip ToFlip(FlippingFlags flags) =>
        ((flags & FlippingFlags.FlippedHorizontally) != 0 ? TileFlip.Horizontal : TileFlip.None)
        | ((flags & FlippingFlags.FlippedVertically) != 0 ? TileFlip.Vertical : TileFlip.None)
        | ((flags & FlippingFlags.FlippedDiagonally) != 0 ? TileFlip.Diagonal : TileFlip.None);

    /* Where things are */

    // The left and the bottom edge of the map in Tiled's pixels, which is what everything is measured from
    private float PixelLeft => Left * TileSize.X;
    private float PixelBottom => (Top + Height) * TileSize.Y;

    /// <summary>Turns a point of the map the way Tiled has it (pixels from its top left corner, Y going down) into where that is in the world.</summary>
    public Vector2 TiledToWorld(Vector2 pixel) => Origin + new Vector2(pixel.X - PixelLeft, PixelBottom - pixel.Y);

    /// <summary>Turns a point of the world into the pixel of the map it is on the way Tiled counts them.</summary>
    public Vector2 WorldToTiled(Vector2 world) => new(world.X - Origin.X + PixelLeft, PixelBottom - (world.Y - Origin.Y));

    /// <summary>The middle of a cell in the world, by its column and row the way Tiled counts them (from the top left).</summary>
    public Vector2 TileToWorld(int x, int y) => TiledToWorld(new Vector2(x + 0.5f, y + 0.5f) * TileSize);

    /// <summary>The column and the row a point of the world is in, the way Tiled counts them. It may well be off the map.</summary>
    public (int X, int Y) WorldToTile(Vector2 world)
    {
        Vector2 pixel = WorldToTiled(world) / TileSize;
        return ((int)MathF.Floor(pixel.X), (int)MathF.Floor(pixel.Y));
    }

    /// <summary>Whether a column and a row are on the map.</summary>
    public bool Contains(int x, int y) => IndexOf(x, y) >= 0;

    internal int IndexOf(int x, int y)
    {
        int column = x - Left, row = y - Top;
        return column >= 0 && row >= 0 && column < Width && row < Height ? row * Width + column : -1;
    }

    /* Layers and tiles */

    /// <summary>A layer by its name (the case doesn't matter), null if there is none.</summary>
    public TileMapLayer? FindLayer(string name) =>
        layers.Find(layer => string.Equals(layer.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every tile of every tile layer, layer by layer from the back.</summary>
    public IEnumerable<TileMapCell> Tiles() => layers.SelectMany(layer => layer.Tiles());

    /// <summary>The tiles at a point of the world, one for every tile layer that has one there, from the back to the front.</summary>
    public IEnumerable<TileMapCell> TilesAt(Vector2 world)
    {
        foreach (TileMapLayer layer in layers)
        {
            var (x, y) = WorldToTile(world - layer.WorldOffset);
            if (layer.GetTile(x, y) is { } cell)
                yield return cell;
        }
    }

    /// <summary>
    /// What a tile number of a layer stands for, null for no tile (0) and for a number no tile set of the map has.
    /// </summary>
    public TileMapTile? ResolveTile(uint gid)
    {
        if (gid == 0)
            return null;

        // Asked by the game in its updates and by whoever builds the layers for drawing, which may be at once
        lock (tiles)
            return ResolveTileLocked(gid);
    }

    private TileMapTile? ResolveTileLocked(uint gid)
    {
        if (gid == 0)
            return null;

        if (tiles.TryGetValue(gid, out TileMapTile? known))
            return known;

        TileMapTile? tile = null;

        for (int i = tilesets.Count - 1; i >= 0; i--)
        {
            if (gid >= tilesets[i].First)
            {
                tile = DescribeTile(gid, tilesets[i]);
                break;
            }
        }

        // Before its frames are looked up, which may well be the tile itself
        tiles[gid] = tile;

        // The tile set it is out of, found with a loop. With a lambda that needs the tile, every look up made an
        // object for the lambda to keep the tile in as it started, the ones that found the tile in the dictionary
        // three lines in and left again as well, which is nearly all of them
        Dictionary<uint, Tile>? definitions = null;
        if (tile is not null)
        {
            foreach (var set in tilesets)
            {
                if (set.Set == tile.Tileset)
                {
                    definitions = set.Tiles;
                    break;
                }
            }
        }

        if (tile is not null && definitions is not null && definitions.TryGetValue((uint)tile.Id, out Tile? definition) && definition.Animation.Count > 0)
        {
            var frames = new List<(TileMapTile, float)>();
            uint first = gid - (uint)tile.Id;

            foreach (Frame frame in definition.Animation)
            {
                if (ResolveTileLocked(first + frame.TileID) is { } shown)
                    frames.Add((shown, MathF.Max(0.001f, (float)frame.Duration / 1000.0f)));
            }

            tile.Animation = frames;
            tile.AnimationLength = frames.Sum(frame => frame.Item2);
        }

        return tile;
    }

    private TileMapTile? DescribeTile(uint gid, (uint First, Tileset Set, string Directory, Dictionary<uint, Tile> Tiles) found)
    {
        Tileset set = found.Set;
        uint id = gid - found.First;
        found.Tiles.TryGetValue(id, out Tile? definition);

        string image;
        float x, y, width, height;

        if (set.Image.HasValue && set.Image.Value.Source.HasValue)
        {
            // One image, cut into tiles of the same size
            if (set.TileCount > 0 && id >= set.TileCount)
                return null;

            int imageWidth = set.Image.Value.Width.GetValueOr(0);
            int columns = set.Columns > 0
                ? (int)set.Columns
                : Math.Max(1, (imageWidth - (int)set.Margin * 2 + (int)set.Spacing) / Math.Max(1, (int)(set.TileWidth + set.Spacing)));

            image = System.IO.Path.GetFullPath(System.IO.Path.Combine(found.Directory, set.Image.Value.Source.Value));
            width = set.TileWidth;
            height = set.TileHeight;
            x = set.Margin + id % columns * (set.TileWidth + set.Spacing);
            y = set.Margin + id / columns * (set.TileHeight + set.Spacing);
        }
        else if (definition is { Image.HasValue: true } && definition.Image.Value.Source.HasValue)
        {
            // A collection of images, the tile is the whole of its own, or the part of it the set says
            Image own = definition.Image.Value;

            image = System.IO.Path.GetFullPath(System.IO.Path.Combine(found.Directory, own.Source.Value));
            x = definition.X;
            y = definition.Y;
            width = definition.Width > 0 ? definition.Width : own.Width.GetValueOr((int)set.TileWidth);
            height = definition.Height > 0 ? definition.Height : own.Height.GetValueOr((int)set.TileHeight);
        }
        else
        {
            return null;
        }

        var collision = new List<(Vector2, Vector2)>();
        if (definition is { ObjectLayer.HasValue: true })
        {
            foreach (TiledObject shape in definition.ObjectLayer.Value.Objects)
            {
                // Only what has a width and a height to it. Measured from the top in Tiled, from the bottom here
                if (shape is RectangleObject or EllipseObject && shape.Width > 0 && shape.Height > 0)
                    collision.Add((new Vector2(shape.X, height - shape.Y - shape.Height), new Vector2(shape.Width, shape.Height)));
            }
        }

        return new TileMapTile
        {
            Gid = gid,
            Id = (int)id,
            Tileset = set,
            Class = definition?.Type ?? string.Empty,
            Properties = definition is null ? TileMapProperties.Empty : new TileMapProperties(definition.Properties, found.Directory),
            ImagePath = image,
            Source = new Vector4(x + SOURCE_INSET, y + SOURCE_INSET, x + width - SOURCE_INSET, y + height - SOURCE_INSET),

            // A tile set can have its tiles squeezed into the grid whatever size they are
            Size = set.RenderSize == TileRenderSize.Grid ? TileSize : new Vector2(width, height),
            Offset = set.TileOffset.HasValue ? new Vector2(set.TileOffset.Value.X, -set.TileOffset.Value.Y) : Vector2.Zero,
            Collision = collision
        };
    }

    /* Objects */

    /// <summary>The object with a number, which is how an object property of another object names it. Null if there is none.</summary>
    public TileMapObject? FindObject(int id) => objects.Find(found => found.Id == id);

    /// <summary>The first object by a name (the case doesn't matter), null if there is none.</summary>
    public TileMapObject? FindObject(string name) =>
        objects.Find(found => string.Equals(found.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Hands the objects of the map to whoever wants them, see <see cref="TileMapObjectDispatcher"/> for how to say who that is.
    /// </summary>
    public void DispatchObjects(Action<TileMapObjectDispatcher> configure)
    {
        var dispatcher = new TileMapObjectDispatcher();
        configure(dispatcher);
        dispatcher.Dispatch(objects);
    }

    private TileMapObject ReadObject(TileMapLayer layer, TiledObject source)
    {
        var position = new Vector2(source.X, source.Y);
        var size = new Vector2(source.Width, source.Height);

        // Tiled turns an object clockwise around where it is, with Y going down
        float turn = source.Rotation * MathF.PI / 180.0f;
        Vector2 Turned(Vector2 offset) => position + new Vector2(
            offset.X * MathF.Cos(turn) - offset.Y * MathF.Sin(turn),
            offset.X * MathF.Sin(turn) + offset.Y * MathF.Cos(turn));

        TileMapTile? tile = source is TileObject tileObject ? ResolveObjectTile(tileObject) : null;

        TileMapShape shape = source switch
        {
            TileObject => TileMapShape.Tile,
            PointObject => TileMapShape.Point,
            EllipseObject => TileMapShape.Ellipse,
            PolygonObject => TileMapShape.Polygon,
            PolylineObject => TileMapShape.Polyline,
            TextObject => TileMapShape.Text,
            _ => TileMapShape.Rectangle
        };

        // What an object is placed by is its top left corner, except for a tile, which stands on its bottom left one
        // unless its tile set says otherwise
        Vector2 centre = shape switch
        {
            TileMapShape.Point => position,
            TileMapShape.Tile => Turned((new Vector2(0.5f) - AnchorOf(tile)) * size),
            _ => Turned(size / 2.0f)
        };

        List<Vector2>? corners = source switch
        {
            PolygonObject polygon => polygon.Points,
            PolylineObject polyline => polyline.Points,
            _ => null
        };

        return new TileMapObject(this, layer, position, centre, corners is null ? [] : [.. corners.Select(Turned)])
        {
            Id = (int)source.ID.GetValueOr(0u),
            Name = source.Name,
            Class = source.Type.Length > 0 ? source.Type : tile?.Class ?? string.Empty,
            Shape = shape,
            Size = shape == TileMapShape.Point ? Vector2.Zero : size,
            Rotation = -source.Rotation,
            Visible = source.Visible,
            Text = source is TextObject text ? text.Text : string.Empty,
            Tile = tile,
            Flip = source is TileObject flipped ? ToFlip(flipped.FlippingFlags) : TileFlip.None,
            Properties = new TileMapProperties(source.Properties, directory, tile?.Properties)
        };
    }

    private TileMapTile? ResolveObjectTile(TileObject source)
    {
        uint gid = source.GID;

        // The tile of an object out of a template is counted in the template's own tile set, the map counts differently
        if (source.TemplateTileset.HasValue)
        {
            Tileset template = source.TemplateTileset.Value;
            int match = tilesets.FindIndex(known => known.Set.Name == template.Name);
            if (match < 0)
                return null;

            gid = gid - template.FirstGID.GetValueOr(1u) + tilesets[match].First;
        }

        return ResolveTile(gid);
    }

    // The point of a tile object that is where the object is, as a share of its size from its top left corner
    private static Vector2 AnchorOf(TileMapTile? tile) => (tile?.Tileset.ObjectAlignment ?? ObjectAlignment.Unspecified) switch
    {
        ObjectAlignment.TopLeft => new Vector2(0.0f, 0.0f),
        ObjectAlignment.Top => new Vector2(0.5f, 0.0f),
        ObjectAlignment.TopRight => new Vector2(1.0f, 0.0f),
        ObjectAlignment.Left => new Vector2(0.0f, 0.5f),
        ObjectAlignment.Center => new Vector2(0.5f, 0.5f),
        ObjectAlignment.Right => new Vector2(1.0f, 0.5f),
        ObjectAlignment.Bottom => new Vector2(0.5f, 1.0f),
        ObjectAlignment.BottomRight => new Vector2(1.0f, 1.0f),
        _ => new Vector2(0.0f, 1.0f)
    };

    /* What is solid */

    /// <summary>
    /// Everything of the map there is to bump into, as few rectangles as it takes. The tiles of the layers that are
    /// collidable (see <see cref="TileMapLayer.IsCollidable"/>), tiles that have a custom property <c>collidable</c>
    /// set on them in their tile set, the shapes that were drawn onto tiles in Tiled's collision editor, and the
    /// rectangles of collidable object layers. Tiles next to each other come back as one rectangle.
    /// </summary>
    /// <param name="isSolid">Decides for every tile instead, for when being solid depends on something else altogether.</param>
    public List<TileMapBox> BuildColliders(Func<TileMapCell, bool>? isSolid = null)
    {
        var boxes = new List<TileMapBox>();
        var solid = new bool[Width * Height];

        foreach (TileMapLayer layer in layers)
        {
            if (layer.Kind == TileMapLayerKind.Objects && layer.IsCollidable)
            {
                boxes.AddRange(objects
                    .Where(found => found.Layer == layer && found.Shape is TileMapShape.Rectangle or TileMapShape.Tile && found.Size is { X: > 0, Y: > 0 })
                    .Select(found => new TileMapBox(found.Min, found.Size)));
                continue;
            }

            foreach (TileMapCell cell in layer.Tiles())
            {
                bool wanted = isSolid?.Invoke(cell) ?? (layer.IsCollidable || cell.Tile.Properties.GetBool("collidable") || cell.Tile.Collision.Count > 0);
                if (!wanted)
                    continue;

                Vector2 corner = cell.Centre - TileSize / 2.0f;

                if (cell.Tile.Collision.Count > 0)
                {
                    // Only the part of the tile that was drawn as solid
                    foreach (var (min, size) in cell.Tile.Collision)
                        boxes.Add(new TileMapBox(corner + min, size));
                }
                else if (layer.WorldOffset != Vector2.Zero)
                {
                    // Off the grid, so there is nothing to put it together with
                    boxes.Add(new TileMapBox(corner, TileSize));
                }
                else
                {
                    solid[IndexOf(cell.X, cell.Y)] = true;
                }
            }
        }

        Merge(solid, boxes);
        return boxes;
    }

    /// <summary>
    /// Turns the cells that are solid into rectangles. Every run of them along a row is one, and runs that are right
    /// underneath each other and just as long are one as well.
    /// </summary>
    private void Merge(bool[] solid, List<TileMapBox> boxes)
    {
        // A run that is still growing downwards, by the column it starts at and how long it is
        var open = new Dictionary<(int Start, int Length), int>();

        for (int row = 0; row <= Height; row++)
        {
            var runs = new HashSet<(int, int)>();

            for (int column = 0; row < Height && column < Width; column++)
            {
                if (!solid[row * Width + column])
                    continue;

                int start = column;
                while (column < Width && solid[row * Width + column])
                    column++;

                runs.Add((start, column - start));
            }

            // What didn't carry on into this row is done
            foreach (var (run, firstRow) in open.Where(pair => !runs.Contains(pair.Key)).ToArray())
            {
                open.Remove(run);

                // From the bottom of the last row it was in up to the top of the first
                var min = TiledToWorld(new Vector2((run.Start + Left) * TileSize.X, (row + Top) * TileSize.Y));
                boxes.Add(new TileMapBox(min, new Vector2(run.Length * TileSize.X, (row - firstRow) * TileSize.Y)));
            }

            foreach (var run in runs)
                open.TryAdd(run, row);
        }
    }

    /// <summary>
    /// Makes a pathfinder over the floor of the map as its tiles are right now, see <see cref="TileMapPathfinder"/>.
    /// What is solid is what <see cref="BuildColliders"/> makes solid, unless <paramref name="isSolid"/> says otherwise.
    /// </summary>
    public TileMapPathfinder CreatePathfinder(Func<TileMapCell, bool>? isSolid = null) => new(this, isSolid);

    /// <summary>
    /// The middle of every cell that blocks light, for an <see cref="OcclusionMap2D"/> laid over the map
    /// (<c>new OcclusionMap2D(map.Width, map.Height, map.Origin, map.TileSize)</c>). The tiles of the layers that
    /// cast shadows (<see cref="TileMapLayer.CastsShadows"/>) and every cell under an object of a layer that blocks
    /// light (<see cref="TileMapLayer.BlocksLight"/>), by the box around the object, turned or not.
    /// </summary>
    public IEnumerable<Vector2> ShadowCasters()
    {
        foreach (var cell in layers.Where(layer => layer.CastsShadows).SelectMany(layer => layer.Tiles()))
            yield return cell.Centre;

        foreach (Vector2 centre in ObjectShadowCasters())
            yield return centre;
    }

    /// <summary>
    /// How many cells a tile is cut into each way when the map is asked what blocks light by the shape of things
    /// (<see cref="ShadowCasterTexels"/>, <see cref="CreateOcclusion"/>). A cell a texel of the art, less on a map
    /// so big that would be silly.
    /// </summary>
    public int ShadowCellsPerTile => Math.Max(1, Math.Min((int)MathF.Min(TileSize.X, TileSize.Y), 4096 / Math.Max(1, Math.Max(Width, Height))));

    /// <summary>
    /// What blocks light by the shape of it and not by the square it sits in. <see cref="ShadowCasters"/> says a
    /// tile blocks light or doesn't, which is right for a wall and wrong for a pot, a tuft of grass, the rounded
    /// end of a platform, the air in the corners of those tiles is air and the wall behind it wants lighting like
    /// the wall next to it. Here every tile is cut into <paramref name="perTile"/> cells each way (see
    /// <see cref="ShadowCellsPerTile"/>) and only the ones the picture of the tile has something in come back, as
    /// where they are in a grid that many times the map's, counted from its bottom left corner. The objects of the
    /// layers that block light are still the box around them.
    /// </summary>
    public IEnumerable<(int X, int Y)> ShadowCasterTexels(int perTile)
    {
        perTile = Math.Max(perTile, 1);

        foreach (var cell in layers.Where(layer => layer.CastsShadows).SelectMany(layer => layer.Tiles()))
        {
            int tileX = (int)MathF.Floor((cell.Centre.X - Origin.X) / TileSize.X);
            int tileY = (int)MathF.Floor((cell.Centre.Y - Origin.Y) / TileSize.Y);
            Vector4 source = cell.Tile.Source;

            for (int v = 0; v < perTile; v++)
            {
                for (int u = 0; u < perTile; u++)
                {
                    // Which part of the image ends up here, turned over the way the tile is, the same thing
                    // tilemap.slang does to its corners (v counts from the top, like the image)
                    var at = new Vector2((u + 0.5f) / perTile, (v + 0.5f) / perTile);
                    if (cell.Flip.HasFlag(TileFlip.Vertical)) at.Y = 1.0f - at.Y;
                    if (cell.Flip.HasFlag(TileFlip.Horizontal)) at.X = 1.0f - at.X;
                    if (cell.Flip.HasFlag(TileFlip.Diagonal)) at = new Vector2(at.Y, at.X);

                    float x = source.X + (source.Z - source.X) * at.X;
                    float y = source.Y + (source.W - source.Y) * at.Y;
                    if (silhouettes.Filled(cell.Tile.ImagePath, x, y))
                        yield return (tileX * perTile + u, tileY * perTile + (perTile - 1 - v));
                }
            }
        }

        foreach (Vector2 centre in ObjectShadowCasters())
        {
            int left = (int)MathF.Floor((centre.X - Origin.X) / TileSize.X) * perTile;
            int bottom = (int)MathF.Floor((centre.Y - Origin.Y) / TileSize.Y) * perTile;
            for (int y = bottom; y < bottom + perTile; y++)
            {
                for (int x = left; x < left + perTile; x++)
                    yield return (x, y);
            }
        }
    }

    /// <summary>
    /// Makes the occlusion map of this map, what blocks light in it by the shape of every tile
    /// (<see cref="ShadowCasterTexels"/>), with a texel of the distance field a cell so it is no bigger than it
    /// has to be. Hand it to the renderer (<c>DeferredRenderer2D.Occlusion</c>), it belongs to whoever asked.
    /// </summary>
    public Horizon.Rendering.Lighting.OcclusionMap2D CreateOcclusion()
    {
        int perTile = ShadowCellsPerTile;
        var occlusion = new Horizon.Rendering.Lighting.OcclusionMap2D(Width * perTile, Height * perTile, Origin, TileSize / perTile, fieldTexelsPerCell: 1);

        foreach (var (x, y) in ShadowCasterTexels(perTile))
            occlusion[x, y] = true;

        return occlusion;
    }

    /// <summary>Helper method to list the middle of every cell under an object of a layer that blocks light.</summary>
    private IEnumerable<Vector2> ObjectShadowCasters()
    {
        foreach (TileMapObject found in objects)
        {
            if (!found.Layer.BlocksLight || found.Shape is TileMapShape.Point or TileMapShape.Text)
                continue;

            // The box around it as it sits in the world, a polygon by its corners, everything else by its size and turn
            Vector2 min, max;
            if (found.Shape is TileMapShape.Polygon or TileMapShape.Polyline)
            {
                var points = found.Points;
                if (points.Count == 0) continue;
                min = max = points[0];
                foreach (Vector2 point in points)
                {
                    min = Vector2.Min(min, point);
                    max = Vector2.Max(max, point);
                }
            }
            else
            {
                (float sin, float cos) = MathF.SinCos(found.Rotation * MathF.PI / 180.0f);
                Vector2 half = found.Size / 2.0f;
                var reach = new Vector2(MathF.Abs(half.X * cos) + MathF.Abs(half.Y * sin), MathF.Abs(half.X * sin) + MathF.Abs(half.Y * cos));
                min = found.Position - reach;
                max = found.Position + reach;
            }

            // Every cell of the map's grid the box covers, by more than a sliver
            int left = (int)MathF.Floor((min.X - Origin.X) / TileSize.X + 0.05f);
            int right = (int)MathF.Ceiling((max.X - Origin.X) / TileSize.X - 0.05f);
            int bottom = (int)MathF.Floor((min.Y - Origin.Y) / TileSize.Y + 0.05f);
            int top = (int)MathF.Ceiling((max.Y - Origin.Y) / TileSize.Y - 0.05f);
            for (int y = bottom; y < top; y++)
            {
                for (int x = left; x < right; x++)
                    yield return Origin + new Vector2(x + 0.5f, y + 0.5f) * TileSize;
            }
        }
    }

    /* Drawing */

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);

        time += dt;

        foreach (TileMapLayer layer in layers)
            layer.Drift += layer.Scroll * dt;
    }

    /// <summary>
    /// Publishes what every layer looks like, and a copy of the tiles of any layer that changed, for the frames that are
    /// drawn alongside the simulation. Simulation thread, at the end of every tick.
    /// </summary>
    public override void Capture()
    {
        if (captured.BeginPublish() is { } into)
        {
            into.Time = time;
            into.Origin = Origin;
            into.ParallaxOrigin = ParallaxOrigin;
            into.SnapParallax = SnapParallax;

            if (into.Layers.Length != layers.Count)
                into.Layers = new CapturedLayer[layers.Count];

            for (int i = 0; i < layers.Count; i++)
            {
                TileMapLayer layer = layers[i];

                // A change of the tiles is copied once, and that copy is never written to, every snapshot after it shares it
                if (layer.CapturedVersion != layer.Version)
                {
                    layer.CapturedGids = (uint[])layer.Gids.Clone();
                    layer.CapturedFlips = (TileFlip[])layer.Flips.Clone();
                    layer.CapturedVersion = layer.Version;
                }

                into.Layers[i] = new CapturedLayer
                {
                    Visible = layer.Visible,
                    IsForeground = layer.IsForeground,
                    Opacity = layer.Opacity,
                    Emissive = layer.Emissive,
                    Tint = layer.Tint,
                    WorldOffset = layer.WorldOffset,
                    Parallax = layer.Parallax,
                    Scroll = layer.Scroll,
                    Drift = layer.Drift,
                    Version = layer.Version,
                    Gids = layer.CapturedGids,
                    Flips = layer.CapturedFlips
                };
            }
        }

        base.Capture();
    }

    public override void Render(float dt)
    {
        // Somebody else draws the foreground if they took it, at a time of their choosing
        Draw(foreground: Foreground.Parent is null ? null : false);

        base.Render(dt);
    }

    /// <param name="foreground">Whether to draw the layers that are in the foreground or the ones that aren't, null for all of them.</param>
    internal void Draw(bool? foreground)
    {
        if (Engine.ActiveCamera is not { } camera)
            return;

        using var scope = GraphicsDevice.Current.BeginGpuScope("tile map");

        // Drawn alongside the simulation, the map is drawn as it was between the last two ticks, out of what was published
        RenderFrame frame = RenderFrame.Active;
        CapturedMap? before = null, after = null;
        bool blend = false;
        if (frame.IsDecoupled)
        {
            if (!captured.TryGet(frame, out before, out after, out blend))
                return;

            blend &= before.Layers.Length == after.Layers.Length;
        }

        float alpha = frame.Alpha;
        Vector2 origin = after?.Origin ?? Origin;
        Vector2 parallaxOrigin = after?.ParallaxOrigin ?? ParallaxOrigin;
        bool snapParallax = after?.SnapParallax ?? SnapParallax;
        float shownTime = after is null ? time : blend ? Interpolate.Linear(before!.Time, after.Time, alpha) : after.Time;

        var view = camera.Bounds;
        var viewMin = new Vector2(view.X, view.Y);
        var viewMax = new Vector2(view.X + view.Width, view.Y + view.Height);
        // The middle of what is shown, which is the camera as the frame has it, between two ticks and rounded to
        // its pixels. Camera.Position is where the simulation last put it, a tick ahead and unrounded, and parallax
        // worked out from that against a view worked out from the other had every layer that isn't at the map's
        // own depth jittering by the difference every frame, worst of all under a screen shake
        var eye = new Vector2(view.X + view.Width * 0.5f, view.Y + view.Height * 0.5f);

        // What the geometry of the map does to the ambient light, if anybody asked, see GeometryOcclusion
        uint geometrySlot = TileMapGpu.NoTexture;
        if (GeometryOcclusion && GeometryOcclusionStrength > 0.0f && GeometryOcclusionReach > 0.0f)
        {
            geometry.Ensure(this, GeometryOcclusionReach, Math.Clamp(GeometryOcclusionStrength, 0.0f, 1.0f), GeometrySignature());
            geometrySlot = geometry.Slot ?? TileMapGpu.NoTexture;
        }

        Vector2 worldSize = Size;

        // Every layer as it is shown this frame, built if it changed, with the settings the GPU draws it by
        shown.Clear();
        int count = after?.Layers.Length ?? layers.Count;
        for (int index = 0; index < count && index < layers.Count; index++)
        {
            TileMapLayer layer = layers[index];
            CapturedLayer state = after is null ? Live(layer) : after.Layers[index];

            if (!state.Visible || state.Opacity <= 0.0f)
                continue;

            Vector2 drift = blend ? Interpolate.Linear(before!.Layers[index].Drift, state.Drift, alpha) : state.Drift;

            // How far the layer has lagged behind the camera (or run ahead of it) on its way from where all layers line up
            Vector2 scrolled = (eye - parallaxOrigin) * (Vector2.One - state.Parallax);
            Vector2 moved = state.WorldOffset + drift + scrolled;

            if (snapParallax && state.Parallax != Vector2.One)
                moved = new Vector2(MathF.Round(moved.X), MathF.Round(moved.Y));

            if (after is null ? layer.IsDirty : layer.BuiltVersion != state.Version)
                Build(layer, state.Gids ?? layer.Gids, state.Flips ?? layer.Flips, state.Version);

            if (layer.Source is ImageLayer image)
                PlaceImage(layer, image, viewMin - origin - moved, viewMax - origin - moved);

            Animate(layer, shownTime);

            shown.Add((layer, new TileMapGpu.Layer
            {
                Offset = origin + moved,

                // What the geometry is made of doesn't read it, that is lit as its face and has no corner with itself
                Geometry = layer.CastsShadows || layer.BlocksLight ? TileMapGpu.NoTexture : geometrySlot,
                GeometryOrigin = origin,
                GeometryScale = Vector2.One / Vector2.Max(worldSize, Vector2.One),
                OcclusionMaps = Math.Clamp(OcclusionMapStrength, 0.0f, 1.0f),
                Emissive = state.Emissive,
                Tint = new Vector4(state.Tint, state.Opacity)
            }, state.IsForeground));
        }

        if (!gpu.Sync(shown, textureOf ??= TextureOf))
            return;

        gpu.Draw(foreground, viewMin, viewMax, camera);
    }

    /// <summary>Helper method to boil everything the geometry occlusion is made from down to a number that is another one whenever any of it changes.</summary>
    private long GeometrySignature()
    {
        var hash = new HashCode();
        hash.Add(GeometryOcclusionReach);
        hash.Add(GeometryOcclusionStrength);
        foreach (TileMapLayer layer in layers)
        {
            hash.Add(layer.CastsShadows);
            hash.Add(layer.BlocksLight);
            if (layer.CastsShadows) hash.Add(layer.Version);
        }

        hash.Add(objects.Count);
        return hash.ToHashCode();
    }

    protected override void DisposeOther()
    {
        // The images are the object manager's, shared with whichever map uses the same ones
        gpu.Dispose();
        geometry.Dispose();
        textures.Clear();

        base.DisposeOther();
    }

    // Handed to the GPU side every frame. Made once, a method handed over by its name is a new delegate every time
    private Func<string, TileMapTexture>? textureOf;

    private TileMapTexture TextureOf(string path)
    {
        if (!textures.TryGetValue(path, out TileMapTexture? texture))
            textures[path] = texture = TileMapTexture.Load(path);

        return texture;
    }

    /// <summary>
    /// Helper method for what a layer looks like as it is, for a frame that is drawn with the simulation standing still.
    /// </summary>
    private static CapturedLayer Live(TileMapLayer layer) => new()
    {
        Visible = layer.Visible,
        IsForeground = layer.IsForeground,
        Opacity = layer.Opacity,
        Emissive = layer.Emissive,
        Tint = layer.Tint,
        WorldOffset = layer.WorldOffset,
        Parallax = layer.Parallax,
        Scroll = layer.Scroll,
        Drift = layer.Drift,
        Version = layer.Version
    };

    /// <summary>
    /// Puts together what a layer is drawn from. Everything is placed as if the bottom left corner of the map were at
    /// zero and the layer hadn't moved. Where the map is and how far the layer has scrolled is added when it is drawn.
    /// </summary>
    /// <param name="gids">The tiles to build from, the layer's own or a copy of them that was published.</param>
    /// <param name="version">Which change of the tiles that is.</param>
    private void Build(TileMapLayer layer, uint[] gids, TileFlip[] flips, int version)
    {
        layer.IsDirty = false;
        layer.BuiltVersion = version;
        layer.Batches.Clear();
        layer.Animated.Clear();

        switch (layer.Kind)
        {
            case TileMapLayerKind.Tiles:
                BuildTiles(layer, gids, flips);
                break;

            case TileMapLayerKind.Objects:
                BuildTileObjects(layer);
                break;

            case TileMapLayerKind.Image:
                // One batch that is filled in when it is drawn, there may be any number of the image in view
                if (layer.Source is ImageLayer { Image.HasValue: true } image && image.Image.Value.Source.HasValue)
                    layer.Batches.Add(new TileMapBatch(System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, image.Image.Value.Source.Value))));
                break;
        }
    }

    private void BuildTiles(TileMapLayer layer, uint[] gids, TileFlip[] flips)
    {
        // By the image and by the part of the map, so what is out of view is left out a part at a time
        var batches = new Dictionary<(string, int, int), TileMapBatch>();

        for (int row = 0; row < Height; row++)
        {
            for (int column = 0; column < Width; column++)
            {
                int index = row * Width + column;
                if (index >= gids.Length || ResolveTile(gids[index]) is not { } tile)
                    continue;

                var key = (tile.ImagePath, column / BATCH_TILES, row / BATCH_TILES);
                if (!batches.TryGetValue(key, out TileMapBatch? batch))
                {
                    batches[key] = batch = new TileMapBatch(tile.ImagePath);
                    layer.Batches.Add(batch);
                }

                // A tile that is bigger than a cell stands on the bottom left corner of its cell and sticks out of it
                var corner = new Vector2(column * TileSize.X, (Height - 1 - row) * TileSize.Y) + tile.Offset;

                int instance = batch.Add(new TileInstance
                {
                    Position = corner + tile.Size / 2.0f,
                    Size = tile.Size,
                    Source = tile.Source,
                    Flip = (float)flips[index]
                });

                if (tile.Animation.Count > 1)
                    layer.Animated.Add(new TileMapAnimated(batch, instance, tile));
            }
        }
    }

    /// <summary>The tiles that were put down as objects, any size, anywhere, turned any bloody way.</summary>
    private void BuildTileObjects(TileMapLayer layer)
    {
        var batches = new Dictionary<string, TileMapBatch>();

        foreach (TileMapObject found in objects)
        {
            if (found.Layer != layer || found.Tile is not { } tile || !found.Visible)
                continue;

            if (!batches.TryGetValue(tile.ImagePath, out TileMapBatch? batch))
            {
                batches[tile.ImagePath] = batch = new TileMapBatch(tile.ImagePath);
                layer.Batches.Add(batch);
            }

            int instance = batch.Add(new TileInstance
            {
                // Where it is in the world without where the map and the layer are, which are added when it is drawn
                Position = found.Position - Origin - layer.WorldOffset,
                Size = found.Size,
                Source = tile.Source,
                Rotation = found.Rotation * MathF.PI / 180.0f,
                Flip = (float)found.Flip
            });

            if (tile.Animation.Count > 1)
                layer.Animated.Add(new TileMapAnimated(batch, instance, tile));
        }
    }

    /// <summary>
    /// Lays the image of an image layer out for what is in view, once where the layer has it, or as many times as it
    /// takes to cover the view along the ways it repeats.
    /// </summary>
    /// <param name="viewMin">The bottom left corner of what the camera sees, measured the way the layer's batches are.</param>
    private void PlaceImage(TileMapLayer layer, ImageLayer image, Vector2 viewMin, Vector2 viewMax)
    {
        if (layer.Batches.Count == 0)
            return;

        TileMapBatch batch = layer.Batches[0];
        Vector2 size = TextureOf(batch.ImagePath).Size;
        if (size.X <= 0 || size.Y <= 0)
            return;

        bool repeatX = image.RepeatX.GetValueOr(false), repeatY = image.RepeatY.GetValueOr(false);

        // The bottom left corner of the image where the layer has it
        Vector2 corner = TiledToWorld(new Vector2(image.X, image.Y + size.Y)) - Origin;

        int firstX = 0, lastX = 0, firstY = 0, lastY = 0;
        if (repeatX)
        {
            firstX = (int)MathF.Floor((viewMin.X - corner.X) / size.X);
            lastX = (int)MathF.Floor((viewMax.X - corner.X) / size.X);
        }
        if (repeatY)
        {
            firstY = (int)MathF.Floor((viewMin.Y - corner.Y) / size.Y);
            lastY = (int)MathF.Floor((viewMax.Y - corner.Y) / size.Y);
        }

        int count = (lastX - firstX + 1) * (lastY - firstY + 1);
        Vector2 first = corner + new Vector2(firstX, firstY) * size + size / 2.0f;

        // Nothing has moved far enough to need another copy
        if (batch.Instances.Count == count && count > 0 && batch.Instances[0].Position == first)
            return;

        batch.Clear();
        for (int y = firstY; y <= lastY; y++)
        {
            for (int x = firstX; x <= lastX; x++)
            {
                batch.Add(new TileInstance
                {
                    Position = corner + new Vector2(x, y) * size + size / 2.0f,
                    Size = size,
                    Source = new Vector4(0, 0, size.X, size.Y)
                });
            }
        }
    }

    /// <summary>Moves the tiles of a layer that play through frames on to the frame it is time for.</summary>
    /// <param name="time">How long the map has been running, as of the frame that is drawn.</param>
    private static void Animate(TileMapLayer layer, float time)
    {
        foreach (TileMapAnimated animated in layer.Animated)
        {
            TileMapTile tile = animated.Tile;
            if (tile.AnimationLength <= 0.0f)
                continue;

            float at = time % tile.AnimationLength;
            int frame = 0;
            while (frame < tile.Animation.Count - 1 && at >= tile.Animation[frame].Duration)
                at -= tile.Animation[frame++].Duration;

            if (frame == animated.Frame)
                continue;

            animated.Frame = frame;

            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(animated.Batch.Instances);
            span[animated.Index].Source = tile.Animation[frame].Tile.Source;
            animated.Batch.NeedsUpload = true;
        }
    }
}

/// <summary>
/// The layers of a <see cref="TileMap"/> that go in front of whatever is drawn after the map. Add this to the same
/// renderer once everything that goes in between is in it. See <see cref="TileMap.Foreground"/>.
/// </summary>
public sealed class TileMapForeground : GameObject
{
    private readonly TileMap map;

    internal TileMapForeground(TileMap map)
    {
        Name = "Tile Map Foreground";
        this.map = map;
    }

    public override void Render(float dt)
    {
        map.Draw(foreground: true);
        base.Render(dt);
    }
}
