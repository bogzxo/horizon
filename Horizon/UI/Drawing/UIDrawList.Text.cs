using Horizon.Rendering;
using System.Numerics;
using System.Runtime.InteropServices;

using Horizon.Graphics;
using Horizon.Rendering.Spriting;
using Horizon.UI.Skinning;

namespace Horizon.UI.Drawing;

// Text and the icons in it.
public sealed partial class UIDrawList
{
    /// <summary>The font text is written in right now, the skin's unless somebody pushed another.</summary>
    public UIFont Font => font ?? Skin.Font;

    /// <summary>
    /// Writes text in another font than the skin's until <see cref="PopFont"/>, a code editor's in a font for code
    /// while the rest of the UI keeps its own. Its glyphs come out of its own texture, a slot of the run like a picture.
    /// </summary>
    public void PushFont(UIFont other)
    {
        fonts.Push(font);
        font = ReferenceEquals(other, Skin.Font) ? null : other;
    }

    /// <summary>Back to the font text was written in before the last <see cref="PushFont"/>.</summary>
    public void PopFont() => font = fonts.Count > 0 ? fonts.Pop() : null;

    /// <summary>
    /// Draws an icon over a rectangle. Its art, and its label on top if it has one.
    /// </summary>
    public void Icon(in UIIcon icon, UIRect rect, Vector4 tint)
    {
        // An icon is as big as the text it sits in, which is rarely a whole multiple of its art.
        Quad(
            rect,
            icon.Region.Position,
            icon.Region.Position + icon.Region.TexelSize,
            SpriteItem.PackColor(tint * RegionTint(icon.Region)),
            ATLAS_SLOT | SpriteItem.SmoothFlag);

        if (icon.Symbol is { } symbol)
        {
            // As tall as it is told to be, as wide as that makes it, in whole pixels so it stays in the middle.
            float height = MathF.Round(rect.Height * icon.SymbolSize);
            Vector2 symbolSize = new(MathF.Round(symbol.TexelSize.X * height / symbol.TexelSize.Y), height);
            Vector2 corner = rect.Min + Vector2.Round((rect.Size - symbolSize) * 0.5f);

            Quad(
                new UIRect(corner, corner + symbolSize),
                symbol.Position,
                symbol.Position + symbol.TexelSize,
                SpriteItem.PackColor(icon.SymbolColor * new Vector4(1.0f, 1.0f, 1.0f, tint.W)),
                ATLAS_SLOT | SpriteItem.SmoothFlag);
        }

        if (icon.Label.Length == 0)
            return;

        // The label is sized to the icon rather than to the text around it.
        float labelScale = rect.Height * ICON_LABEL_HEIGHT / Skin.Font.LineHeight;
        Vector2 size = Skin.Font.Measure(icon.Label, labelScale, markup: false);

        Text(
            icon.Label,
            rect.Center + new Vector2(-0.5f, 0.5f) * size,
            labelScale,
            icon.LabelColor * new Vector4(1.0f, 1.0f, 1.0f, tint.W),
            markup: false);
    }

    /// <summary>
    /// Draws an icon of another skin than the one the list is drawn in, its art and its symbol out of that skin's atlas
    /// and its label in this one's font. For an editor showing a game's icons in a UI that wears its own skin.
    /// </summary>
    public void Icon(UISkin from, in UIIcon icon, UIRect rect, Vector4 tint)
    {
        if (ReferenceEquals(from, Skin))
        {
            Icon(icon, rect, tint);
            return;
        }

        Region(from, icon.Region, rect, tint);

        if (icon.Symbol is { } symbol)
        {
            float height = MathF.Round(rect.Height * icon.SymbolSize);
            Vector2 symbolSize = new(MathF.Round(symbol.TexelSize.X * height / symbol.TexelSize.Y), height);
            Vector2 corner = rect.Min + Vector2.Round((rect.Size - symbolSize) * 0.5f);
            Region(from, symbol, new UIRect(corner, corner + symbolSize), icon.SymbolColor * new Vector4(1.0f, 1.0f, 1.0f, tint.W));
        }

        if (icon.Label.Length == 0)
            return;

        float labelScale = rect.Height * ICON_LABEL_HEIGHT / Skin.Font.LineHeight;
        Vector2 size = Skin.Font.Measure(icon.Label, labelScale, markup: false);
        Text(icon.Label, rect.Center + new Vector2(-0.5f, 0.5f) * size, labelScale, icon.LabelColor * new Vector4(1.0f, 1.0f, 1.0f, tint.W), markup: false);
    }

    /// <summary>
    /// Draws text with the top left corner of its first line at <paramref name="position"/>.
    /// </summary>
    /// <param name="markup">Whether <c>[icon:name]</c> tags are drawn as icons. Off for text somebody typed.</param>
    public void Text(ReadOnlySpan<char> text, Vector2 position, float scale, Vector4 color, bool markup = true) =>
        Text(text, position, scale, color, markup, UITextStyle.None, default);

    /// <summary>
    /// Draws text in a style, bold, italic or both, with the top left corner of its first line at
    /// <paramref name="position"/>. A bold letter is wider than a plain one by <see cref="UIFont.BoldAdvance"/>, which
    /// <see cref="UIFont.MeasureLine"/> knows about.
    /// </summary>
    /// <param name="icons">The set of icons the text is written in from the start, for a piece of a longer text that said so earlier.</param>
    public void Text(ReadOnlySpan<char> text, Vector2 position, float scale, Vector4 color, bool markup, UITextStyle style, ReadOnlySpan<char> icons)
    {
        UIFont font = Font;
        bool own = this.font is null;
        uint packed = SpriteItem.PackColor(color);
        float lineHeight = font.LineHeight * scale;

        // A line that starts on a whole pixel keeps every glyph's edge where the field says it is.
        position = new Vector2(MathF.Round(position.X), MathF.Round(position.Y));
        Vector2 pen = position;

        // Bold is the field's edge further out, and room made for it. Italic leans every letter over about the line it sits on
        bool bold = (style & UITextStyle.Bold) != 0;
        uint glyphFlags = SpriteItem.FieldFlag | (bold ? SpriteItem.BoldFlag : 0u);
        float boldAdvance = bold ? font.BoldAdvance * scale : 0.0f;
        float slant = (style & UITextStyle.Italic) != 0 ? UIFont.ITALIC_SLANT : 0.0f;

        for (int i = 0; i < text.Length; i++)
        {
            char character = text[i];

            if (character == '\n')
            {
                pen = new Vector2(position.X, pen.Y - lineHeight);
                continue;
            }

            // $(name), an icon of UISymbols, as tall as the line wants it
            if (markup && character == '$' && UISymbols.TryRead(text[i..], lineHeight, out char symbol, out int symbolLength, out float symbolWidth))
            {
                Symbol(symbol, UISymbols.InText(pen, lineHeight), color);
                pen.X += symbolWidth;
                i += symbolLength - 1;
                continue;
            }

            if (markup && character == '[' && font.TryReadIconSet(text[i..], out var set, out int skipped))
            {
                icons = set;
                i += skipped - 1;
                continue;
            }

            if (markup && character == '[' && font.TryReadIcon(text[i..], scale, icons, out var name, out int length, out Vector2 size))
            {
                // Centred on the line. Art that hasn't made it into the atlas yet still takes up its room.
                if (Skin.TryGetIcon(name, icons, time, out var icon))
                {
                    Vector2 corner = new(pen.X, MathF.Round(pen.Y - (lineHeight + size.Y) * 0.5f));
                    Icon(icon, new UIRect(corner, corner + size), new Vector4(1.0f, 1.0f, 1.0f, color.W));
                }

                pen.X += size.X;
                i += length - 1;
                continue;
            }

            if (font.Resolve(character, out var glyph))
            {
                // A glyph's offset is measured from the pen to its top left corner, downwards.
                Vector2 topLeft = new(pen.X + glyph.Offset.X * scale, pen.Y - glyph.Offset.Y * scale);
                Vector2 glyphSize = glyph.Size * scale;

                var box = new UIRect(new Vector2(topLeft.X, topLeft.Y - glyphSize.Y), new Vector2(topLeft.X + glyphSize.X, topLeft.Y));

                // The skin's font has a slot of its own, another font's texture is asked for a slot glyph by glyph, an
                // icon in between can have started a new run
                uint flags = glyphFlags | (own ? FONT_SLOT : ImageSlot(font.Texture));
                if (slant == 0.0f)
                    Quad(box, glyph.Position, glyph.Position + glyph.TexelSize, packed, flags, font.FieldSlope);
                else
                    Quad(box, glyph.Position, glyph.Position + glyph.TexelSize, packed, flags, font.FieldSlope, slant, pen.Y - font.Baseline * scale);
            }

            pen.X += glyph.XAdvance * scale + boldAdvance;
            if (i + 1 < text.Length) pen.X += font.Kerning(character, text[i + 1]) * scale;
        }
    }

    /// <summary>
    /// Draws text lined up inside an area, <see cref="Origin.Center"/> centres it,
    /// <see cref="Origin.TopLeft"/> pushes it into the top left corner, and so on.
    /// </summary>
    public void Text(ReadOnlySpan<char> text, UIRect area, Origin align, float scale, Vector4 color, bool markup = true)
    {
        Vector2 size = Font.Measure(text, scale, markup);
        Vector2 center = area.PointAt(align) - align.ToVector() * size;

        Text(text, center + new Vector2(-0.5f, 0.5f) * size, scale, color, markup);
    }

    /// <summary>
    /// Draws text that was cut into lines (see <see cref="UIFont.Wrap"/>) lined up inside an area, the block of
    /// lines placed by the alignment and every line lined up the same way inside of the block.
    /// </summary>
    /// <param name="lines">Where each line starts in the text and how long it is.</param>
    public void Text(ReadOnlySpan<char> text, ReadOnlySpan<(int Start, int Length)> lines, UIRect area, Origin align, float scale, Vector4 color, bool markup = true)
    {
        UIFont font = Font;
        float lineHeight = font.LineHeight * scale;

        Vector2 size = font.Measure(text, lines, scale, markup);
        Vector2 center = area.PointAt(align) - align.ToVector() * size;
        Vector2 topLeft = center + new Vector2(-0.5f, 0.5f) * size;

        // A line is as wide as itself, and slides along the block by how the text is lined up
        float side = align.ToVector().X;
        for (int i = 0; i < lines.Length; i++)
        {
            ReadOnlySpan<char> line = text.Slice(lines[i].Start, lines[i].Length);
            float width = font.Measure(line, scale, markup).X;

            Text(line, new Vector2(topLeft.X + (side + 0.5f) * (size.X - width), topLeft.Y - i * lineHeight), scale, color, markup);
        }
    }
}
