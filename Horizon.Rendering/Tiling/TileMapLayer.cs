using System.Numerics;

using DotTiled;

namespace Horizon.Rendering.Tiling;

public enum TileMapLayerKind
{
    Tiles,
    Objects,
    Image
}

/// <summary>How a tile is turned over, see <see cref="TileMapCell.Flip"/>. The three together make every quarter turn and mirror image there is.</summary>
[Flags]
public enum TileFlip
{
    None = 0,
    Horizontal = 1,
    Vertical = 2,

    /// <summary>Mirrored along the diagonal from the top left to the bottom right, before the other two.</summary>
    Diagonal = 4
}

/// <summary>
/// A tile of a tile set, with everything the tile set says about it.
/// </summary>
public sealed class TileMapTile
{
    /// <summary>The number the tile has in the layers of the map.</summary>
    public uint Gid { get; init; }

    /// <summary>The number the tile has in its tile set.</summary>
    public int Id { get; init; }

    public Tileset Tileset { get; init; } = null!;

    public string Class { get; init; } = string.Empty;

    public TileMapProperties Properties { get; init; } = TileMapProperties.Empty;

    /// <summary>The image the tile is cut out of.</summary>
    public string ImagePath { get; init; } = string.Empty;

    /// <summary>Where in the image the tile is, in pixels from its top left corner: left, top, right, bottom.</summary>
    public Vector4 Source { get; init; }

    /// <summary>How big the tile is drawn, which for a tile set with tiles bigger than the grid is more than a cell.</summary>
    public Vector2 Size { get; init; }

    /// <summary>How far the tile set has its tiles drawn from where they would be, in the world's units (Y going up).</summary>
    public Vector2 Offset { get; init; }

    /// <summary>The frames the tile plays through and for how many seconds each, empty for a tile that sits still.</summary>
    public IReadOnlyList<(TileMapTile Tile, float Duration)> Animation { get; internal set; } = [];

    /// <summary>
    /// The rectangles drawn onto the tile in Tiled's collision editor, as the bottom left corner and the size of
    /// each, measured from the bottom left corner of the tile with Y going up.
    /// </summary>
    public IReadOnlyList<(Vector2 Min, Vector2 Size)> Collision { get; init; } = [];

    internal float AnimationLength { get; set; }
}

/// <summary>A tile where it is in a layer.</summary>
/// <param name="X">The column, counted the way Tiled does: from the left.</param>
/// <param name="Y">The row, counted the way Tiled does: from the top.</param>
/// <param name="Centre">The middle of the cell in the world.</param>
public readonly record struct TileMapCell(TileMapLayer Layer, int X, int Y, TileMapTile Tile, TileFlip Flip, Vector2 Centre);

/// <summary>A rectangle of the world, by its bottom left corner and its size.</summary>
public readonly record struct TileMapBox(Vector2 Min, Vector2 Size)
{
    public Vector2 Max => Min + Size;
    public Vector2 Centre => Min + Size / 2.0f;
}

/// <summary>
/// A layer of a <see cref="TileMap"/>. What Tiled says about it is what it starts out as; everything that can be
/// set here can be changed while the map is on screen, which is how a layer is faded, slid away or made to drift.
/// <para>
/// A few custom properties of a layer mean something to the map (their names don't mind their case):
/// <list type="bullet">
/// <item><c>IsCollidable</c> (bool): its tiles are solid, see <see cref="TileMap.BuildColliders"/>.</item>
/// <item><c>CastsShadows</c> (bool): its tiles block light. A layer that doesn't say does if it is collidable.</item>
/// <item><c>Emissive</c> (float, 0 to 1): how much of it shows no matter the light. A sky is 1.</item>
/// <item><c>Foreground</c> (bool, <c>IsAlwaysOnTop</c> works too): it is drawn by <see cref="TileMap.Foreground"/>, in front of whatever is added between the two.</item>
/// <item><c>ScrollX</c>, <c>ScrollY</c> (float): it drifts by itself, in pixels a second. For clouds, with an image layer that repeats.</item>
/// </list>
/// </para>
/// </summary>
public sealed class TileMapLayer
{
    private readonly TileMap map;

    internal TileMapLayer(TileMap map, BaseLayer source, string group, TileMapProperties properties)
    {
        this.map = map;
        Source = source;
        Group = group;
        Properties = properties;

        Name = source.Name;
        Class = source.Class;
        Kind = source switch
        {
            TileLayer => TileMapLayerKind.Tiles,
            ObjectLayer => TileMapLayerKind.Objects,
            _ => TileMapLayerKind.Image
        };

        IsCollidable = properties.GetBool("IsCollidable", properties.GetBool("Collidable"));
        CastsShadows = properties.GetBool("CastsShadows", IsCollidable);
        Emissive = Math.Clamp(properties.GetFloat("Emissive"), 0.0f, 1.0f);
        IsForeground = properties.GetBool("Foreground", properties.GetBool("IsAlwaysOnTop"));

        // Tiled counts down, the world counts up
        Scroll = new Vector2(properties.GetFloat("ScrollX"), -properties.GetFloat("ScrollY"));
    }

    /// <summary>The layer as DotTiled read it.</summary>
    public BaseLayer Source { get; }

    public string Name { get; }
    public string Class { get; }
    public TileMapLayerKind Kind { get; }

    /// <summary>The names of the groups the layer is in from the outermost in, joined by slashes. Empty for a layer that isn't in one.</summary>
    public string Group { get; }

    public TileMapProperties Properties { get; }

    /// <summary>Whether the layer is drawn. What a hidden layer has in it is still there to be asked about (colliders are usually hidden).</summary>
    public bool Visible { get; set; } = true;

    /// <summary>From 0 (not there) to 1. Starts out as the layer's own times that of the groups it is in.</summary>
    public float Opacity { get; set; } = 1.0f;

    /// <summary>A colour everything in the layer is multiplied by.</summary>
    public Vector3 Tint { get; set; } = Vector3.One;

    /// <summary>How far the layer is from where it would be, in the world's units (Y going up). Tiles and objects are asked about where this puts them.</summary>
    public Vector2 WorldOffset { get; set; }

    /// <summary>
    /// How fast the layer moves when the camera does: 1 is with the map, less than that is further away, 0 is
    /// painted onto the screen, more than 1 is closer than the map. All layers are where they were drawn when the
    /// camera is on <see cref="TileMap.ParallaxOrigin"/>.
    /// </summary>
    public Vector2 Parallax { get; set; } = Vector2.One;

    /// <summary>How fast the layer drifts by itself, in the world's units a second.</summary>
    public Vector2 Scroll { get; set; }

    /// <summary>How much of the layer shows no matter the light, from 0 to 1, when the map is drawn by a <see cref="DeferredRenderer2D"/>.</summary>
    public float Emissive { get; set; }

    /// <summary>Whether the layer is left to <see cref="TileMap.Foreground"/> to draw.</summary>
    public bool IsForeground { get; set; }

    public bool IsCollidable { get; set; }
    public bool CastsShadows { get; set; }

    /* Tiles. Kept here rather than read out of the source so they can be changed. */

    internal uint[] Gids = [];
    internal TileFlip[] Flips = [];

    // Where the layer has drifted to, and whether what is on the GPU is out of date
    internal Vector2 Drift;
    internal bool IsDirty = true;
    internal readonly List<TileMapBatch> Batches = [];
    internal readonly List<TileMapAnimated> Animated = [];

    /// <summary>
    /// The tile at a column and a row of a tile layer, counted the way Tiled does (from the top left). Null where
    /// there is none, which is also what anywhere off the map and any other kind of layer is.
    /// </summary>
    public TileMapCell? GetTile(int x, int y)
    {
        int index = map.IndexOf(x, y);
        if (index < 0 || index >= Gids.Length || map.ResolveTile(Gids[index]) is not { } tile)
            return null;

        return new TileMapCell(this, x, y, tile, Flips[index], map.TileToWorld(x, y) + WorldOffset);
    }

    /// <summary>
    /// Puts a tile into a tile layer, or takes one out with a number of 0. What is on screen follows at the next frame.
    /// </summary>
    /// <param name="gid">The number of the tile in the map, see <see cref="TileMapTile.Gid"/>.</param>
    /// <returns>False for somewhere off the map, or a layer that isn't one of tiles.</returns>
    public bool SetTile(int x, int y, uint gid, TileFlip flip = TileFlip.None)
    {
        int index = map.IndexOf(x, y);
        if (index < 0 || index >= Gids.Length)
            return false;

        Gids[index] = gid;
        Flips[index] = flip;
        IsDirty = true;
        return true;
    }

    /// <summary>Every tile of the layer, row by row from the top.</summary>
    public IEnumerable<TileMapCell> Tiles()
    {
        if (Gids.Length == 0)
            yield break;

        for (int row = 0; row < map.Height; row++)
        {
            for (int column = 0; column < map.Width; column++)
            {
                if (GetTile(column + map.Left, row + map.Top) is { } cell)
                    yield return cell;
            }
        }
    }

    public override string ToString() => Group.Length > 0 ? $"{Group}/{Name}" : Name;
}
