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
    /// Draws text with the top left corner of its first line at <paramref name="position"/>.
    /// </summary>
    /// <param name="markup">Whether <c>[icon:name]</c> tags are drawn as icons. Off for text somebody typed.</param>
    public void Text(ReadOnlySpan<char> text, Vector2 position, float scale, Vector4 color, bool markup = true)
    {
        UIFont font = Skin.Font;
        uint packed = SpriteItem.PackColor(color);
        float lineHeight = font.LineHeight * scale;

        // A line that starts on a whole pixel keeps every glyph's edge where the field says it is.
        position = new Vector2(MathF.Round(position.X), MathF.Round(position.Y));
        Vector2 pen = position;

        // The set of icons the text says it is written in, from where it says so
        ReadOnlySpan<char> icons = default;

        for (int i = 0; i < text.Length; i++)
        {
            char character = text[i];

            if (character == '\n')
            {
                pen = new Vector2(position.X, pen.Y - lineHeight);
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

                Quad(
                    new UIRect(new Vector2(topLeft.X, topLeft.Y - glyphSize.Y), new Vector2(topLeft.X + glyphSize.X, topLeft.Y)),
                    glyph.Position,
                    glyph.Position + glyph.TexelSize,
                    packed,
                    FONT_SLOT | SpriteItem.FieldFlag,
                    font.FieldSlope);
            }

            pen.X += glyph.XAdvance * scale;
            if (i + 1 < text.Length) pen.X += font.Kerning(character, text[i + 1]) * scale;
        }
    }

    /// <summary>
    /// Draws text lined up inside an area, <see cref="Origin.Center"/> centres it,
    /// <see cref="Origin.TopLeft"/> pushes it into the top left corner, and so on.
    /// </summary>
    public void Text(ReadOnlySpan<char> text, UIRect area, Origin align, float scale, Vector4 color, bool markup = true)
    {
        Vector2 size = Skin.Font.Measure(text, scale, markup);
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
        UIFont font = Skin.Font;
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
