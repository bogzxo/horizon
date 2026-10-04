using System.Numerics;

using Horizon.OpenGL.Assets;
using Horizon.Rendering.Text;

namespace Horizon.Rendering.UIX.Drawing;

/// <summary>
/// A bitmap font as the UI needs it: enough to measure text and to place its glyphs.
/// </summary>
public sealed class UIFont
{
    private readonly Dictionary<char, CharDefinition> glyphs;
    private readonly int spaceAdvance;

    /// <summary>The glyph atlas. Only its alpha is used, text takes its colour from whoever draws it.</summary>
    public Texture Texture { get; }

    /// <summary>The height of a line of text at scale 1, in pixels.</summary>
    public float LineHeight { get; }

    /// <summary>
    /// Loads a BMFont definition and its atlas. Has to run on the GL thread.
    /// </summary>
    public UIFont(string directory, string file)
    {
        var importer = new BMFontImporter(directory, file);

        glyphs = importer.Definitions;
        Texture = importer.Texture ?? Texture.Invalid;

        float tallest = 0.0f;
        foreach (var glyph in glyphs.Values)
            tallest = MathF.Max(tallest, glyph.Offset.Y + glyph.Size.Y);

        LineHeight = importer.LineHeight > 0 ? importer.LineHeight : tallest;

        // Fonts are often exported without a glyph for the space, in which case any letter's advance
        // is a better guess than nothing ('n' being the traditional one).
        spaceAdvance =
            glyphs.TryGetValue(' ', out var space) ? space.XAdvance
            : glyphs.TryGetValue('n', out var n) ? n.XAdvance
            : (int)(LineHeight * 0.5f);
    }

    /// <summary>
    /// Finds what a character looks like. The glyph always says how far the character moves the pen;
    /// the result says whether there is anything to draw as well.
    /// </summary>
    internal bool Resolve(char character, out CharDefinition glyph)
    {
        if (glyphs.TryGetValue(character, out glyph))
            return glyph.Size.X > 0 && glyph.Size.Y > 0;

        if (!char.IsWhiteSpace(character) && glyphs.TryGetValue('?', out glyph))
            return true;

        glyph = new CharDefinition { Id = character, XAdvance = spaceAdvance };
        return false;
    }

    /// <summary>
    /// The size of the box a piece of text takes up. Lines are split on '\n'.
    /// </summary>
    public Vector2 Measure(ReadOnlySpan<char> text, float scale)
    {
        if (text.IsEmpty)
            return Vector2.Zero;

        float widest = 0.0f;
        float width = 0.0f;
        int lines = 1;

        foreach (char character in text)
        {
            if (character == '\n')
            {
                widest = MathF.Max(widest, width);
                width = 0.0f;
                lines++;
                continue;
            }

            Resolve(character, out var glyph);
            width += glyph.XAdvance;
        }

        return new Vector2(MathF.Max(widest, width), lines * LineHeight) * scale;
    }
}
