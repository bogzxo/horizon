using System.Numerics;

using Horizon.Rendering.UIX.Drawing;
using Horizon.Rendering.UIX.Skinning;

namespace Horizon.Rendering.UIX.Components;

/// <summary>
/// A window onto something taller than itself: its children are laid out at their full height from the top,
/// cut off at its edges, and moved up and down with the mouse wheel. A bar along the right shows how far down
/// it is, and can be dragged.
/// It has to be given a height, a scroll panel that is as tall as its content has nothing to scroll.
/// </summary>
public class ScrollPanel : Panel
{
    private const float BAR_WIDTH = 6.0f;
    private const float MIN_THUMB = 24.0f;

    private float offset;
    private float contentHeight;
    private bool draggingBar;

    /// <summary>How far down the content is scrolled, 0 being the top.</summary>
    public float Offset
    {
        get => offset;
        set => offset = Math.Clamp(value, 0.0f, MaxOffset);
    }

    /// <summary>How far one notch of the wheel moves the content.</summary>
    public float WheelStep { get; set; } = 48.0f;

    /// <summary>How far down the content can go: what of it doesn't fit.</summary>
    public float MaxOffset => MathF.Max(0.0f, contentHeight - Bounds.Shrink(Padding).Height);

    /// <summary>Scrolls all the way down, for content that grows at its end (a log).</summary>
    public void ScrollToEnd() => offset = float.MaxValue;

    protected override bool HitTestVisible => true;

    protected override bool ClipsChildren => true;

    protected override Vector2 Measure(UISkin skin)
    {
        // As wide as the widest child (and the bar), the height is whoever uses it to say.
        float width = 0.0f;
        foreach (var child in ChildSpan)
        {
            if (child.Visible)
                width = MathF.Max(width, child.DesiredSize.X);
        }

        return new Vector2(width + BAR_WIDTH, 0.0f) + Padding.Total;
    }

    protected override void Arrange(UIRect content)
    {
        contentHeight = 0.0f;
        foreach (var child in ChildSpan)
        {
            if (child.Visible)
                contentHeight = MathF.Max(contentHeight, child.DesiredSize.Y);
        }

        // The content can have shrunk under us, or the panel grown.
        offset = Math.Clamp(offset, 0.0f, MathF.Max(0.0f, contentHeight - content.Height));

        float width = content.Width - BAR_WIDTH;
        foreach (var child in ChildSpan)
        {
            // Every child gets the whole width and its own height, starting at the top less the offset.
            Vector2 size = new(width, MathF.Max(child.DesiredSize.Y, 0.0f));
            Vector2 topLeft = new(content.Min.X, content.Max.Y + offset);

            child.ArrangeTree(new UIRect(new Vector2(topLeft.X, topLeft.Y - size.Y), new Vector2(topLeft.X + size.X, topLeft.Y)));
        }
    }

    protected override void PaintChildren(UIDrawList list)
    {
        UIRect content = Bounds.Shrink(Padding);

        list.PushClip(content);
        base.PaintChildren(list);
        list.PopClip();

        if (MaxOffset <= 0.0f)
            return;

        // The bar is as long compared to its track as the panel is compared to its content.
        UIRect track = new(new Vector2(content.Max.X - BAR_WIDTH, content.Min.Y), content.Max);
        float length = MathF.Max(MIN_THUMB, track.Height * content.Height / contentHeight);
        float top = track.Max.Y - (track.Height - length) * (offset / MaxOffset);

        list.Rect(track, list.Skin.HoverColor);
        list.Rect(
            new UIRect(new Vector2(track.Min.X, top - length), new Vector2(track.Max.X, top)),
            draggingBar ? list.Skin.HighlightColor : list.Skin.AccentColor);
    }

    protected internal override bool OnScroll(float delta)
    {
        if (MaxOffset <= 0.0f)
            return false;

        Offset -= delta * WheelStep;
        return true;
    }

    protected internal override void OnPointerDown(Vector2 point)
    {
        UIRect content = Bounds.Shrink(Padding);
        draggingBar = MaxOffset > 0.0f && point.X >= content.Max.X - BAR_WIDTH * 2.0f;

        if (draggingBar)
            OnPointerDrag(point);
    }

    protected internal override void OnPointerDrag(Vector2 point)
    {
        if (!draggingBar)
            return;

        // The middle of the bar follows the pointer along the track.
        UIRect content = Bounds.Shrink(Padding);
        float length = MathF.Max(MIN_THUMB, content.Height * content.Height / contentHeight);
        float travel = content.Height - length;

        if (travel > 0.0f)
            Offset = (content.Max.Y - length * 0.5f - point.Y) / travel * MaxOffset;
    }

    protected internal override void OnPointerUp(Vector2 point) => draggingBar = false;

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("offset", () => Offset, value => Offset = value);
        Expose("wheel_step", () => WheelStep, value => WheelStep = value);
    }
}
