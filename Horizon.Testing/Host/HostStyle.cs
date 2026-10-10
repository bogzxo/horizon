using System.Numerics;

namespace Horizon.Testing;

/// <summary>
/// What the host's own screens are painted in, the selector and the strip over a running example, so the two look
/// like they belong to each other and neither has a colour typed into the middle of its layout.
/// </summary>
internal static class HostStyle
{
    /// <summary>Behind the selector.</summary>
    public static readonly Vector4 Backdrop = new(0.055f, 0.065f, 0.095f, 1.0f);

    /// <summary>What a card is filled with, nearly solid so an example shows through just enough to know it is there.</summary>
    public static readonly Vector4 Card = new(0.1f, 0.12f, 0.17f, 0.94f);

    /// <summary>The cap a key is written on.</summary>
    public static readonly Vector4 Key = new(0.22f, 0.26f, 0.36f, 1.0f);

    /// <summary>Headings, and the number of an example.</summary>
    public static readonly Vector4 Accent = new(1.0f, 0.8f, 0.45f, 1.0f);

    /// <summary>Text that is there to be found, not to be read first.</summary>
    public static readonly Vector4 Dim = new(0.93f, 0.95f, 1.0f, 0.55f);

    /// <summary>How round the corners of a card are.</summary>
    public const float RADIUS = 10.0f;
}
