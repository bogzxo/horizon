using System.Numerics;

using Horizon.Graphics;
using Horizon.Rendering.Text;

namespace Horizon.UI.Drawing;

/// <summary>
/// Tells text how big the icons in it are. The skin does this for its font.
/// </summary>
internal interface IUIIconSource
{
    /// <summary>How tall icons are, as a multiple of the height of the line they are in.</summary>
    float IconScale { get; }

    /// <summary>The size of an icon's art in texels, false if there is no icon by that name.</summary>
    /// <param name="set">The set of icons the text is written in, empty for none.</param>
    /// <param name="lineTexels">How many texels of the art are as tall as a line. The height of the art, unless the icon says otherwise.</param>
    bool TryGetIconSize(ReadOnlySpan<char> name, ReadOnlySpan<char> set, out Vector2 texelSize, out float lineTexels);
}

/// <summary>
/// A font as the UI needs it, a distance field (see <see cref="DistanceFieldFont"/>) with enough around it to
/// measure text and to place its glyphs, kerning included.
/// Text can have icons in it, <c>[icon:name]</c> is replaced by the icon of that name (see
/// <see cref="Skinning.UISkin.TryGetIcon"/>), sized to sit in the line. A tag that names no icon is left as it is written.
/// <c>[icons:set]</c> draws nothing itself and has every icon after it in the text come from that set of the skin, so
/// <c>"[icons:playstation][icon:pad_a] pick"</c> shows whatever the skin says a PlayStation gamepad has for pad_a.
/// </summary>
public sealed class UIFont
{
    private const string ICON_TAG = "[icon:";
    private const string ICON_SET_TAG = "[icons:";
    private const char ICON_TAG_END = ']';

    private readonly Dictionary<char, CharDefinition> glyphs;
    private readonly Dictionary<int, float> kerning;
    private readonly float spaceAdvance;

    // The first couple of hundred characters, which is nearly everything anybody ever writes, by their number.
    // Every character of every label is looked up three times a tick (measured, lined up, drawn) and through
    // the dictionary that was three hashes a letter to find the letter A where it has always been
    private const int QUICK = 256;
    private readonly CharDefinition[] quickGlyphs = new CharDefinition[QUICK];
    private readonly bool[] quickKnown = new bool[QUICK];

    /// <summary>The font itself, the field and its numbers.</summary>
    public DistanceFieldFont Field { get; }

    /// <summary>The glyph atlas, a distance field. Text takes its colour from whoever draws it.</summary>
    public Texture Texture => Field.Texture;

    /// <summary>The height of a line of text at scale 1, in pixels.</summary>
    public float LineHeight { get; }

    /// <summary>
    /// How much the field changes from one texel of the atlas to the next on its way across an edge (half of it
    /// is the whole spread). Every glyph is drawn with this so the shader knows how wide a screen pixel is in the
    /// field without having to guess it from the pixel next door, see FIELD_FLAG in sprites.slang.
    /// </summary>
    internal float FieldSlope { get; }

    /// <summary>Who to ask about the icons in a text. Without one, tags are just text.</summary>
    internal IUIIconSource? Icons { get; set; }

    /// <summary>
    /// Loads a font, TrueType or BMFont, as a distance field. Render thread.
    /// </summary>
    /// <param name="emSize">How many pixels to the em a TrueType font's metrics come out at, a scale of 1 draws it this big. A bitmap font is the size it was baked at.</param>
    public UIFont(string directory, string file, int emSize = DistanceFieldFont.DEFAULT_EM)
    {
        Field = DistanceFieldFont.Load(directory, file, emSize);
        glyphs = Field.Glyphs;
        kerning = Field.Kerning;

        float tallest = 0.0f;
        foreach (var glyph in glyphs.Values)
            tallest = MathF.Max(tallest, glyph.Offset.Y + glyph.Size.Y);

        LineHeight = Field.LineHeight > 0 ? Field.LineHeight : tallest;

        // The spread is in the pixels the glyphs are measured in, the shader wants it in texels of the atlas, and
        // any glyph that has both sizes says how the two compare
        foreach (var glyph in glyphs.Values)
        {
            if (glyph.Size.X <= 0.0f || glyph.TexelSize.X <= 0.0f) continue;

            float spreadTexels = Field.Spread * glyph.TexelSize.X / glyph.Size.X;
            if (spreadTexels > 0.0f) FieldSlope = 0.5f / spreadTexels;
            break;
        }

        // Fonts are often exported without a glyph for the space, in which case any letter's advance
        // is a better guess than nothing ('n' being the traditional one).
        foreach (var (character, glyph) in glyphs)
        {
            if (character >= QUICK) continue;

            quickGlyphs[character] = glyph;
            quickKnown[character] = true;
        }

        spaceAdvance =
            glyphs.TryGetValue(' ', out var space) ? space.XAdvance
            : glyphs.TryGetValue('n', out var n) ? n.XAdvance
            : LineHeight * 0.5f;
    }

    /// <summary>How much nearer (or further) the second of two characters sits to the first than its advance says, at scale 1.</summary>
    internal float Kerning(char first, char second) =>
        kerning.Count > 0 && kerning.TryGetValue(DistanceFieldFont.KerningKey(first, second), out float kern) ? kern : 0.0f;

    /// <summary>
    /// Finds what a character looks like. The glyph always says how far the character moves the pen,
    /// the result says whether there is anything to draw as well.
    /// </summary>
    internal bool Resolve(char character, out CharDefinition glyph)
    {
        if (character < QUICK)
        {
            if (quickKnown[character])
            {
                glyph = quickGlyphs[character];
                return glyph.Size.X > 0 && glyph.Size.Y > 0;
            }
        }
        else if (glyphs.TryGetValue(character, out glyph))
        {
            return glyph.Size.X > 0 && glyph.Size.Y > 0;
        }

        if (!char.IsWhiteSpace(character) && glyphs.TryGetValue('?', out glyph))
            return true;

        glyph = new CharDefinition { Id = character, XAdvance = spaceAdvance };
        return false;
    }

    /// <summary>
    /// Tests whether text starts with the tag of an icon that exists.
    /// </summary>
    /// <param name="name">The name of the icon.</param>
    /// <param name="length">How many characters the tag takes up.</param>
    /// <param name="size">How big the icon is in a line of text at <paramref name="scale"/>.</param>
    /// <param name="set">The set of icons the text is written in so far, empty for none.</param>
    internal bool TryReadIcon(ReadOnlySpan<char> text, float scale, ReadOnlySpan<char> set, out ReadOnlySpan<char> name, out int length, out Vector2 size)
    {
        name = default;
        length = 0;
        size = default;

        if (Icons is null || !text.StartsWith(ICON_TAG))
            return false;

        int end = text.IndexOf(ICON_TAG_END);
        if (end < 0)
            return false;

        name = text[ICON_TAG.Length..end];
        if (!Icons.TryGetIconSize(name, set, out Vector2 texelSize, out float lineTexels) || texelSize.Y <= 0.0f)
            return false;

        length = end + 1;
        size = IconSize(texelSize, lineTexels, scale);
        return true;
    }

    /// <summary>
    /// Tests whether text starts with a tag that says which set of icons the rest of it is written in.
    /// </summary>
    /// <param name="set">The name of the set.</param>
    /// <param name="length">How many characters the tag takes up, none of which are drawn.</param>
    internal bool TryReadIconSet(ReadOnlySpan<char> text, out ReadOnlySpan<char> set, out int length)
    {
        set = default;
        length = 0;

        if (Icons is null || !text.StartsWith(ICON_SET_TAG))
            return false;

        int end = text.IndexOf(ICON_TAG_END);
        if (end < 0)
            return false;

        set = text[ICON_SET_TAG.Length..end];
        length = end + 1;
        return true;
    }

    /// <summary>
    /// How big an icon is drawn in a line of text. Every icon in a line is the same height whatever the
    /// size of its art (the height of the line, times the skin's icon scale), and as wide as that makes it.
    /// Both in whole pixels, so its edges stay sharp.
    /// </summary>
    private Vector2 IconSize(Vector2 texelSize, float lineTexels, float scale)
    {
        float height = MathF.Max(1.0f, MathF.Round(LineHeight * scale * (Icons?.IconScale ?? 1.0f)));

        // Art that is shorter than what the icon says a line is comes out shorter, at the same scale as its taller mates
        float unit = height / MathF.Max(1.0f, lineTexels);
        return new Vector2(MathF.Max(1.0f, MathF.Round(texelSize.X * unit)), MathF.Max(1.0f, MathF.Round(texelSize.Y * unit)));
    }

    /// <summary>
    /// The size of the box a piece of text takes up. Lines are split on '\n'.
    /// </summary>
    /// <param name="markup">Whether <c>[icon:name]</c> tags are icons. Off for text somebody typed.</param>
    public Vector2 Measure(ReadOnlySpan<char> text, float scale, bool markup = true)
    {
        if (text.IsEmpty)
            return Vector2.Zero;

        // Counted in pixels at the scale asked for, as icons don't scale the way glyphs do.
        float widest = 0.0f;
        float width = 0.0f;
        int lines = 1;
        ReadOnlySpan<char> icons = default;

        for (int i = 0; i < text.Length; i++)
        {
            char character = text[i];

            if (character == '\n')
            {
                widest = MathF.Max(widest, width);
                width = 0.0f;
                lines++;
                continue;
            }

            if (markup && character == ICON_TAG[0])
            {
                if (TryReadIconSet(text[i..], out var set, out int skipped))
                {
                    icons = set;
                    i += skipped - 1;
                    continue;
                }

                if (TryReadIcon(text[i..], scale, icons, out _, out int length, out Vector2 size))
                {
                    width += size.X;
                    i += length - 1;
                    continue;
                }
            }

            Resolve(character, out var glyph);
            width += glyph.XAdvance * scale;
            if (i + 1 < text.Length) width += Kerning(character, text[i + 1]) * scale;
        }

        return new Vector2(MathF.Max(widest, width), lines * LineHeight * scale);
    }

    /// <summary>
    /// Cuts text into lines no wider than a width, breaking between words. A word that is wider than the whole
    /// width on its own is broken wherever it has to be. A '\n' is a line of its own whatever the width.
    /// Nothing is allocated past the list growing, so a label can do this every update.
    /// </summary>
    /// <param name="lines">Where the lines go, each as where it starts in the text and how long it is. Emptied first.</param>
    /// <param name="markup">Whether <c>[icon:name]</c> tags are icons (one piece of a word, as wide as the icon).</param>
    public void Wrap(ReadOnlySpan<char> text, float scale, float maxWidth, List<(int Start, int Length)> lines, bool markup = true)
    {
        lines.Clear();
        if (text.IsEmpty)
            return;

        ReadOnlySpan<char> icons = default;

        int lineStart = 0;
        int lineEnd = 0;
        float lineWidth = 0.0f;
        int at = 0;

        while (at < text.Length)
        {
            // The gap in front of the next word, which is left out if the word starts a line
            float gapWidth = 0.0f;
            while (at < text.Length && text[at] != '\n' && char.IsWhiteSpace(text[at]))
                gapWidth += Advance(text, ref at, scale, ref icons, markup);

            // The end of the line, with or without a line break, and what was placed so far is the line
            if (at >= text.Length || text[at] == '\n')
            {
                lines.Add((lineStart, lineEnd - lineStart));

                if (at < text.Length)
                    at++;

                lineStart = lineEnd = at;
                lineWidth = 0.0f;
                continue;
            }

            int wordStart = at;
            float wordWidth = 0.0f;
            while (at < text.Length && text[at] != '\n' && !char.IsWhiteSpace(text[at]))
                wordWidth += Advance(text, ref at, scale, ref icons, markup);

            if (lineWidth > 0.0f && lineWidth + gapWidth + wordWidth > maxWidth)
            {
                // Doesn't fit after what is there, so it goes on a line of its own
                lines.Add((lineStart, lineEnd - lineStart));
                lineStart = wordStart;
                lineWidth = 0.0f;
            }

            if (lineWidth == 0.0f && wordWidth > maxWidth)
            {
                // Wider than a whole line by itself, so it is cut wherever it runs out of room
                int cut = wordStart;
                float cutWidth = 0.0f;
                ReadOnlySpan<char> cutIcons = icons;

                while (cut < at)
                {
                    int before = cut;
                    float advance = Advance(text, ref cut, scale, ref cutIcons, markup);

                    if (cutWidth > 0.0f && cutWidth + advance > maxWidth)
                    {
                        lines.Add((lineStart, before - lineStart));
                        lineStart = before;
                        cutWidth = 0.0f;
                    }

                    cutWidth += advance;
                }

                lineWidth = cutWidth;
                lineEnd = at;
                continue;
            }

            // Onto the line, with the gap in front of it if it isn't the first word there
            lineWidth += (lineWidth > 0.0f ? gapWidth : 0.0f) + wordWidth;
            lineEnd = at;
        }

        // Text that ends without a line break still has its last line
        if (lineEnd > lineStart || lines.Count == 0)
            lines.Add((lineStart, lineEnd - lineStart));
    }

    /// <summary>
    /// How wide the widest of a set of lines is, for the box wrapped text takes up.
    /// </summary>
    public Vector2 Measure(ReadOnlySpan<char> text, ReadOnlySpan<(int Start, int Length)> lines, float scale, bool markup = true)
    {
        float widest = 0.0f;
        foreach (var (start, length) in lines)
            widest = MathF.Max(widest, Measure(text.Slice(start, length), scale, markup).X);

        return new Vector2(widest, Math.Max(1, lines.Length) * LineHeight * scale);
    }

    /// <summary>
    /// Helper method to step over one piece of a text (a character, or a whole icon tag) and say how wide it is.
    /// A tag that only says which set of icons the text is written in is stepped over and is as wide as nothing.
    /// </summary>
    private float Advance(ReadOnlySpan<char> text, ref int at, float scale, ref ReadOnlySpan<char> icons, bool markup)
    {
        char character = text[at];

        if (markup && character == ICON_TAG[0])
        {
            if (TryReadIconSet(text[at..], out var set, out int skipped))
            {
                icons = set;
                at += skipped;
                return 0.0f;
            }

            if (TryReadIcon(text[at..], scale, icons, out _, out int length, out Vector2 size))
            {
                at += length;
                return size.X;
            }
        }

        Resolve(character, out var glyph);
        at++;
        float kern = at < text.Length ? Kerning(character, text[at]) : 0.0f;
        return (glyph.XAdvance + kern) * scale;
    }
}
