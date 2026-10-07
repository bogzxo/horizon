using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.Rendering.UIX.Drawing;
using Horizon.Rendering.UIX.Skinning;

namespace Horizon.Rendering.UIX.Components;

/// <summary>
/// Pages you flip between with a strip of tabs along the top: an options screen, an inventory, an editor's side panel.
/// Every child is a page (anything, usually a stack), the tabs are named by <see cref="Tabs"/> in the same order, and
/// only the <see cref="Selected"/> one is drawn and clickable. The others keep their state and their place in the
/// layout, so nothing jumps about when you flip and the panel is as big as its biggest page.
/// <para>
/// Click a tab to open it, or drive it from a gamepad with <see cref="Next"/> and <see cref="Previous"/> (bumpers are
/// the usual), and put the buttons for that at the ends of the strip with <see cref="PreviousHint"/> and
/// <see cref="NextHint"/>:
/// </para>
/// <code>
/// let tabs = compositor.tabs({ tabs: "Display, Look, Fight", prev_hint: "[icon:pad_lb]", next_hint: "[icon:pad_rb]" });
/// let display = compositor.stack({ parent: tabs, spacing: 8 });
/// let look = compositor.stack({ parent: tabs, spacing: 8 });
/// let fight = compositor.stack({ parent: tabs, spacing: 8 });
/// </code>
/// </summary>
public class TabPanel : UIComponent
{
    // The line under the open tab, and the room between the strip and the pages
    private const float UNDERLINE = 3.0f;

    private string[] tabs = [];
    private int selected;
    private IRuntimeValue? changedHandler;

    /// <summary>What every tab is called, in the order of the pages. A page without a name gets "Tab n".</summary>
    public string[] Tabs
    {
        get => tabs;
        set => tabs = value ?? [];
    }

    /// <summary>The page that is open, counted from 0. Clamped to the pages there are.</summary>
    public int Selected
    {
        get => Math.Clamp(selected, 0, Math.Max(0, ChildSpan.Length - 1));
        set => Select(value);
    }

    /// <summary>The page that is open, null for a panel without any.</summary>
    public UIComponent? SelectedPage => ChildSpan.Length > 0 ? ChildSpan[Selected] : null;

    /// <summary>Called with the index of the page whenever another one is opened, from code or by a click.</summary>
    public Action<int>? OnChanged { get; set; }

    /// <summary>How big the names of the tabs are written, 0 for the skin's text size.</summary>
    public float TextScale { get; set; }

    /// <summary>How tall the strip of tabs is, 0 for as tall as a line of text needs.</summary>
    public float HeaderHeight { get; set; }

    /// <summary>The room between two tabs, and between the strip and the page.</summary>
    public float Spacing { get; set; } = 28.0f;

    /// <summary>Text (icons and all) written at the left end of the strip, the button that goes back a tab.</summary>
    public string PreviousHint { get; set; } = string.Empty;

    /// <summary>Text written at the right end of the strip, the button that goes on a tab.</summary>
    public string NextHint { get; set; } = string.Empty;

    /// <summary>The colour of the open tab's name and the line under it, null for the skin's accent.</summary>
    public Vector4? AccentColor { get; set; }

    /// <summary>The colour of the names of the tabs that aren't open, null for the skin's text dimmed.</summary>
    public Vector4? Color { get; set; }

    protected override bool HitTestVisible => true;

    /// <summary>What the tab of a page says.</summary>
    public string TitleOf(int index) => index < tabs.Length && tabs[index].Length > 0 ? tabs[index] : $"Tab {index + 1}";

    /// <summary>Opens a page, wrapping round past either end.</summary>
    public void Select(int index)
    {
        // A layout sets this before its pages exist, so it's only wrapped round once there are some
        int count = ChildSpan.Length;
        int wanted = count == 0 ? Math.Max(0, index) : ((index % count) + count) % count;
        if (wanted == selected)
            return;

        selected = wanted;
        OnChanged?.Invoke(selected);
        InvokeScript(changedHandler, new NumberValue(selected));
    }

    /// <summary>Opens the next page, back to the first after the last.</summary>
    public void Next() => Select(Selected + 1);

    /// <summary>Opens the page before, round to the last from the first.</summary>
    public void Previous() => Select(Selected - 1);

    /// <summary>Opens whichever page a component is on (a page, or anything inside one). False if it's on none of them.</summary>
    public bool Reveal(UIComponent component)
    {
        for (UIComponent? at = component; at is not null; at = at.Parent)
        {
            if (at.Parent != this)
                continue;

            int index = Array.IndexOf(ChildSpan.ToArray(), at);
            if (index < 0)
                return false;

            Select(index);
            return true;
        }

        return false;
    }

    protected internal override bool ShowsChild(UIComponent child) => child == SelectedPage;

    private float TextScaleOf(UISkin skin) => TextScale > 0.0f ? TextScale : skin.TextScale;

    private float HeaderOf(UISkin skin) => HeaderHeight > 0.0f ? HeaderHeight : MathF.Round(skin.Font.LineHeight * TextScaleOf(skin) * 1.6f);

    protected override Vector2 Measure(UISkin skin)
    {
        // As big as the biggest page, so flipping doesn't make everything around the panel jump
        Vector2 page = Vector2.Zero;
        foreach (var child in ChildSpan)
            page = Vector2.Max(page, child.DesiredSize);

        float scale = TextScaleOf(skin);
        float strip = 0.0f;
        for (int i = 0; i < ChildSpan.Length; i++)
            strip += skin.Font.Measure(TitleOf(i), scale, markup: false).X + Spacing;
        strip += skin.Font.Measure(PreviousHint, scale).X + skin.Font.Measure(NextHint, scale).X;

        return new Vector2(MathF.Max(page.X, strip), page.Y + HeaderOf(skin) + Spacing * 0.5f) + Padding.Total;
    }

    protected override void Arrange(UIRect content)
    {
        // The pages get everything under the strip, each placed by its own anchor and position like in a panel
        float header = Module?.Compositor.Skin is { } skin ? HeaderOf(skin) + Spacing * 0.5f : 0.0f;
        var pages = new UIRect(content.Min, new Vector2(content.Max.X, content.Max.Y - header));

        foreach (var child in ChildSpan)
            child.ArrangeTree(child.Place(pages));
    }

    /// <summary>Where the strip of tabs is.</summary>
    public UIRect HeaderBounds => Module?.Compositor.Skin is { } skin
        ? new UIRect(new Vector2(Bounds.Min.X + Padding.Left, Bounds.Max.Y - Padding.Top - HeaderOf(skin)), new Vector2(Bounds.Max.X - Padding.Right, Bounds.Max.Y - Padding.Top))
        : default;

    /// <summary>Where the tab of a page is on screen, in the layout's units. Laid out in the middle of the strip.</summary>
    public UIRect TabBounds(int index)
    {
        if (Module?.Compositor.Skin is not { } skin || index < 0 || index >= ChildSpan.Length)
            return default;

        float scale = TextScaleOf(skin);
        UIRect header = HeaderBounds;

        float total = 0.0f;
        for (int i = 0; i < ChildSpan.Length; i++)
            total += skin.Font.Measure(TitleOf(i), scale, markup: false).X + (i > 0 ? Spacing : 0.0f);

        float left = header.Center.X - total * 0.5f;
        for (int i = 0; i < index; i++)
            left += skin.Font.Measure(TitleOf(i), scale, markup: false).X + Spacing;

        float width = skin.Font.Measure(TitleOf(index), scale, markup: false).X;
        return new UIRect(new Vector2(left - Spacing * 0.25f, header.Min.Y), new Vector2(left + width + Spacing * 0.25f, header.Max.Y));
    }

    /// <summary>The tab at a point in the layout's units, -1 for none. For whoever wants to click one without the pointer (an editor).</summary>
    public int TabAt(Vector2 point)
    {
        for (int i = 0; i < ChildSpan.Length; i++)
        {
            if (TabBounds(i).Contains(point))
                return i;
        }

        return -1;
    }

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;
        float scale = TextScaleOf(skin);
        UIRect header = HeaderBounds;

        Vector4 accent = AccentColor ?? skin.AccentColor;
        Vector4 dim = Color ?? skin.TextColor * skin.DisabledTint;
        Vector2 pointer = Module is { } module ? module.ToLocal(module.Compositor.Pointer.Position) : default;

        for (int i = 0; i < ChildSpan.Length; i++)
        {
            UIRect tab = TabBounds(i);
            bool open = i == Selected;

            if (!open && IsHovered && tab.Contains(pointer))
                list.Box(tab, skin.HoverColor);

            list.Text(TitleOf(i), tab, Origin.Center, scale, open ? accent : dim, markup: false);

            // The open one is underlined, which reads as a tab whatever the skin looks like
            if (open)
                list.Rect(new UIRect(tab.Min, new Vector2(tab.Max.X, tab.Min.Y + UNDERLINE)), accent);
        }

        if (PreviousHint.Length > 0)
            list.Text(PreviousHint, header, Origin.Left, scale, Vector4.One);
        if (NextHint.Length > 0)
            list.Text(NextHint, header, Origin.Right, scale, Vector4.One);
    }

    protected internal override void OnPointerUp(Vector2 point)
    {
        if (EnabledInHierarchy && TabAt(point) is >= 0 and var index)
            Select(index);
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        // Scripts have no lists, so the names are one text with commas in it (like a selector's options)
        Expose("tabs", () => string.Join(", ", tabs), value => Tabs = value.Split(',', StringSplitOptions.TrimEntries));
        Expose("selected", () => selected, value => Select((int)value));
        Expose("text_scale", () => TextScale, value => TextScale = value);
        Expose("header_height", () => HeaderHeight, value => HeaderHeight = MathF.Max(0.0f, value));
        Expose("spacing", () => Spacing, value => Spacing = MathF.Max(0.0f, value));
        Expose("prev_hint", () => PreviousHint, value => PreviousHint = value);
        Expose("next_hint", () => NextHint, value => NextHint = value);
        Expose("accent", () => AccentColor ?? Vector4.Zero, value => AccentColor = value);
        Expose("color", () => Color ?? Vector4.Zero, value => Color = value);
        Expose("on_changed", () => changedHandler ?? new NullValue(), value => changedHandler = value);
    }
}
