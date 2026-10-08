using System.Numerics;
using System.Runtime.InteropServices;

using Horizon.Graphics;
using Horizon.Rendering.Spriting;
using Horizon.UI.Skinning;

namespace Horizon.UI.Drawing;

/// <summary>
/// Everything the UI wants on screen this frame, as quads in painter's order: what is added later is
/// drawn on top. Components paint into it on the simulation thread and the sprite renderer draws the result
/// (the quads are its <see cref="SpriteItem"/>s), the whole UI in a single call unless more than two
/// custom images are involved.
/// </summary>
public sealed partial class UIDrawList
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

    // The atlases of other people this list drew out of (or wanted to), for the renderer to bring up to date
    private readonly List<TextureAtlas> atlases = [];

    private readonly Stack<UIRect> clips = new();
    private UIRect clip = Unclipped;

    private Vector2 origin;
    private Vector2 scale = Vector2.One;

    // How components that are being animated are drawn. Scaled and then moved, on top of where the layout put
    // them, and faded. Nested, each inside of the one before.
    private readonly Stack<(Vector2 Offset, Vector2 Scale, float Opacity)> visuals = new();
    private Vector2 visualOffset;
    private Vector2 visualScale = Vector2.One;
    private float opacity = 1.0f;

    // How fast what is being painted is going across the screen, for the effects that blur what moves. At a point p
    // (as the camera sees it) that is motionBase + motionSlope * p. Each component says so for what it paints, see
    // PushMotion.
    private readonly Stack<(Vector2 Base, Vector2 Slope)> motions = new();
    private Vector2 motionBase, motionSlope;
    private int frame;
    private float deltaTime;

    // How long the UI has been drawn for, for the icons that animate. Starts over now and then so it never grows coarse
    private float time;
    private const float TIME_WRAP = 3600.0f;

    /// <summary>The skin the frame is painted with.</summary>
    public UISkin Skin { get; private set; } = null!;

    /// <summary>
    /// Whether the quads are told how fast they are going. Only when something is going to use it. Working it out
    /// takes every component a little memory and a little time.
    /// </summary>
    internal bool TracksMotion { get; private set; }

    /// <summary>Whether anything that was painted this frame is going anywhere, as far as <see cref="TracksMotion"/> can tell.</summary>
    internal bool Moving { get; private set; }

    internal ReadOnlySpan<SpriteItem> Items => items.AsSpan(0, itemCount);
    internal ReadOnlySpan<Run> Runs => CollectionsMarshal.AsSpan(runs);
    internal ReadOnlySpan<TextureAtlas> Atlases => CollectionsMarshal.AsSpan(atlases);

    /// <param name="frame">Which update this is, counting up by one for as long as the UI is painted without a break.</param>
    /// <param name="dt">How long the update is, in seconds.</param>
    /// <param name="tracksMotion">See <see cref="TracksMotion"/>.</param>
    internal void Begin(UISkin skin, int frame = 0, float dt = 0.0f, bool tracksMotion = false)
    {
        Skin = skin;

        this.frame = frame;
        deltaTime = dt;
        time = (time + dt) % TIME_WRAP;
        TracksMotion = tracksMotion;
        Moving = false;

        motions.Clear();
        motionBase = motionSlope = Vector2.Zero;

        itemCount = 0;
        runs.Clear();
        runStart = 0;
        runImage0 = runImage1 = null;
        atlases.Clear();

        clips.Clear();
        clip = Unclipped;

        origin = Vector2.Zero;
        scale = Vector2.One;

        visuals.Clear();
        visualOffset = Vector2.Zero;
        visualScale = Vector2.One;
        opacity = 1.0f;
    }

    internal void End()
    {
        CloseRun();

        // Art that was asked for while painting (or just before, by another UI with the same skin) isn't in
        // the atlas until the next frame is drawn, so this list may well show less than it should
        Incomplete = Skin.HasPendingArt;
    }

    /// <summary>
    /// Whether another list draws exactly what this one does. The same quads in the same order, showing the same
    /// textures. Two updates of a UI that nothing happened in paint lists that are, and the second one needn't be drawn.
    /// </summary>
    internal bool SameAs(UIDrawList other)
    {
        if (itemCount != other.itemCount || Skin != other.Skin || Incomplete != other.Incomplete || Moving != other.Moving)
            return false;

        if (!CollectionsMarshal.AsSpan(runs).SequenceEqual(CollectionsMarshal.AsSpan(other.runs)))
            return false;

        // Byte for byte, an item is nothing but numbers
        return MemoryMarshal.AsBytes(items.AsSpan(0, itemCount)).SequenceEqual(MemoryMarshal.AsBytes(other.items.AsSpan(0, itemCount)));
    }

    /// <summary>
    /// Whether the list was painted while art of its skin was still on its way into the atlas, and so without it.
    /// </summary>
    internal bool Incomplete { get; private set; }

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
    /// Everything painted until the matching <see cref="PopMotion"/> is going as fast as a component is, which is
    /// worked out here from where its bounds end up being drawn this update and where they were the last.
    /// Both of its corners are followed, so what is painted near one of them goes as fast as that corner does:
    /// the far end of something that is growing moves, the end it grows from doesn't.
    /// </summary>
    /// <param name="motion">What the component remembers of where it was.</param>
    /// <param name="bounds">Where the layout put the component.</param>
    internal void PushMotion(UIMotion motion, UIRect bounds)
    {
        Vector2 min = (bounds.Min * visualScale + visualOffset) * scale + origin;
        Vector2 max = (bounds.Max * visualScale + visualOffset) * scale + origin;

        motion.Track(min, max, bounds.Size, frame, deltaTime);
        motions.Push((motionBase, motionSlope));

        // From one corner to the other the speed changes evenly. Something without a size goes at one speed
        Vector2 size = max - min;
        Vector2 spread = motion.Max - motion.Min;

        motionSlope = new Vector2(
            size.X != 0.0f ? spread.X / size.X : 0.0f,
            size.Y != 0.0f ? spread.Y / size.Y : 0.0f);
        motionBase = motion.Min - motionSlope * min;
    }

    internal void PopMotion() => (motionBase, motionSlope) = motions.Pop();

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

    /// <summary>
    /// Fills a rectangle with rounded corners. The corners are worked out for every pixel as they are drawn,
    /// so they are as crisp at any size as the screen allows and need no art.
    /// </summary>
    /// <param name="radius">How big the corners are. No more than half of the shorter side, which makes a pill of it.</param>
    public void RoundRect(UIRect rect, float radius, Vector4 color)
    {
        radius = MathF.Min(radius, MathF.Min(rect.Width, rect.Height) * 0.5f);
        if (radius < 0.5f)
        {
            Rect(rect, color);
            return;
        }

        uint packed = SpriteItem.PackColor(color);
        var (min, max) = rect;

        // A column down the middle and a strip either side of it between the corners
        Quad(new UIRect(new Vector2(min.X + radius, min.Y), new Vector2(max.X - radius, max.Y)), Vector2.Zero, Vector2.Zero, packed, SpriteItem.NoTexture);
        Quad(new UIRect(new Vector2(min.X, min.Y + radius), new Vector2(min.X + radius, max.Y - radius)), Vector2.Zero, Vector2.Zero, packed, SpriteItem.NoTexture);
        Quad(new UIRect(new Vector2(max.X - radius, min.Y + radius), new Vector2(max.X, max.Y - radius)), Vector2.Zero, Vector2.Zero, packed, SpriteItem.NoTexture);

        Corners(rect, radius, 1.0f, packed);
    }

    /// <summary>Draws the edge of a rectangle with rounded corners, on the inside. See <see cref="RoundRect"/>.</summary>
    public void RoundOutline(UIRect rect, float radius, float thickness, Vector4 color)
    {
        radius = MathF.Min(radius, MathF.Min(rect.Width, rect.Height) * 0.5f);
        if (radius < 0.5f)
        {
            Outline(rect, thickness, color);
            return;
        }

        thickness = MathF.Min(thickness, radius);

        uint packed = SpriteItem.PackColor(color);
        var (min, max) = rect;

        Quad(new UIRect(new Vector2(min.X + radius, max.Y - thickness), new Vector2(max.X - radius, max.Y)), Vector2.Zero, Vector2.Zero, packed, SpriteItem.NoTexture);
        Quad(new UIRect(new Vector2(min.X + radius, min.Y), new Vector2(max.X - radius, min.Y + thickness)), Vector2.Zero, Vector2.Zero, packed, SpriteItem.NoTexture);
        Quad(new UIRect(new Vector2(min.X, min.Y + radius), new Vector2(min.X + thickness, max.Y - radius)), Vector2.Zero, Vector2.Zero, packed, SpriteItem.NoTexture);
        Quad(new UIRect(new Vector2(max.X - thickness, min.Y + radius), new Vector2(max.X, max.Y - radius)), Vector2.Zero, Vector2.Zero, packed, SpriteItem.NoTexture);

        Corners(rect, radius, thickness / radius, packed);
    }

    /// <summary>
    /// Fills a rectangle the way the skin has its flat controls, which is with rounded corners if it says so (see <see cref="UISkin.CornerRadius"/>).
    /// What a component draws itself with when the skin has no art for it.
    /// </summary>
    public void Box(UIRect rect, Vector4 color) => RoundRect(rect, Skin.CornerRadius, color);

    /// <summary>Draws the edge of what <see cref="Box"/> fills.</summary>
    public void Frame(UIRect rect, float thickness, Vector4 color) => RoundOutline(rect, Skin.CornerRadius, thickness, color);

    /// <summary>
    /// Helper method to draw the four corners of a rounded rectangle, each a quarter of a disc with its middle
    /// at the corner of the quad that points into the rectangle.
    /// </summary>
    /// <param name="ring">How much of the discs is filled from their edge inwards, see <see cref="SpriteItem.Ring"/>.</param>
    private void Corners(UIRect rect, float radius, float ring, uint color)
    {
        var (min, max) = rect;
        Vector2 size = new(radius);
        uint flags = SpriteItem.NoTexture | SpriteItem.CornerFlag;

        // What the top left and the bottom right of each quad are told is how far they are from the middle of their disc
        Quad(new UIRect(new Vector2(min.X, max.Y - radius), new Vector2(min.X + radius, max.Y)), Vector2.One, Vector2.Zero, color, flags, ring);
        Quad(new UIRect(max - size, max), Vector2.UnitY, Vector2.UnitX, color, flags, ring);
        Quad(new UIRect(min, min + size), Vector2.UnitX, Vector2.UnitY, color, flags, ring);
        Quad(new UIRect(new Vector2(max.X - radius, min.Y), new Vector2(max.X, min.Y + radius)), Vector2.Zero, Vector2.One, color, flags, ring);
    }

    /// <summary>Draws a region of the skin stretched over a rectangle.</summary>
    public void Region(in UIRegion region, UIRect rect, Vector4 tint) =>
        Quad(rect, region.Position, region.Position + region.TexelSize, SpriteItem.PackColor(tint * RegionTint(region)), ATLAS_SLOT);

    /// <summary>
    /// Draws a region of the skin over a rectangle of any size without distorting its edges. The
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
    public void Image(Texture texture, UIRect rect, Vector4 tint) =>
        Image(texture, rect, Vector2.Zero, new Vector2(texture.Width, texture.Height), tint);

    /// <summary>
    /// A region of somebody else's atlas (a character's, say), by the key it was asked for under. Nothing is drawn
    /// until the atlas has it, which the renderer sees to the next time this is drawn. Mirrored flips it left to right.
    /// </summary>
    public void Image(TextureAtlas atlas, string key, UIRect rect, Vector4 tint, bool mirrored = false)
    {
        if (!atlases.Contains(atlas)) atlases.Add(atlas);

        if (!atlas.TryGet(key, out var region) || atlas.Texture.Handle == 0) return;

        // An atlas that trims kept part of the frame, and the rectangle is the whole frame: the part goes where it was
        if (region.Trimmed)
        {
            Vector2 from = region.Offset / region.FrameSize, to = (region.Offset + region.Size) / region.FrameSize;
            if (mirrored) (from.X, to.X) = (1.0f - to.X, 1.0f - from.X);
            rect = new UIRect(rect.Min + rect.Size * from, rect.Min + rect.Size * to);
        }

        Vector2 topLeft = region.Position, bottomRight = region.Position + region.Size;
        if (mirrored) (topLeft.X, bottomRight.X) = (bottomRight.X, topLeft.X);

        Image(atlas.Texture, rect, topLeft, bottomRight, tint);
    }

    /// <summary>
    /// Draws a part of a texture that isn't part of the skin, such as one sprite out of a sheet.
    /// </summary>
    /// <param name="texTopLeft">The top left corner of the part, in pixels from the top left of the texture.</param>
    /// <param name="texBottomRight">Its bottom right corner.</param>
    public void Image(Texture texture, UIRect rect, Vector2 texTopLeft, Vector2 texBottomRight, Vector4 tint)
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

        Quad(rect, texTopLeft, texBottomRight, SpriteItem.PackColor(tint), slot);
    }

    // A region that was made without a tint has none, rather than one that makes it invisible.
    private static Vector4 RegionTint(in UIRegion region) => region.Tint == default ? Vector4.One : region.Tint;

    // texTopLeft is the texel the top left corner of the rectangle shows, texBottomRight the bottom right.
    private void Quad(UIRect rect, Vector2 texTopLeft, Vector2 texBottomRight, uint color, uint flags, float ring = 0.0f)
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
        // would shimmer its way through an animation and come out uneven in a UI that is scaled to its window.
        // Text has a filter of its own.
        Vector2 drawn = visualScale * scale;
        bool whole = drawn.X == MathF.Round(drawn.X) && drawn.Y == MathF.Round(drawn.Y);
        if (!whole && (flags & 0xFF) != SpriteItem.NoTexture && (flags & SpriteItem.CoverageFlag) == 0)
            flags |= SpriteItem.SmoothFlag;

        Vector2 min = (visible.Min * visualScale + visualOffset) * scale + origin;
        Vector2 max = (visible.Max * visualScale + visualOffset) * scale + origin;

        ref SpriteItem item = ref items[itemCount++];
        item = SpriteItem.Rectangle(min, max, texTopLeft, texBottomRight, color, flags);
        item.Ring = ring;

        // A quad goes at one speed all over, the one of its middle. They are small enough for that. A panel is
        // nine of them and text one a letter
        if (TracksMotion)
        {
            item.Motion = motionBase + motionSlope * ((min + max) * 0.5f);
            Moving |= item.Motion != Vector2.Zero;
        }
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
