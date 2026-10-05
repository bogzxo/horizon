using System.Numerics;
using System.Runtime.InteropServices;

using Horizon.OpenGL.Assets;
using Horizon.Rendering.Spriting;
using Horizon.Rendering.UIX.Skinning;

namespace Horizon.Rendering.UIX.Drawing;

/// <summary>
/// Everything the UI wants on screen this frame, as quads in painter's order: what is added later is
/// drawn on top. Components paint into it on the logic thread and the sprite renderer draws the result
/// (the quads are its <see cref="SpriteItem"/>s), the whole UI in a single call unless more than two
/// custom images are involved.
/// </summary>
public sealed class UIDrawList
{
    // The slots of the textures every run shares. Custom images take the ones after.
    private const uint ATLAS_SLOT = 0;
    private const uint FONT_SLOT = 1;
    private const uint IMAGE_SLOT = 2;

    // How much of an icon's height the label on it takes up.
    private const float ICON_LABEL_HEIGHT = 0.75f;

    /// <summary>
    /// A run of quads drawn in one call. Skin art, text and solid colours all share a run; only a quad
    /// showing a third custom image when the run already has two starts a new one.
    /// </summary>
    internal readonly record struct Run(int First, int Count, Texture? Image0, Texture? Image1);

    private static readonly UIRect Unclipped =
        new(new Vector2(float.NegativeInfinity), new Vector2(float.PositiveInfinity));

    private SpriteItem[] items = new SpriteItem[512];
    private int itemCount;

    private readonly List<Run> runs = [];
    private int runStart;
    private Texture? runImage0, runImage1;

    private readonly Stack<UIRect> clips = new();
    private UIRect clip = Unclipped;

    private Vector2 origin;
    private Vector2 scale = Vector2.One;

    // How components that are being animated are drawn: scaled and then moved, on top of where the layout put
    // them, and faded. Nested, each inside of the one before.
    private readonly Stack<(Vector2 Offset, Vector2 Scale, float Opacity)> visuals = new();
    private Vector2 visualOffset;
    private Vector2 visualScale = Vector2.One;
    private float opacity = 1.0f;

    /// <summary>The skin the frame is painted with.</summary>
    public UISkin Skin { get; private set; } = null!;

    internal ReadOnlySpan<SpriteItem> Items => items.AsSpan(0, itemCount);
    internal ReadOnlySpan<Run> Runs => CollectionsMarshal.AsSpan(runs);

    internal void Begin(UISkin skin)
    {
        Skin = skin;

        itemCount = 0;
        runs.Clear();
        runStart = 0;
        runImage0 = runImage1 = null;

        clips.Clear();
        clip = Unclipped;

        origin = Vector2.Zero;
        scale = Vector2.One;

        visuals.Clear();
        visualOffset = Vector2.Zero;
        visualScale = Vector2.One;
        opacity = 1.0f;
    }

    internal void End() => CloseRun();

    /// <summary>
    /// Everything painted until the matching <see cref="PopVisual"/> is drawn scaled around a point, moved
    /// and faded, without the layout knowing. This is how a component that is being animated shows up.
    /// Clips are cut out before any of it, so a clip moves along with what it cuts.
    /// </summary>
    internal void PushVisual(Vector2 offset, Vector2 scale, Vector2 pivot, float opacity)
    {
        visuals.Push((visualOffset, visualScale, this.opacity));

        // A point p ends up at (p - pivot) * scale + pivot + offset, inside of whatever is around it already.
        visualOffset += (pivot * (Vector2.One - scale) + offset) * visualScale;
        visualScale *= scale;
        this.opacity *= Math.Clamp(opacity, 0.0f, 1.0f);
    }

    internal void PopVisual() => (visualOffset, visualScale, opacity) = visuals.Pop();

    /// <summary>
    /// Everything painted from here on is scaled and then moved, which is how a module places itself.
    /// </summary>
    internal void SetTransform(Vector2 origin, Vector2 scale)
    {
        this.origin = origin;
        this.scale = scale;
    }

    /// <summary>
    /// Cuts everything painted until the matching <see cref="PopClip"/> down to a rectangle. Clips nest.
    /// </summary>
    public void PushClip(UIRect rect)
    {
        clips.Push(clip);
        clip = clip.Intersect(rect);
    }

    public void PopClip() => clip = clips.Pop();

    /// <summary>Fills a rectangle with a colour.</summary>
    public void Rect(UIRect rect, Vector4 color) =>
        Quad(rect, Vector2.Zero, Vector2.Zero, SpriteItem.PackColor(color), SpriteItem.NoTexture);

    /// <summary>Draws the edge of a rectangle, on the inside.</summary>
    public void Outline(UIRect rect, float thickness, Vector4 color)
    {
        uint packed = SpriteItem.PackColor(color);
        var (min, max) = rect;

        Quad(new UIRect(new Vector2(min.X, max.Y - thickness), max), Vector2.Zero, Vector2.Zero, packed, SpriteItem.NoTexture);
        Quad(new UIRect(min, new Vector2(max.X, min.Y + thickness)), Vector2.Zero, Vector2.Zero, packed, SpriteItem.NoTexture);
        Quad(new UIRect(new Vector2(min.X, min.Y + thickness), new Vector2(min.X + thickness, max.Y - thickness)), Vector2.Zero, Vector2.Zero, packed, SpriteItem.NoTexture);
        Quad(new UIRect(new Vector2(max.X - thickness, min.Y + thickness), new Vector2(max.X, max.Y - thickness)), Vector2.Zero, Vector2.Zero, packed, SpriteItem.NoTexture);
    }

    /// <summary>Draws a region of the skin stretched over a rectangle.</summary>
    public void Region(in UIRegion region, UIRect rect, Vector4 tint) =>
        Quad(rect, region.Position, region.Position + region.TexelSize, SpriteItem.PackColor(tint * RegionTint(region)), ATLAS_SLOT);

    /// <summary>
    /// Draws a region of the skin over a rectangle of any size without distorting its edges: the
    /// corners keep their size, the edges stretch along their length and only the middle stretches both ways.
    /// </summary>
    public void NineSlice(in UIRegion region, UIRect rect, Vector4 tint)
    {
        if (rect.IsEmpty)
            return;

        UIEdges border = region.Border;
        if (border == default)
        {
            Region(region, rect, tint);
            return;
        }

        // The border is measured in texels; on screen it is as big as the art is drawn.
        float left = border.Left * region.Scale, right = border.Right * region.Scale;
        float top = border.Top * region.Scale, bottom = border.Bottom * region.Scale;

        // A rectangle too small for its borders squeezes them rather than letting them cross over.
        float squeezeX = MathF.Min(1.0f, rect.Width / (left + right));
        float squeezeY = MathF.Min(1.0f, rect.Height / (top + bottom));

        Vector2 texMin = region.Position;
        Vector2 texMax = region.Position + region.TexelSize;

        // Left to right and top to bottom, on screen and in the texture.
        ReadOnlySpan<float> xs = [rect.Min.X, rect.Min.X + left * squeezeX, rect.Max.X - right * squeezeX, rect.Max.X];
        ReadOnlySpan<float> ys = [rect.Max.Y, rect.Max.Y - top * squeezeY, rect.Min.Y + bottom * squeezeY, rect.Min.Y];
        ReadOnlySpan<float> us = [texMin.X, texMin.X + border.Left, texMax.X - border.Right, texMax.X];
        ReadOnlySpan<float> vs = [texMin.Y, texMin.Y + border.Top, texMax.Y - border.Bottom, texMax.Y];

        uint packed = SpriteItem.PackColor(tint * RegionTint(region));
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                Quad(
                    new UIRect(new Vector2(xs[column], ys[row + 1]), new Vector2(xs[column + 1], ys[row])),
                    new Vector2(us[column], vs[row]),
                    new Vector2(us[column + 1], vs[row + 1]),
                    packed,
                    ATLAS_SLOT);
            }
        }
    }

    /// <summary>Draws a whole texture that isn't part of the skin, such as a portrait or an icon.</summary>
    public void Image(Texture texture, UIRect rect, Vector4 tint)
    {
        // Two custom images fit in a run next to the atlas and the font, a third means a new run.
        uint slot;
        if (texture == runImage0)
            slot = IMAGE_SLOT;
        else if (texture == runImage1)
            slot = IMAGE_SLOT + 1;
        else if (runImage0 is null)
        {
            runImage0 = texture;
            slot = IMAGE_SLOT;
        }
        else if (runImage1 is null)
        {
            runImage1 = texture;
            slot = IMAGE_SLOT + 1;
        }
        else
        {
            CloseRun();
            runImage0 = texture;
            slot = IMAGE_SLOT;
        }

        Quad(rect, Vector2.Zero, new Vector2(texture.Width, texture.Height), SpriteItem.PackColor(tint), slot);
    }

    /// <summary>
    /// Draws an icon over a rectangle: its art, and its label on top if it has one.
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

        // Glyphs that start on whole pixels stay sharp.
        position = new Vector2(MathF.Round(position.X), MathF.Round(position.Y));
        Vector2 pen = position;

        for (int i = 0; i < text.Length; i++)
        {
            char character = text[i];

            if (character == '\n')
            {
                pen = new Vector2(position.X, pen.Y - lineHeight);
                continue;
            }

            if (markup && character == '[' && font.TryReadIcon(text[i..], scale, out var name, out int length, out Vector2 size))
            {
                // Centred on the line. Art that hasn't made it into the atlas yet still takes up its room.
                if (Skin.TryGetIcon(name, out var icon))
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
                    glyph.Position + glyph.Size,
                    packed,
                    FONT_SLOT | SpriteItem.CoverageFlag);
            }

            pen.X += glyph.XAdvance * scale;
        }
    }

    /// <summary>
    /// Draws text lined up inside an area: <see cref="Origin.Center"/> centres it,
    /// <see cref="Origin.TopLeft"/> pushes it into the top left corner, and so on.
    /// </summary>
    public void Text(ReadOnlySpan<char> text, UIRect area, Origin align, float scale, Vector4 color, bool markup = true)
    {
        Vector2 size = Skin.Font.Measure(text, scale, markup);
        Vector2 center = area.PointAt(align) - align.ToVector() * size;

        Text(text, center + new Vector2(-0.5f, 0.5f) * size, scale, color, markup);
    }

    // A region that was made without a tint has none, rather than one that makes it invisible.
    private static Vector4 RegionTint(in UIRegion region) => region.Tint == default ? Vector4.One : region.Tint;

    // texTopLeft is the texel the top left corner of the rectangle shows, texBottomRight the bottom right.
    private void Quad(UIRect rect, Vector2 texTopLeft, Vector2 texBottomRight, uint color, uint flags)
    {
        UIRect visible = rect.Intersect(clip);
        if (visible.IsEmpty)
            return;

        // Everything is axis aligned, so clipping is just a matter of trimming the quad and showing
        // the matching part of the texture.
        if (visible != rect)
        {
            Vector2 texelsPerUnit = (texBottomRight - texTopLeft) / rect.Size;
            Vector2 trimmedTopLeft = new(visible.Min.X - rect.Min.X, rect.Max.Y - visible.Max.Y);
            Vector2 trimmedBottomRight = new(rect.Max.X - visible.Max.X, visible.Min.Y - rect.Min.Y);

            texTopLeft += trimmedTopLeft * texelsPerUnit;
            texBottomRight -= trimmedBottomRight * texelsPerUnit;
        }

        if (itemCount == items.Length)
            Array.Resize(ref items, items.Length * 2);

        if (opacity < 1.0f)
        {
            // Alpha is the top byte, see SpriteItem.PackColor.
            uint alpha = (uint)((color >> 24) * opacity + 0.5f);
            color = (color & 0x00FFFFFF) | alpha << 24;
        }

        // Art that is drawn at a size it wasn't made for has its texels blended at the edges, or pixel art
        // would shimmer its way through an animation. Text has a filter of its own.
        if (visualScale != Vector2.One && (flags & 0xFF) != SpriteItem.NoTexture && (flags & SpriteItem.CoverageFlag) == 0)
            flags |= SpriteItem.SmoothFlag;

        items[itemCount++] = SpriteItem.Rectangle(
            (visible.Min * visualScale + visualOffset) * scale + origin,
            (visible.Max * visualScale + visualOffset) * scale + origin,
            texTopLeft,
            texBottomRight,
            color,
            flags);
    }

    private void CloseRun()
    {
        int count = itemCount - runStart;
        if (count > 0)
            runs.Add(new Run(runStart, count, runImage0, runImage1));

        runStart = itemCount;
        runImage0 = runImage1 = null;
    }
}
