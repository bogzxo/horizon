using System.Numerics;

namespace Horizon.Rendering.UIX.Skinning;

/// <summary>
/// A piece of art in the skin texture, measured in pixels from the top left of the image.
/// </summary>
/// <param name="Border">
/// How much of each edge keeps its size when the region is drawn as a nine-slice, so corners and
/// outlines stay crisp however far the middle is stretched. Leave empty for art that should simply scale.
/// </param>
public readonly record struct UIRegion(Vector2 Position, Vector2 Size, UIEdges Border = default);
