using System.Numerics;

namespace Horizon.Rendering.Text;

/// <summary>
/// One glyph of a font. Where it is in the atlas (<see cref="Position"/> and <see cref="TexelSize"/>, in texels),
/// how big it is drawn and where it sits against the pen (<see cref="Size"/> and <see cref="Offset"/>, in pixels at
/// the font's size, the offset from the pen to its top left corner, downwards), and how far it moves the pen on.
/// The atlas is a distance field that can be drawn at another size than it was made at, so the texels and the
/// pixels needn't agree.
/// </summary>
public readonly struct CharDefinition
{
    public readonly char Id { get; init; }
    public readonly Vector2 Position { get; init; }
    public readonly Vector2 TexelSize { get; init; }
    public readonly Vector2 Size { get; init; }
    public readonly Vector2 Offset { get; init; }
    public readonly float XAdvance { get; init; }
}
