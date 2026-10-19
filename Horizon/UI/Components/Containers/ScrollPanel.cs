using System.Numerics;

using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// A window onto something taller than itself. Its children are laid out at their full height from the top,
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
    private UIScrollEase ease;

    // Where on the thumb it was taken hold of, from its top
    private float barGrab;

    /// <summary>How far down the content is scrolled, 0 being the top.</summary>
    public float Offset
    {
        get => offset;
        set
        {
            // Said by somebody, not eased to by the wheel
            ease.Stop();
            offset = Math.Clamp(value, 0.0f, MaxOffset);
        }
    }

    /// <summary>How far one notch of the wheel moves the content.</summary>
    public float WheelStep { get; set; } = 48.0f;

    /// <summary>
    /// How much room is kept between the bar and what is in the panel, so a row doesn't run right up against it.
    /// Kept whether the bar is there or not, or everything would jump sideways when the content outgrows the panel.
    /// </summary>
    public float BarGap { get; set; } = 6.0f;

    /// <summary>How far down the content can go, which is what of it doesn't fit.</summary>
    public float MaxOffset => MathF.Max(0.0f, contentHeight - Bounds.Shrink(Padding).Height);

    /// <summary>Scrolls all the way down, for content that grows at its end (a log).</summary>
    public void ScrollToEnd() => offset = float.MaxValue;

    /// <summary>
    /// Scrolls just far enough for a component inside the panel to be in view, by where it was last laid out. Nothing
    /// if it is in view already. For a list that is walked with a gamepad, or an editor showing what was picked.
    /// </summary>
    public void ScrollIntoView(UIComponent component)
    {
        UIRect content = Bounds.Shrink(Padding);
        UIRect target = component.Bounds;

        if (content.IsEmpty || target.IsEmpty)
            return;

        // Up for something above the window, down for something below it. Something taller than the window shows its top
        if (target.Max.Y > content.Max.Y)
            Offset -= target.Max.Y - content.Max.Y;
        else if (target.Min.Y < content.Min.Y)
            Offset += MathF.Min(content.Min.Y - target.Min.Y, content.Max.Y - target.Max.Y);
    }

    /// <summary>
    /// Scrolls a component inside the panel to the top of what's in view, or as near as the end of the content lets it,
    /// for a section gone to by its name (where <see cref="ScrollIntoView"/> stops as soon as it shows, at the bottom).
    /// </summary>
    public void ScrollToTop(UIComponent component)
    {
        UIRect content = Bounds.Shrink(Padding);
        if (content.IsEmpty || component.Bounds.IsEmpty)
            return;

        Offset = Math.Clamp(Offset + (content.Max.Y - component.Bounds.Max.Y), 0.0f, MaxOffset);
    }

    protected override void Update(float dt)
    {
        base.Update(dt);
        ease.Clamp(MaxOffset);
        if (ease.Goal is not null)
            offset = Math.Clamp(ease.Step(offset, dt), 0.0f, MaxOffset);
    }

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

        return new Vector2(width + BAR_WIDTH + BarGap, 0.0f) + Padding.Total;
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

        float width = MathF.Max(0.0f, content.Width - BAR_WIDTH - BarGap);
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

        // Pills, for a skin that has round corners at all
        float round = list.Skin.CornerRadius > 0.0f ? BAR_WIDTH * 0.5f : 0.0f;

        list.RoundRect(track, round, list.Skin.HoverColor);
        list.RoundRect(
            new UIRect(new Vector2(track.Min.X, top - length), new Vector2(track.Max.X, top)),
            round,
            draggingBar ? list.Skin.HighlightColor : list.Skin.AccentColor);
    }

    protected internal override bool OnScroll(float delta)
    {
        if (MaxOffset <= 0.0f)
            return false;

        ease.Wheel(offset, -delta * WheelStep, MaxOffset);
        return true;
    }

    protected internal override void OnPointerDown(Vector2 point)
    {
        UIRect content = Bounds.Shrink(Padding);
        draggingBar = MaxOffset > 0.0f && point.X >= content.Max.X - BAR_WIDTH * 2.0f;

        if (draggingBar)
        {
            // Taken by the thumb it keeps where it was taken, anywhere else on the track its middle goes to the pointer
            float length = MathF.Max(MIN_THUMB, content.Height * content.Height / contentHeight);
            float top = content.Max.Y - (content.Height - length) * (offset / MaxOffset);
            barGrab = point.Y <= top && point.Y >= top - length ? top - point.Y : length * 0.5f;
            OnPointerDrag(point);
        }
    }

    protected internal override void OnPointerDrag(Vector2 point)
    {
        if (!draggingBar)
            return;

        // The thumb follows the pointer along the track, held where it was taken hold of
        UIRect content = Bounds.Shrink(Padding);
        float length = MathF.Max(MIN_THUMB, content.Height * content.Height / contentHeight);
        float travel = content.Height - length;

        if (travel > 0.0f)
            Offset = (content.Max.Y - (point.Y + barGrab)) / travel * MaxOffset;
    }

    protected internal override void OnPointerUp(Vector2 point) => draggingBar = false;

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("offset", () => Offset, value => Offset = value);
        Expose("wheel_step", () => WheelStep, value => WheelStep = value);
        Expose("bar_gap", () => BarGap, value => BarGap = value);
    }
}
