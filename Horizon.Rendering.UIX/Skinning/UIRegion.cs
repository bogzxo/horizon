using System.Numerics;

namespace Horizon.Rendering.UIX.Skinning;

/// <summary>
/// A piece of art as the skin's atlas has it, measured in texels from the top left of the atlas.
/// </summary>
/// <param name="Position">The top left corner of the art in the atlas.</param>
/// <param name="TexelSize">How big the art is in the atlas, see <see cref="Size"/> for how big it is on screen.</param>
/// <param name="Border">
/// How much of each edge (in texels) keeps its size when the region is drawn as a nine-slice, so corners and
/// outlines stay crisp however far the middle is stretched. Leave empty for art that should simply scale.
/// </param>
/// <param name="Scale">How many units on screen a texel takes up. Pixel art is drawn at a whole multiple of its size.</param>
/// <param name="Tint">Multiplied into whatever the art is drawn with, for skins that recolour a piece of art.</param>
/// <param name="Frames">How many frames the art has. The region is the first; the others are "name#1", "name#2" and so on.</param>
/// <param name="FrameTime">How long each frame of an animation is shown for, in seconds.</param>
/// <param name="Content">
/// How far in from each edge (in texels) whatever goes on top of the art sits, for art that isn't the same
/// on every side: a button with a lip along the bottom has its label on the face above it. Empty for art
/// that leaves this to the skin.
/// </param>
public readonly record struct UIRegion(
    Vector2 Position,
    Vector2 TexelSize,
    UIEdges Border = default,
    float Scale = 1.0f,
    Vector4 Tint = default,
    int Frames = 1,
    float FrameTime = 0.1f,
    UIEdges Content = default)
{
    /// <summary>The size of the art on screen when nothing stretches it.</summary>
    public Vector2 Size => TexelSize * Scale;

    /// <summary>
    /// <see cref="Content"/> as it is on screen, or what the skin says when the art has no say in it.
    /// </summary>
    public UIEdges ContentOr(UIEdges otherwise) =>
        Content == default
            ? otherwise
            : new UIEdges(Content.Left * Scale, Content.Top * Scale, Content.Right * Scale, Content.Bottom * Scale);
}

/// <summary>
/// Something small that can be drawn in a line of text with an <c>[icon:name]</c> tag: a region of the skin,
/// optionally recoloured and with a short label on top. A blank round button with an "A" on it makes a
/// gamepad prompt without needing art for every button.
/// </summary>
/// <param name="Region">The art.</param>
/// <param name="Label">Written over the middle of the art, empty for none.</param>
/// <param name="LabelColor">The colour of the label.</param>
/// <param name="Symbol">More art drawn small over the middle, for what there is no letter for (a triangle). Null for none.</param>
/// <param name="SymbolColor">The colour the symbol is multiplied with.</param>
/// <param name="SymbolSize">How tall the symbol is compared to the icon, 1 being just as tall.</param>
public readonly record struct UIIcon(
    UIRegion Region,
    string Label,
    Vector4 LabelColor,
    UIRegion? Symbol = null,
    Vector4 SymbolColor = default,
    float SymbolSize = 0.5f);
