using Horizon.Content;
using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// A stack with a title bar over it that folds the stack away and back out again, for tidying up a long panel of
/// settings into the parts somebody is looking at. Click the bar (or confirm on it with a pad) to fold it, the
/// little arrow says which way it is. Folded it is as tall as its bar and what is in it is neither drawn nor hit,
/// and it folds smoothly over <see cref="CollapseTime"/> rather than snapping, the rest of the layout coming along.
/// Everything a <see cref="StackPanel"/> does it does, below the bar, and its padding is round what is below the bar.
/// <code>
/// let audio = compositor.collapsible({ title: "Audio", expanded: false, spacing: 6 });
/// let volume = compositor.slider({ parent: audio });
/// </code>
/// </summary>
public class Collapsible : StackPanel
{
    // The bar's art when the skin has some, and the arrow, how much of the bar it takes and how many steps it is drawn in
    private string style = "collapsible";
    private string hoverStyle = "collapsible_hover";
    private const float ARROW_SHARE = 0.32f;
    private const int ARROW_STEPS = 5;
    private const int BAR_PART = 0, RIM_PART = 1, TITLE_PART = 2;

    private bool expanded = true;
    private IRuntimeValue? toggledHandler;

    // How far open it is, 0 folded to 1 open, and whether it has had an update to settle that from yet
    private float openness = 1.0f;
    private bool settled;

    // What is below the bar at its full height, padding and all, from the last measure
    private float contentHeight;

    // Whether the press that is down started on the bar
    private bool pressedOnHeader;

    /// <summary>What the bar says.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Whether what is below the bar is showing. Changing it folds it away or out over <see cref="CollapseTime"/>.</summary>
    public bool Expanded
    {
        get => expanded;
        set
        {
            if (expanded == value)
                return;

            expanded = value;

            // A layout that says so before anything has been drawn starts that way, not folding away in front of everybody
            if (!settled)
                openness = value ? 1.0f : 0.0f;

            OnToggled?.Invoke(value);
            InvokeScript(toggledHandler, new BooleanValue(value));
        }
    }

    /// <summary>How many seconds it takes to fold away or out, 0 to snap.</summary>
    public float CollapseTime { get; set; } = 0.18f;

    /// <summary>How tall the bar is, 0 for as tall as a line of text needs.</summary>
    public float HeaderHeight { get; set; }

    /// <summary>How big the title is written, 0 for the skin's text size.</summary>
    public float TextScale { get; set; }

    /// <summary>The colour of the title and the arrow, null for the skin's.</summary>
    public Vector4? TitleColor { get; set; }

    /// <summary>
    /// Which of the skin's art the bar is drawn with, the one named after it with "_hover" on the end under the pointer.
    /// A skin with none of it gets a flat bar in its control colour.
    /// </summary>
    public string Style
    {
        get => style;
        set
        {
            style = StyleStem(value, "_hover");
            hoverStyle = style + "_hover";
        }
    }

    /// <summary>Called with whether it is open now, every time it is folded or opened, from code or by a click.</summary>
    public Action<bool>? OnToggled { get; set; }

    /// <summary>How far open it is right now, 0 folded away and 1 all the way out, in between while it moves.</summary>
    public float Openness => openness;

    public Collapsible()
    { }

    public Collapsible(string title)
    {
        Title = title;
    }

    /// <summary>Folds it away if it is open and opens it if it isn't.</summary>
    public void Toggle() => Expanded = !Expanded;

    protected override bool HitTestVisible => true;

    // Cut off at the bottom while it is folding, and with nothing below the bar to hit while it is folded
    protected override bool ClipsChildren => openness < 1.0f;

    protected internal override bool ShowsChild(UIComponent child) => openness > 0.0f;

    protected internal override bool Navigable => true;

    protected internal override void OnActivate() => Toggle();

    // Right opens it and left folds it, the way a tree does, and either one that changes nothing moves on
    protected internal override bool OnAdjust(int step)
    {
        if ((step > 0) == expanded)
            return false;

        Expanded = step > 0;
        return true;
    }

    private float TextScaleOf(UISkin skin) => TextScale > 0.0f ? TextScale : skin.TextScale;

    private float HeaderOf(UISkin skin) => HeaderHeight > 0.0f ? HeaderHeight : MathF.Round(skin.Font.LineHeight * TextScaleOf(skin) * 1.6f);

    /// <summary>Where the bar is, in the layout's units.</summary>
    public UIRect HeaderBounds => Module?.Compositor.Skin is { } skin
        ? new UIRect(new Vector2(Bounds.Min.X, Bounds.Max.Y - HeaderOf(skin)), Bounds.Max)
        : default;

    protected override Vector2 Measure(UISkin skin)
    {
        // The stack's own size is what goes below the bar, padding and all, and only as much of it as is open
        Vector2 content = base.Measure(skin);
        contentHeight = content.Y;

        float header = HeaderOf(skin);
        float title = skin.Font.Measure(Resolve(Title), TextScaleOf(skin), markup: false).X + header * 1.5f;

        return new Vector2(MathF.Max(content.X, title), header + content.Y * openness);
    }

    protected override void Arrange(UIRect content)
    {
        // What is below the bar is laid out at its full height from under the bar whatever is showing of it, so folding
        // slides the bottom up over it rather than squashing everything in it
        float header = Module?.Compositor.Skin is { } skin ? HeaderOf(skin) : 0.0f;
        float top = Bounds.Max.Y - header - Padding.Top;
        float height = MathF.Max(0.0f, contentHeight - Padding.Total.Y);

        base.Arrange(new UIRect(new Vector2(content.Min.X, top - height), new Vector2(content.Max.X, top)));
    }

    protected override void Paint(UIDrawList list)
    {
        // The backdrop of a panel, if it has one, behind the lot
        base.Paint(list);

        UISkin skin = list.Skin;
        UIRect header = HeaderBounds;
        bool enabled = EnabledInHierarchy;
        bool lit = enabled && (IsSelected || (IsHovered && header.Contains(PointerInLayout())));
        Vector4 tint = enabled ? Vector4.One : skin.DisabledTint;

        // The bar, the rim and the title are parts of their own. The hover box is a quad that is only there under the
        // pointer, and counted as one of the lot it pushed the title along a quad, so the frame between two ticks
        // blended a letter with the hover box and the title flashed white
        list.BeginPart(BAR_PART);
        bool art = skin.TryGetRegion(lit ? hoverStyle : style, out var region) || skin.TryGetRegion(style, out region);
        if (art)
        {
            list.NineSlice(region, header, tint);
        }
        else
        {
            list.Box(header, skin.ControlColor * tint);
            if (lit)
                list.Box(header, skin.HoverColor);
        }

        list.BeginPart(RIM_PART);
        PaintSelection(list);

        list.BeginPart(TITLE_PART);
        Vector4 color = (TitleColor ?? (art ? region.TextOr(skin.ControlTextColor) : skin.ControlTextColor)) * tint;
        float size = MathF.Round(header.Height * ARROW_SHARE);
        float indent = MathF.Round(header.Height * 0.4f);
        Vector2 arrow = new(header.Min.X + indent + size * 0.5f, header.Center.Y);

        PaintArrow(list, arrow, size, color);

        UIRect text = new(new Vector2(arrow.X + size * 0.5f + indent * 0.75f, header.Min.Y), new Vector2(header.Max.X - indent, header.Max.Y));
        list.Text(Resolve(Title), text, Origin.Left, TextScaleOf(skin), color, markup: false);
        list.EndParts();
    }

    protected override void PaintChildren(UIDrawList list)
    {
        if (openness >= 1.0f)
        {
            base.PaintChildren(list);
            return;
        }

        // Folding, what is below the bar is cut off where the bottom has got to
        list.PushClip(new UIRect(Bounds.Min, new Vector2(Bounds.Max.X, HeaderBounds.Min.Y)));
        base.PaintChildren(list);
        list.PopClip();
    }

    /// <summary>
    /// Helper method to draw the arrow out of a few bars, a step smaller each, so it is the same crisp thing in a
    /// pixel art skin and a smooth one. It points right while folded and down while open, it turns over halfway.
    /// </summary>
    private void PaintArrow(UIDrawList list, Vector2 center, float size, Vector4 color)
    {
        float bar = size / ARROW_STEPS;
        bool down = openness >= 0.5f;

        for (int i = 0; i < ARROW_STEPS; i++)
        {
            float length = size * (1.0f - (float)i / ARROW_STEPS);
            float at = -size * 0.5f + bar * (i + 0.5f);

            UIRect step = down
                ? UIRect.FromCenter(new Vector2(center.X, center.Y - at), new Vector2(length, bar))
                : UIRect.FromCenter(new Vector2(center.X + at, center.Y), new Vector2(bar, length));

            list.Rect(step, color);
        }
    }

    private Vector2 PointerInLayout() => Module is { } module ? module.ToLocal(module.Compositor.Pointer.Position) : default;

    protected override void Update(float dt)
    {
        settled = true;

        float target = expanded ? 1.0f : 0.0f;
        if (openness == target)
            return;

        float step = CollapseTime > 0.0f ? dt / CollapseTime : 1.0f;
        openness = expanded ? MathF.Min(1.0f, openness + step) : MathF.Max(0.0f, openness - step);
    }

    protected internal override void OnPointerDown(Vector2 point) => pressedOnHeader = HeaderBounds.Contains(point);

    protected internal override void OnPointerUp(Vector2 point)
    {
        if (pressedOnHeader && EnabledInHierarchy && HeaderBounds.Contains(point))
            Toggle();

        pressedOnHeader = false;
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("title", () => Title, value => Title = value);
        Expose("expanded", () => Expanded, value => Expanded = value);
        Expose("collapse_time", () => CollapseTime, value => CollapseTime = MathF.Max(0.0f, value));
        Expose("header_height", () => HeaderHeight, value => HeaderHeight = MathF.Max(0.0f, value));
        Expose("text_scale", () => TextScale, value => TextScale = MathF.Max(0.0f, value));
        Expose("title_color", () => TitleColor ?? Vector4.Zero, value => TitleColor = value.W > 0.0f ? value : null);
        Expose("style", () => Style, value => Style = value);
        Expose("on_toggled", () => toggledHandler ?? new NullValue(), value => toggledHandler = value);
    }
}
