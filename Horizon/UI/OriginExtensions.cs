using Horizon.Rendering;
using System.Numerics;

namespace Horizon.UI;

public static class OriginExtensions
{
    /// <summary>
    /// Where an origin sits on a unit square centred on zero, Y-up: <see cref="Origin.TopLeft"/> is (-0.5, 0.5).
    /// </summary>
    public static Vector2 ToVector(this Origin origin) => origin switch
    {
        Origin.TopLeft => new Vector2(-0.5f, 0.5f),
        Origin.Top => new Vector2(0.0f, 0.5f),
        Origin.TopRight => new Vector2(0.5f, 0.5f),
        Origin.Left => new Vector2(-0.5f, 0.0f),
        Origin.Right => new Vector2(0.5f, 0.0f),
        Origin.BottomLeft => new Vector2(-0.5f, -0.5f),
        Origin.Bottom => new Vector2(0.0f, -0.5f),
        Origin.BottomRight => new Vector2(0.5f, -0.5f),
        _ => Vector2.Zero
    };
}
