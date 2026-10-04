using System.Numerics;

namespace Horizon.Rendering.UIX;

/// <summary>
/// An axis aligned rectangle in UI space. Like the rest of the engine UI space is Y-up, so
/// <see cref="Min"/> is the bottom left corner and <see cref="Max"/> the top right one.
/// </summary>
public readonly record struct UIRect(Vector2 Min, Vector2 Max)
{
    public Vector2 Size => Max - Min;
    public Vector2 Center => (Min + Max) * 0.5f;
    public float Width => Max.X - Min.X;
    public float Height => Max.Y - Min.Y;
    public bool IsEmpty => Max.X <= Min.X || Max.Y <= Min.Y;

    public static UIRect FromCenter(Vector2 center, Vector2 size) =>
        new(center - size * 0.5f, center + size * 0.5f);

    public bool Contains(Vector2 point) =>
        point.X >= Min.X && point.X <= Max.X && point.Y >= Min.Y && point.Y <= Max.Y;

    public UIRect Intersect(UIRect other) =>
        new(Vector2.Max(Min, other.Min), Vector2.Min(Max, other.Max));

    /// <summary>Moves every edge inwards.</summary>
    public UIRect Shrink(UIEdges edges) =>
        new(
            new Vector2(Min.X + edges.Left, Min.Y + edges.Bottom),
            new Vector2(Max.X - edges.Right, Max.Y - edges.Top));

    /// <summary>The point of this rectangle an origin names, e.g. its top left corner.</summary>
    public Vector2 PointAt(Origin origin) => Center + origin.ToVector() * Size;
}
