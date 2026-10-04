using System.Numerics;

namespace Horizon.Rendering.UIX;

/// <summary>
/// A distance for each side of a rectangle: padding, or the fixed border of a nine-slice.
/// </summary>
public readonly record struct UIEdges(float Left, float Top, float Right, float Bottom)
{
    public UIEdges(float all)
        : this(all, all, all, all) { }

    public UIEdges(float horizontal, float vertical)
        : this(horizontal, vertical, horizontal, vertical) { }

    /// <summary>How much the edges take up in total on each axis.</summary>
    public Vector2 Total => new(Left + Right, Top + Bottom);
}
