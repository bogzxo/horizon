using System.Numerics;

namespace Horizon.Rendering.Tiling;

public enum TileMapShape
{
    Rectangle,
    Ellipse,
    Point,
    Polygon,
    Polyline,

    /// <summary>A tile put down as an object, anywhere, any size, turned any way. The map draws these itself.</summary>
    Tile,

    Text
}

/// <summary>
/// Something that was placed in an object layer of a map, a spawn point, a light, a trigger. Everything about
/// where it is has been turned into the world's units already (Y going up, see <see cref="TileMap.Origin"/>), so
/// whoever is handed one can put something there without knowing how Tiled counts.
/// </summary>
public sealed class TileMapObject
{
    private readonly TileMap map;

    // The object as Tiled has it, in pixels from the top left corner of the map with Y going down, because Tiled
    private readonly Vector2 pixelPosition;
    private readonly Vector2 pixelCentre;
    private readonly Vector2[] pixelPoints;

    internal TileMapObject(TileMap map, TileMapLayer layer, Vector2 pixelPosition, Vector2 pixelCentre, Vector2[] pixelPoints)
    {
        this.map = map;
        this.pixelPosition = pixelPosition;
        this.pixelCentre = pixelCentre;
        this.pixelPoints = pixelPoints;
        Layer = layer;
    }

    /// <summary>The number Tiled gave the object, which is what an object property of another object points at it with.</summary>
    public int Id { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary>What Tiled calls the class of the object (and called its type before). For a tile object that doesn't say, the class of its tile.</summary>
    public string Class { get; init; } = string.Empty;

    public TileMapShape Shape { get; init; }

    /// <summary>The layer the object is in.</summary>
    public TileMapLayer Layer { get; }

    /// <summary>
    /// Where the object is in the world, the point itself for a point, the middle for everything that has a size,
    /// and the first corner for a polygon or a polyline.
    /// </summary>
    public Vector2 Position => map.TiledToWorld(Shape is TileMapShape.Polygon or TileMapShape.Polyline ? pixelPosition : pixelCentre) + Layer.WorldOffset;

    /// <summary>How big the object is, zero for a point.</summary>
    public Vector2 Size { get; init; }

    /// <summary>The bottom left corner of the object before it is turned, see <see cref="Rotation"/>.</summary>
    public Vector2 Min => Position - Size / 2.0f;

    /// <summary>In degrees, counter-clockwise the way the world turns (Tiled's own are clockwise).</summary>
    public float Rotation { get; init; }

    public bool Visible { get; init; } = true;

    /// <summary>The corners of a polygon or a polyline, in the world.</summary>
    public IReadOnlyList<Vector2> Points
    {
        get
        {
            var points = new Vector2[pixelPoints.Length];
            for (int i = 0; i < points.Length; i++)
                points[i] = map.TiledToWorld(pixelPoints[i]) + Layer.WorldOffset;

            return points;
        }
    }

    /// <summary>What a text object says.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>The tile a tile object shows, null for every other shape.</summary>
    public TileMapTile? Tile { get; init; }

    /// <summary>How the tile of a tile object is turned over.</summary>
    public TileFlip Flip { get; init; }

    /// <summary>
    /// The custom properties of the object. For a tile object these include the properties of its tile, for
    /// whatever the object doesn't say itself.
    /// </summary>
    public TileMapProperties Properties { get; init; } = TileMapProperties.Empty;

    public override string ToString() => $"{Shape} '{Name}' ({Class}) at {Position}";
}

/// <summary>
/// Hands the objects of a map to whoever knows what to do with them, by what they are.
/// <code>
/// map.DispatchObjects(objects => objects
///     .OfClass("spawn", spawn => spawns.Add(spawn.Position))
///     .WithProperty("light_radius", light => renderer.AddLight(new Light2D { Position = light.Position, ... }))
///     .InLayer("triggers", trigger => ...)
///     .Otherwise(unknown => Console.WriteLine($"Nobody wanted {unknown}")));
/// </code>
/// An object goes to every handler it matches, in the order they were added. <see cref="Otherwise"/> gets the
/// ones that matched none.
/// </summary>
public sealed class TileMapObjectDispatcher
{
    private readonly List<(Func<TileMapObject, bool> Matches, Action<TileMapObject> Handle)> handlers = [];
    private Action<TileMapObject>? otherwise;

    /// <summary>Objects of a class, the way it is set in Tiled (the case doesn't matter).</summary>
    public TileMapObjectDispatcher OfClass(string name, Action<TileMapObject> handle) =>
        Where(found => string.Equals(found.Class, name, StringComparison.OrdinalIgnoreCase), handle);

    public TileMapObjectDispatcher Named(string name, Action<TileMapObject> handle) =>
        Where(found => string.Equals(found.Name, name, StringComparison.OrdinalIgnoreCase), handle);

    /// <summary>Objects that have a custom property by this name, whatever it is set to.</summary>
    public TileMapObjectDispatcher WithProperty(string property, Action<TileMapObject> handle) =>
        Where(found => found.Properties.Has(property), handle);

    /// <summary>Objects of a layer, by its name.</summary>
    public TileMapObjectDispatcher InLayer(string layer, Action<TileMapObject> handle) =>
        Where(found => string.Equals(found.Layer.Name, layer, StringComparison.OrdinalIgnoreCase), handle);

    public TileMapObjectDispatcher OfShape(TileMapShape shape, Action<TileMapObject> handle) =>
        Where(found => found.Shape == shape, handle);

    public TileMapObjectDispatcher Where(Func<TileMapObject, bool> matches, Action<TileMapObject> handle)
    {
        handlers.Add((matches, handle));
        return this;
    }

    /// <summary>Every object there is.</summary>
    public TileMapObjectDispatcher All(Action<TileMapObject> handle) => Where(_ => true, handle);

    /// <summary>The objects none of the other handlers matched.</summary>
    public TileMapObjectDispatcher Otherwise(Action<TileMapObject> handle)
    {
        otherwise = handle;
        return this;
    }

    internal void Dispatch(IEnumerable<TileMapObject> objects)
    {
        foreach (TileMapObject found in objects)
        {
            bool wanted = false;

            foreach (var (matches, handle) in handlers)
            {
                if (!matches(found))
                    continue;

                wanted = true;
                handle(found);
            }

            if (!wanted)
                otherwise?.Invoke(found);
        }
    }
}
