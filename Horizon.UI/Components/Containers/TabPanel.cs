using Horizon.Content;
using Horizon.Rendering;
using System.Numerics;

using Horizon.Core.Tweening;
using Horizon.HIDL.Runtime;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// Pages you flip between with a strip of tabs along the top, an options screen, an inventory, an editor's side panel.
/// Every child is a page (anything, usually a stack), the tabs are named by <see cref="Tabs"/> in the same order, and
/// only the <see cref="Selected"/> one is drawn and clickable. The others keep their state and their place in the
/// layout, so nothing jumps about when you flip and the panel is as big as its biggest page.
/// <para>
/// Click a tab to open it, or drive it from a gamepad with <see cref="Next"/> and <see cref="Previous"/> (bumpers are
/// the usual), and put the buttons for that at the ends of the strip with <see cref="PreviousHint"/> and
/// <see cref="NextHint"/>.
/// </para>
/// <code>
/// let tabs = compositor.tabs({ tabs: ["Display", "Look", "Fight"], prev_hint: "[icon:pad_lb]", next_hint: "[icon:pad_rb]" });
/// let display = compositor.stack({ parent: tabs, spacing: 8 });
/// let look = compositor.stack({ parent: tabs, spacing: 8 });
/// let fight = compositor.stack({ parent: tabs, spacing: 8 });
/// </code>
/// </summary>
public class TabPanel : UIComponent
{
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

    /// <summary>
    /// Called with whether it's wanted folded down to its strip of tabs, by the chevron at the right end of the strip (down
    /// while it's open, up while it's folded) and by a tab clicked while it's folded. The chevron is only there while
    /// somebody listens, and the panel doesn't fold itself, whoever listens says so with <see cref="Folded"/> (Hex's
    /// panel under the editor folds itself with nothing wrong as well).
    /// </summary>
    public Action<bool>? OnFold { get; set; }

    // How long folding it down or opening it up takes, a drawer's time, see UIComponent.Sliding
    private const float FOLD_TIME = DEFAULT_SLIDE_TIME + 0.04f;

    // How far folded it is, 0 open and 1 down to its strip, at a steady rate and eased where it's used, where it's going,
    // and how tall it was before any of it was folded away
    private float foldProgress;
    private bool folded;
    private float openHeight;

    /// <summary>
    /// Whether it's folded down to its strip of tabs, which way the chevron points. It eases there, a little shorter every
    /// tick with whatever's round it making room, its page keeping the height it has open and cut off at the panel's
    /// bottom, so a terminal on it isn't squashed a row at a time. Its <see cref="UIComponent.Size"/> stays what it is
    /// open. Set it with <see cref="Fold"/> to have it get there at once.
    /// </summary>
    public bool Folded
    {
        get => folded;
        set => Fold(value);
    }

    /// <summary>How far folded it is, eased, 0 open and 1 down to its strip. For a program sizing things round it by hand.</summary>
    public float FoldAmount => Ease.Apply(Easing.OutCubic, foldProgress);

    /// <summary>Whether it's on its way up or down.</summary>
    public bool IsFolding => foldProgress != (folded ? 1.0f : 0.0f);

    /// <summary>
    /// Folds it down to its strip or opens it up again, eased the way <see cref="Folded"/> says, or at once when it's
    /// told to be instant (a program putting itself back the way it was left) or the UI isn't to move.
    /// </summary>
    public void Fold(bool fold, bool instantly = false)
    {
        folded = fold;
        if (instantly || MotionTime(FOLD_TIME) <= 0.0f)
            foldProgress = fold ? 1.0f : 0.0f;
    }

    /// <summary>How tall it is folded, its strip and the room round it.</summary>
    public float FoldedHeight => Module?.Compositor.Skin is { } skin ? HeaderOf(skin) + Spacing * 0.5f + Padding.Total.Y : 0.0f;

    protected override void Update(float dt)
    {
        if (IsFolding)
        {
            float time = MotionTime(FOLD_TIME);
            float step = time > 0.0f ? dt / time : 1.0f;
            foldProgress = folded ? MathF.Min(1.0f, foldProgress + step) : MathF.Max(0.0f, foldProgress - step);
        }

        base.Update(dt);
    }

    /// <summary>
    /// How tall it asks to be, as far folded as it is, when it's <paramref name="open"/> tall open. From its whole height
    /// down to its strip, on whole pixels. For a program fitting things round it by hand before the layout has.
    /// </summary>
    public float FoldedFrom(float open)
    {
        float amount = FoldAmount;
        return amount <= 0.0f ? open : MathF.Round(open + (MathF.Min(FoldedHeight, open) - open) * amount);
    }

    protected override Vector2 Folding(Vector2 desired)
    {
        openHeight = desired.Y;
        return desired with { Y = FoldedFrom(desired.Y) };
    }

    /// <summary>Where the chevron that folds it is, empty while nobody listens for it.</summary>
    public UIRect FoldBounds
    {
        get
        {
            if (OnFold is null)
                return default;

            UIRect header = HeaderBounds;
            float side = header.Height;
            return new UIRect(new Vector2(header.Max.X - side, header.Min.Y), header.Max);
        }
    }

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

    /// <summary>
    /// The layout a page is made from when the program adds pages of its own (<see cref="AddPage"/>), next to this one's
    /// layout. Empty for a panel whose pages are all in its layout.
    /// </summary>
    public string Template { get; set; } = string.Empty;

    protected override bool HitTestVisible => true;

    /// <summary>
    /// Makes a page out of <see cref="Template"/> at the end, with a tab called <paramref name="title"/>, and hands back what
    /// was made. <paramref name="layout"/> is the layout this panel is in, where the template is looked for.
    /// </summary>
    public UILayout AddPage(UILayout layout, string title)
    {
        if (Template.Length == 0)
            throw new InvalidOperationException("A tab panel has pages added to it but doesn't say what from, it needs a template.");

        UILayout page = layout.Instantiate(Template, this);
        string[] named = new string[Math.Max(tabs.Length, ChildSpan.Length)];
        for (int i = 0; i < named.Length; i++)
            named[i] = i < tabs.Length ? tabs[i] : string.Empty;
        named[^1] = title;
        tabs = named;
        return page;
    }

    /// <summary>What the tab of a page says.</summary>
    public string TitleOf(int index) => index < tabs.Length && tabs[index].Length > 0 ? Resolve(tabs[index]) : Numbered(index);

    // "Tab 3" for a page nobody named, made once for each number, it's measured every tick
    private static readonly List<string> numbered = [];

    private static string Numbered(int index)
    {
        lock (numbered)
        {
            while (numbered.Count <= index)
                numbered.Add($"Tab {numbered.Count + 1}");
            return numbered[index];
        }
    }

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

    // Folded all the way the page isn't there to be seen or clicked or tabbed to, only its tab
    protected internal override bool ShowsChild(UIComponent child) => child == SelectedPage && foldProgress < 1.0f;

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
            strip += skin.Font.Measure(TitleOf(i), scale).X + Spacing;
        strip += skin.Font.Measure(Resolve(PreviousHint), scale).X + skin.Font.Measure(Resolve(NextHint), scale).X;

        return new Vector2(MathF.Max(page.X, strip), page.Y + HeaderOf(skin) + Spacing * 0.5f) + Padding.Total;
    }

    protected override void Arrange(UIRect content)
    {
        // The pages get everything under the strip, each placed by its own anchor and position like in a panel. Folding,
        // they keep the height they have open and hang off the bottom
        float header = Module?.Compositor.Skin is { } skin ? HeaderOf(skin) + Spacing * 0.5f : 0.0f;
        float tall = MathF.Max(content.Height, openHeight - Padding.Total.Y);
        var pages = new UIRect(new Vector2(content.Min.X, content.Max.Y - tall), new Vector2(content.Max.X, content.Max.Y - header));

        foreach (var child in ChildSpan)
            child.ArrangeTree(child.Place(pages));
    }

    // How much of the way down it's gone by the time its page has faded out, folding or sliding away
    private const float PAGE_FADE = 0.6f;

    // What of the page hangs off the bottom as it folds is cut off there, and the page fades out as it goes and in as it
    // comes back. A terminal on it paints its cells in steps, a tick at a time, and they went down in jumps beside its
    // marks and everything round it gliding
    protected override void PaintChildren(UIDrawList list)
    {
        float gone = MathF.Max(FoldAmount, Sliding ? 1.0f - SlideAmount : 0.0f);
        if (gone <= 0.0f)
        {
            base.PaintChildren(list);
            return;
        }

        list.PushClip(Bounds);
        list.PushVisual(Vector2.Zero, Vector2.One, Vector2.Zero, Math.Clamp(1.0f - gone / PAGE_FADE, 0.0f, 1.0f));
        base.PaintChildren(list);
        list.PopVisual();
        list.PopClip();
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
            total += skin.Font.Measure(TitleOf(i), scale).X + (i > 0 ? Spacing : 0.0f);

        float left = header.Center.X - total * 0.5f;
        for (int i = 0; i < index; i++)
            left += skin.Font.Measure(TitleOf(i), scale).X + Spacing;

        float width = skin.Font.Measure(TitleOf(index), scale).X;
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

        // The open one in the text's colour on its pill, the accent only when one's given. It was gold, words and an underline
        Vector4 accent = AccentColor ?? skin.TextColor;
        Vector4 dim = Color ?? skin.TextColor * skin.DisabledTint;
        Vector2 pointer = Module is { } module ? module.ToLocal(module.Compositor.Pointer.Position) : default;

        for (int i = 0; i < ChildSpan.Length; i++)
        {
            list.BeginPart(i);
            UIRect tab = TabBounds(i);
            bool open = i == Selected;

            // A pill under the open one, the same as a tab strip's and a picked row's, a fainter one under the pointer.
            // Always there, see-through when not lit, or the quads after it shift (ToggleButton has why)
            UIRect pill = tab.Shrink(new UIEdges(2.0f, 3.0f));
            list.Box(pill, open ? (AccentColor is { } given ? given with { W = ListBox.PICKED } : skin.TextColor with { W = ListBox.PICKED })
                : IsHovered && tab.Contains(pointer) ? skin.HoverColor : skin.HoverColor with { W = 0.0f });

            list.Text(TitleOf(i), tab, Origin.Center, scale, open ? accent : dim);
        }

        list.EndParts();

        // The chevron that folds it, quiet, lit under the pointer, always painted while it's there so the quads don't shift
        if (OnFold is not null)
        {
            UIRect fold = FoldBounds;
            bool over = IsHovered && fold.Contains(pointer);
            list.Box(fold.Shrink(new UIEdges(3.0f)), skin.HoverColor with { W = over ? skin.HoverColor.W : 0.0f });
            float side = MathF.Round(fold.Height * 0.5f);
            list.Symbol(Folded ? "chevron-up" : "chevron-down", UIRect.FromCenter(fold.Center, new Vector2(side)), over ? skin.TextColor : dim);
        }

        if (PreviousHint.Length > 0)
            list.Text(Resolve(PreviousHint), header, Origin.Left, scale, Vector4.One);
        if (NextHint.Length > 0)
            list.Text(Resolve(NextHint), header, Origin.Right, scale, Vector4.One);
    }

    protected internal override void OnPointerUp(Vector2 point)
    {
        if (!EnabledInHierarchy)
            return;

        if (FoldBounds.Contains(point))
        {
            OnFold?.Invoke(!Folded);
            return;
        }

        // A tab clicked on a folded panel opens it as well as its page
        if (TabAt(point) is >= 0 and var index)
        {
            if (Folded)
                OnFold?.Invoke(false);
            Select(index);
        }
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        // A list of names, or one text with commas in it the way the old layouts wrote it
        Expose("tabs", () => tabs, value => Tabs = value);
        Expose("selected", () => selected, value => Select((int)value));
        Expose("text_scale", () => TextScale, value => TextScale = value);
        Expose("header_height", () => HeaderHeight, value => HeaderHeight = MathF.Max(0.0f, value));
        Expose("spacing", () => Spacing, value => Spacing = MathF.Max(0.0f, value));
        Expose("prev_hint", () => PreviousHint, value => PreviousHint = value);
        Expose("next_hint", () => NextHint, value => NextHint = value);
        Expose("accent", () => AccentColor ?? Vector4.Zero, value => AccentColor = value);
        Expose("color", () => Color ?? Vector4.Zero, value => Color = value);
        Expose("template", () => Template, value => Template = value);
        Expose("on_changed", () => changedHandler ?? new NullValue(), value => changedHandler = value);
    }
}
