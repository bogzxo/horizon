using System.Numerics;

using Horizon.UI.Drawing;

namespace Horizon.UI.Components;

// The bar down the right of a scroll panel, how it's drawn and how it's dragged.
public partial class ScrollPanel
{
    // The bar's thickness and its thumb's least length as they come, a bar made thicker has a longer least thumb
    private const float BAR_WIDTH = 6.0f;
    private const float MIN_THUMB = 24.0f;

    private bool draggingBar;

    // Where on the thumb it was taken hold of, down from its top
    private float barGrab;

    /// <summary>
    /// How thick the bar is at rest, in the panel's units. It grows a little under the pointer. A page drawn bigger than
    /// the skin (the settings zoomed on a big screen) makes it thicker with the rest, a thin bar on a 4K screen is a needle.
    /// </summary>
    public float BarWidth { get; set; } = BAR_WIDTH;

    /// <summary>Whether the thumb is being dragged right now.</summary>
    public bool IsDraggingBar => draggingBar;

    private float MinThumb => MIN_THUMB * BarWidth / BAR_WIDTH;

    // How thick it's drawn under the pointer or held, into the gap before it and never past that
    private float LitWidth => MathF.Min(BarWidth * 1.5f, BarWidth + BarGap);

    // Whether there's a bar at all. None on a panel with nothing to scroll or folded too short for one, its thumb is never
    // shorter than its least and would stick out of it
    private bool HasBar(UIRect content) => MaxOffset > 0.0f && content.Height >= MinThumb * 1.5f && contentHeight > 0.0f;

    /// <summary>The track the thumb runs along, in the module's units, empty while there's no bar.</summary>
    public UIRect TrackBounds
    {
        get
        {
            UIRect content = Bounds.Shrink(Padding);
            return HasBar(content) ? new UIRect(new Vector2(content.Max.X - BarWidth, content.Min.Y), content.Max) : default;
        }
    }

    /// <summary>The thumb as it is now, in the module's units, empty while there's no bar.</summary>
    public UIRect ThumbBounds
    {
        get
        {
            UIRect content = Bounds.Shrink(Padding);
            if (!HasBar(content))
                return default;

            var (top, length) = Thumb(content);
            return new UIRect(new Vector2(content.Max.X - BarWidth, top - length), new Vector2(content.Max.X, top));
        }
    }

    // Where the top of the thumb is and how long it is. The thumb is as long compared to its track as the panel is
    // compared to its content
    private (float Top, float Length) Thumb(UIRect content)
    {
        float length = MathF.Min(content.Height, MathF.Max(MinThumb, content.Height * content.Height / contentHeight));
        float top = content.Max.Y - (content.Height - length) * (MaxOffset > 0.0f ? offset / MaxOffset : 0.0f);
        return (top, length);
    }

    // Where a press takes hold of the bar. The bar, the gap before it (nothing in the panel goes there) and whatever
    // padding is past it, so it isn't a few pixels to hit square on
    private UIRect GrabZone(UIRect content) =>
        new(new Vector2(content.Max.X - BarWidth - BarGap, content.Min.Y), new Vector2(Bounds.Max.X, content.Max.Y));

    private void PaintBar(UIDrawList list, UIRect content)
    {
        if (!HasBar(content))
            return;

        // No track to speak of and a thumb in the text's colour seen through, brighter and a bit thicker with the pointer on
        // the bar and while it's dragged. The thumb was the accent, a bar of gold down the side of every list that meant
        // nothing, and the track is painted see-through rather than left out so the quads don't shift as it lights. It lit
        // for the pointer anywhere on the panel that wasn't on a row of it, so a pointer going down a list had it blinking
        Vector2 pointer = Module is { } module ? module.ToLocal(module.Compositor.Pointer.Position) : new Vector2(float.NaN);
        bool overBar = GrabZone(content).Contains(pointer) && PointerWithin();
        bool lit = draggingBar || overBar;

        float width = lit ? LitWidth : BarWidth;
        UIRect track = new(new Vector2(content.Max.X - width, content.Min.Y), content.Max);
        var (top, length) = Thumb(content);

        // Pills, for a skin that has round corners at all
        float round = list.Skin.CornerRadius > 0.0f ? width * 0.5f : 0.0f;

        Vector4 text = list.Skin.TextColor;
        list.RoundRect(track, round, text with { W = lit ? 0.05f : 0.0f });
        list.RoundRect(
            new UIRect(new Vector2(track.Min.X, top - length), new Vector2(track.Max.X, top)),
            round,
            text with { W = draggingBar ? 0.5f : overBar ? 0.32f : 0.2f });
    }

    protected internal override void OnPointerDown(Vector2 point)
    {
        UIRect content = Bounds.Shrink(Padding);
        draggingBar = HasBar(content) && GrabZone(content).Contains(point);

        if (!draggingBar)
            return;

        // Taken by the thumb it keeps where it was taken. Anywhere else on the track the thumb jumps there, its middle
        // under the pointer, and is held by its middle from then on
        var (top, length) = Thumb(content);
        barGrab = point.Y <= top && point.Y >= top - length ? top - point.Y : length * 0.5f;
        OnPointerDrag(point);
    }

    protected internal override void OnPointerDrag(Vector2 point)
    {
        if (!draggingBar)
            return;

        // The thumb follows the pointer along the track, held where it was taken hold of
        UIRect content = Bounds.Shrink(Padding);
        var (_, length) = Thumb(content);
        float travel = content.Height - length;

        if (travel > 0.0f)
            Offset = (content.Max.Y - (point.Y + barGrab)) / travel * MaxOffset;
    }

    protected internal override void OnPointerUp(Vector2 point) => draggingBar = false;
}
