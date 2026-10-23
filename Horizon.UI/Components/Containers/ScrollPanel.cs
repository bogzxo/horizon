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

    /// <summary>
    /// How far in from its top and bottom what's in it fades out while there's more of it past that edge, zero for a
    /// hard edge. The fade grows in as it's scrolled off the end, so nothing jumps.
    /// </summary>
    public float Fade { get; set; } = 18.0f;

    /// <summary>How far down the content can go, which is what of it doesn't fit.</summary>
    public float MaxOffset => MathF.Max(0.0f, contentHeight - Bounds.Shrink(Padding).Height);

    /// <summary>Scrolls all the way down, for content that grows at its end (a log).</summary>
    public void ScrollToEnd() => offset = float.MaxValue;

    /// <summary>
    /// Whether a panel scrolled to its end stays there as what's in it grows, a log's. Scrolled up it stays where it was
    /// put. The newest lines of the output went in under the bottom of it, the end was the one before they were laid out.
    /// </summary>
    public bool StickToEnd { get; set; }

    // How far down it could go the last time it was laid out, what at the end means while it sticks there
    private float laidMax;

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

        // The content can have shrunk under us, or the panel grown. At its end it stays there, when it sticks to it
        float max = MathF.Max(0.0f, contentHeight - content.Height);
        bool atEnd = StickToEnd && offset >= laidMax - 0.5f;
        offset = atEnd ? max : Math.Clamp(offset, 0.0f, max);
        laidMax = max;

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

        // Soft at an edge there's more past, as much of the fade as there's room scrolled
        float fade = MathF.Min(Fade, content.Height * 0.25f);
        list.PushClip(content);
        list.PushFade(content, MathF.Min(fade, offset), MathF.Min(fade, MaxOffset - offset));
        base.PaintChildren(list);
        list.PopFade();
        list.PopClip();

        // No bar on a panel folded too short for one, its thumb is never shorter than its least and stuck out of it
        if (MaxOffset <= 0.0f || content.Height < MIN_THUMB * 1.5f)
            return;

        // The bar is as long compared to its track as the panel is compared to its content.
        UIRect track = new(new Vector2(content.Max.X - BAR_WIDTH, content.Min.Y), content.Max);
        float length = MathF.Max(MIN_THUMB, track.Height * content.Height / contentHeight);
        float top = track.Max.Y - (track.Height - length) * (offset / MaxOffset);

        // Pills, for a skin that has round corners at all
        float round = list.Skin.CornerRadius > 0.0f ? BAR_WIDTH * 0.5f : 0.0f;

        // No track to speak of and a thumb in the text's colour seen through, brighter with the pointer on the bar and
        // while it's dragged. The thumb was the accent, a bar of gold down the side of every list that meant nothing, and
        // the track is painted see-through rather than left out so the quads don't shift as it lights. It lit for the
        // pointer anywhere on the panel that wasn't on a row of it, so a pointer going down a list had it blinking
        Vector2 pointer = Module is { } module ? module.ToLocal(module.Compositor.Pointer.Position) : new Vector2(float.NaN);
        bool overBar = pointer.X >= track.Min.X - BAR_WIDTH && pointer.X <= Bounds.Max.X && pointer.Y >= content.Min.Y && pointer.Y <= content.Max.Y
            && PointerWithin();
        Vector4 text = list.Skin.TextColor;
        list.RoundRect(track, round, text with { W = draggingBar || overBar ? 0.05f : 0.0f });
        list.RoundRect(
            new UIRect(new Vector2(track.Min.X, top - length), new Vector2(track.Max.X, top)),
            round,
            text with { W = draggingBar ? 0.5f : overBar ? 0.32f : 0.2f });
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
        Expose("fade", () => Fade, value => Fade = MathF.Max(0.0f, value));
    }
}
