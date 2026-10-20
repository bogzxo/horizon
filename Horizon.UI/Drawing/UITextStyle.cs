namespace Horizon.UI.Drawing;

/// <summary>
/// How the letters of a piece of text are drawn. The font is one distance field and has no bold or italic face of its
/// own, so both are made out of the field as it is drawn, see <see cref="UIDrawList.Text(System.ReadOnlySpan{char}, System.Numerics.Vector2, float, System.Numerics.Vector4, bool, UITextStyle, System.ReadOnlySpan{char})"/>.
/// </summary>
[Flags]
public enum UITextStyle
{
    None = 0,

    /// <summary>Thicker, the edge of the ink is taken a little way out into the field, and every letter a little wider.</summary>
    Bold = 1,

    /// <summary>Leaning to the right, every letter's quad sheared about the line it sits on.</summary>
    Italic = 2
}
