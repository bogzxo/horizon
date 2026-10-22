using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// One setting as a settings page shows it, what it is, what sets it and the sentence that says what it does.
/// <paramref name="Buttons"/> are pressed beside what sets it (a picture chosen, the usual one back), see
/// <see cref="SettingsPage.OnButton"/>, <paramref name="Slider"/> has a number between its least and its most set with a
/// slider rather than typed, and a row that is <paramref name="ButtonsOnly"/> is its buttons and nothing else (the
/// panels put back to their sizes).
/// </summary>
public sealed record UISetting(
    string Key,
    string Title,
    UIFormKind Kind,
    string Help,
    string Group = "",
    string[]? Choices = null,
    float Least = 0.0f,
    float Most = 0.0f,
    float Step = 1.0f,
    string[]? Buttons = null,
    bool Slider = false,
    bool ButtonsOnly = false);

/// <summary>
/// A page of settings the way VS Code has one, because it works. A search box over all of them, the sections down the
/// left (a click scrolls to one), and a row a setting, its section and its name, a sentence saying what it does at the
/// size the rest of the text is (not a tooltip), and what sets it, a toggle, a dropdown, a number, a line of text, or
/// a button a choice for a list. A setting that is not what it comes as has a line in the accent down its edge and a
/// reset beside its name. What the page shows is asked of whoever has the settings (<see cref="ValueOf"/>,
/// <see cref="IsModified"/>) and what is changed is told to them (<see cref="OnChanged"/>, <see cref="OnReset"/>), who
/// apply it, write the file and call <see cref="Refresh(string)"/>. Dawn's settings, Dusk's and an extension's are this.
/// </summary>
public class SettingsPage : UIComponent
{
    private const float SEARCH_HEIGHT = 34.0f, NAV_WIDTH = 210.0f, GAP = 12.0f, ROW_GAP = 6.0f, CONTROL_HEIGHT = 28.0f;

    // How round the sidebar the sections are on is
    private const float NAV_RADIUS = 10.0f;

    private readonly TextBox search;
    private readonly Label title;
    private readonly Button close;
    private readonly ScrollPanel navScroll;
    private readonly StackPanel nav;
    private readonly ScrollPanel scroll;
    private readonly StackPanel rows;

    // What the rows say when a search leaves none of them
    private readonly Label nothing;

    // What had the keyboard the last update, a row's control tabbed to is scrolled into sight once
    private UIComponent? focusSeen;
    private readonly List<UISetting> settings = [];
    private readonly Dictionary<string, SettingRow> rowOf = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Label> headerOf = new(StringComparer.Ordinal);
    private float builtWidth = -1.0f;
    private bool dirty = true;

    /// <summary>The settings in the order they are shown, their groups the sections.</summary>
    public IReadOnlyList<UISetting> Settings
    {
        get => settings;
        set
        {
            settings.Clear();
            settings.AddRange(value);
            dirty = true;
        }
    }

    /// <summary>What a setting is now.</summary>
    public Func<string, IRuntimeValue?>? ValueOf { get; set; }

    /// <summary>Whether a setting is not what it comes as.</summary>
    public Func<string, bool>? IsModified { get; set; }

    /// <summary>Where a section goes among the others, lower first, null for the order they came in. Equal ones keep that order.</summary>
    public Func<string, int>? GroupOrder { get; set; }

    /// <summary>A setting changed on the page.</summary>
    public Action<string, IRuntimeValue>? OnChanged { get; set; }

    /// <summary>A setting's reset pressed, back to what it comes as.</summary>
    public Action<string>? OnReset { get; set; }

    /// <summary>One of a setting's <see cref="UISetting.Buttons"/> pressed, by its key and which.</summary>
    public Action<string, int>? OnButton { get; set; }

    // Whole components shown as sections of their own (an extension's layout of settings), by the section, in the order
    // they were given, after the settings' own sections unless a setting is in the same one
    private readonly List<(string Group, UIComponent Content)> panels = [];

    /// <summary>
    /// Shows a component as a section of the page, or under a section's settings when the section has some, for what
    /// isn't a list of settings (an extension's own layout of them). Null takes it away. The search shows it whole when
    /// what's typed is in the section's name.
    /// </summary>
    public void SetPanel(string group, UIComponent? content)
    {
        int at = panels.FindIndex(panel => panel.Group == group);
        if (at >= 0)
            panels.RemoveAt(at);
        if (content is not null)
            panels.Add((group, content));
        dirty = true;
    }

    /// <summary>How big the names are and the sentences under them, 0 for the skin's.</summary>
    public float TitleScale { get; set; }

    public float BodyScale { get; set; }

    /// <summary>What the page says at its top left, over the sections. Empty for nothing, the sections up to the top, for a page in a window whose title says it already.</summary>
    public string Title
    {
        get => titleText;
        set
        {
            titleText = value;
            title.Text = $"[b]{value}[/b]";
            title.Visible = value.Length > 0;
        }
    }

    private string titleText = "Settings";

    /// <summary>The x at the top right pressed (or Escape), null for a page with no x.</summary>
    public Action? OnClose
    {
        get => onClose;
        set
        {
            onClose = value;
            close.Visible = value is not null && !hideClose;
        }
    }

    private Action? onClose;

    /// <summary>Whether the x is left off while <see cref="OnClose"/> still has Escape close it, for a page in a window that has an x of its own.</summary>
    public bool HideClose
    {
        get => hideClose;
        set
        {
            hideClose = value;
            close.Visible = onClose is not null && !value;
        }
    }

    private bool hideClose;

    /// <summary>The search box, for a program to give the keyboard to.</summary>
    public TextBox Search => search;

    /// <summary>How many rows the search leaves, for a test.</summary>
    public int RowsShown => rowOf.Values.Count(row => row.Visible);

    protected override bool HitTestVisible => true;

    public SettingsPage()
    {
        Size = new Vector2(900, 600);
        Padding = new UIEdges(20.0f, 14.0f, 20.0f, 14.0f);
        title = Add(new Label { Text = "[b]Settings[/b]", Align = Origin.Left });
        search = Add(new TextBox { Placeholder = "Search settings", Padding = new UIEdges(10.0f, 6.0f, 10.0f, 6.0f) });
        search.OnChanged = Filter;
        close = Add(new Button { Icon = "close", Style = "button_flat", Animated = false, Tooltip = "Close the settings", Visible = false, OnPressed = () => onClose?.Invoke() });
        // The sections down the left scroll as well, a few extensions with settings of their own ran them off the bottom
        navScroll = Add(new ScrollPanel { Padding = new UIEdges(0.0f, 6.0f, 2.0f, 6.0f) });
        // No gaps between the sections' rows, a pointer going down them is always on one, the gap is in their pills
        nav = navScroll.Add(new StackPanel { Spacing = 0.0f, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
        scroll = Add(new ScrollPanel());
        rows = scroll.Add(new StackPanel { Spacing = ROW_GAP, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
        nothing = Add(new Label { Align = Origin.Center, Visible = false });
    }

    /// <summary>Every row showing what its setting is now.</summary>
    public void Refresh()
    {
        foreach (string key in rowOf.Keys)
            Refresh(key);
    }

    /// <summary>A row showing what its setting is now, after it was changed.</summary>
    public void Refresh(string key)
    {
        if (rowOf.TryGetValue(key, out SettingRow? row))
            row.Show(ValueOf?.Invoke(key), IsModified?.Invoke(key) ?? false);
    }

    /// <summary>A setting set as if it had been changed on its row, for a test.</summary>
    public bool Enter(string key, IRuntimeValue value)
    {
        if (!rowOf.ContainsKey(key))
            return false;
        OnChanged?.Invoke(key, value);
        Refresh(key);
        return true;
    }

    /// <summary>A setting's reset pressed, for a test.</summary>
    public bool Reset(string key)
    {
        if (!rowOf.ContainsKey(key))
            return false;
        OnReset?.Invoke(key);
        Refresh(key);
        return true;
    }

    /// <summary>The rows narrowed to what has every word of a text in it, as typing it in the search box does.</summary>
    public void Find(string text)
    {
        search.Text = text;
        Filter(text);
    }

    /// <summary>The page scrolled to a section, as a click on its name does.</summary>
    public void GoTo(string group)
    {
        // Asked for before the page has been built and laid out (as it opens), gone to once it has
        if (dirty || !headerOf.TryGetValue(group, out Label? header) || header.Bounds.IsEmpty)
        {
            wantedGroup = group;
            return;
        }

        wantedGroup = null;
        scroll.ScrollToTop(header);
    }

    private string? wantedGroup;

    protected internal override void OnKeyDown(in UIKeyPress press)
    {
        if (press.Key == Silk.NET.Input.Key.Escape && onClose is not null)
            onClose();
    }

    // Escape in the search box, or anything else of the page with the keyboard, closes it. It only let go of the box
    protected internal override bool OnEscape()
    {
        if (onClose is null)
            return false;

        onClose();
        return true;
    }

    // A control of a row given the keyboard (tabbed to) brought into sight, it was typed into off the bottom of the page
    private void ShowFocused()
    {
        UIComponent? focused = Module?.Compositor.Focus;
        if (ReferenceEquals(focused, focusSeen))
            return;

        focusSeen = focused;
        for (UIComponent? at = focused; at is not null; at = at.Parent)
        {
            if (at is SettingRow row)
            {
                scroll.ScrollIntoView(row);
                return;
            }

            // A control in a section's panel (the profiles, the language servers' form) is brought into sight itself,
            // the keyboard went down a form off the bottom of the window
            if (at.Parent == rows && focused is not null)
            {
                scroll.ScrollIntoView(focused);
                return;
            }
        }
    }

    /// <summary>The section at the top of the rows as they're scrolled, the one lit down the left.</summary>
    public string? CurrentGroup { get; private set; }

    // The last heading that has reached the top of the rows, the first while none has
    private void FindCurrent()
    {
        float top = scroll.Bounds.Max.Y - scroll.Padding.Top;
        string? current = null;
        foreach (var (group, header) in headerOf)
        {
            if (!header.Visible || header.Bounds.IsEmpty)
                continue;

            current ??= group;
            if (header.Bounds.Max.Y >= top - 12.0f)
                current = group;
        }

        CurrentGroup = current;
    }

    protected override void Update(float dt)
    {
        float width = scroll.Bounds.Width - 18.0f;
        if (dirty || width > 0.0f && MathF.Abs(width - builtWidth) > 1.0f)
        {
            dirty = false;
            Build(MathF.Max(240.0f, width > 0.0f ? width : Size.X - NAV_WIDTH - GAP * 2.0f));
        }

        // A section asked for while the page was being made, once its heading has been laid out
        if (wantedGroup is { } wanted && headerOf.TryGetValue(wanted, out Label? heading) && !heading.Bounds.IsEmpty)
            GoTo(wanted);

        FindCurrent();
        ShowFocused();

        base.Update(dt);
    }

    protected override Vector2 Measure(UISkin skin) => Size;

    protected override void Arrange(UIRect content)
    {
        var titleArea = new UIRect(new Vector2(content.Min.X, content.Max.Y - SEARCH_HEIGHT), new Vector2(content.Min.X + NAV_WIDTH, content.Max.Y));
        float closeWidth = close.Visible ? SEARCH_HEIGHT + GAP : 0.0f;
        var searchArea = new UIRect(new Vector2(content.Min.X + NAV_WIDTH + GAP, content.Max.Y - SEARCH_HEIGHT), new Vector2(content.Max.X - GAP - closeWidth, content.Max.Y));
        title.ArrangeTree(titleArea);
        close.ArrangeTree(new UIRect(new Vector2(content.Max.X - SEARCH_HEIGHT, content.Max.Y - SEARCH_HEIGHT), content.Max));
        // With no title the sections go up to the top, beside the search
        float navTop = title.Visible ? content.Max.Y - SEARCH_HEIGHT - GAP : content.Max.Y;
        var navArea = new UIRect(new Vector2(content.Min.X, content.Min.Y), new Vector2(content.Min.X + NAV_WIDTH, navTop));
        var scrollArea = new UIRect(new Vector2(content.Min.X + NAV_WIDTH + GAP, content.Min.Y), new Vector2(content.Max.X, content.Max.Y - SEARCH_HEIGHT - GAP));
        search.ArrangeTree(searchArea);
        nothing.ArrangeTree(new UIRect(new Vector2(scrollArea.Min.X, scrollArea.Max.Y - 120.0f), scrollArea.Max));
        navScroll.ArrangeTree(navArea);
        scroll.ArrangeTree(scrollArea);
    }

    /// <summary>Whether it covers what is under it with the skin's panel colour, for a page over the whole of a window.</summary>
    public bool Opaque { get; set; } = true;

    protected override void Paint(UIDrawList list)
    {
        if (Opaque)
            list.Rect(Bounds, list.Skin.PanelColor with { W = 1.0f });

        // The sections down the left on a sidebar of their own, a round well a breath lighter than what it's on. It was a
        // line between them and the rows, and with the sections' scroll bar beside it that was two lines
        if (!navScroll.Bounds.IsEmpty)
            list.RoundRect(navScroll.Bounds, NAV_RADIUS, list.Skin.TextColor with { W = 0.035f });
    }

    private void Build(float width)
    {
        builtWidth = width;
        if (Module?.Compositor.Skin is { } skin)
        {
            titleSize = TitleScale > 0.0f ? TitleScale * 1.5f : skin.TextSize(UITextRole.Title);
            headingSize = TitleScale > 0.0f ? TitleScale * 1.3f : skin.TextSize(UITextRole.Heading);
            nameSize = TitleScale > 0.0f ? TitleScale : skin.TextSize(UITextRole.Body);
            sentenceSize = BodyScale > 0.0f ? BodyScale : skin.TextSize(UITextRole.Body);
        }

        title.TextScale = titleSize;
        foreach (UIComponent child in rows.Children.ToArray())
            rows.Remove(child);
        foreach (UIComponent child in nav.Children.ToArray())
            nav.Remove(child);
        rowOf.Clear();
        headerOf.Clear();

        // the rows of a section together, the sections in the order they first come, the panels' after them
        var groups = settings.Select(setting => setting.Group).Concat(panels.Select(panel => panel.Group)).Distinct().ToList();
        if (GroupOrder is { } order)
            groups = [.. groups.Select((group, at) => (group, at)).OrderBy(pair => order(pair.group)).ThenBy(pair => pair.at).Select(pair => pair.group)];
        string? part = null;
        foreach (string group in groups)
        {
            string named = group.Length == 0 ? "General" : group;
            int dot = named.IndexOf(" · ", StringComparison.Ordinal);
            string top = dot < 0 ? named : named[..dot], under = dot < 0 ? named : named[(dot + 3)..];

            // The section big and the part it's of quietly in front of it
            string heading = dot < 0 ? $"[b]{named}[/b]" : $"[color:dim]{top} ·[/color] [b]{under}[/b]";
            headerOf[group] = rows.Add(new Label { Text = heading, TextScale = headingSize, Align = Origin.Left, Size = new Vector2(width, 44.0f) });

            // down the left, the part a section is of (Terminal, Editor) once, small and quiet the way a sidebar heads
            // its groups, its sections under it
            if (top != part)
            {
                part = top;
                nav.Add(new Label { Text = $"[color:dim][b]{top}[/b][/color]", TextScale = sentenceSize * 0.9f, Align = Origin.BottomLeft, Size = new Vector2(NAV_WIDTH - 12.0f, nav.Children.Count == 0 ? 22.0f : 34.0f), Padding = new UIEdges(10.0f, 0.0f, 0.0f, 4.0f), Anchor = Origin.Left, Pivot = Origin.Left });
            }

            nav.Add(new SectionLink(this, group) { Text = under, TextScale = sentenceSize, Align = Origin.Left, Size = new Vector2(NAV_WIDTH - 12.0f, 30.0f), Padding = new UIEdges(10.0f, 0.0f, 0.0f, 0.0f), Anchor = Origin.Left, Pivot = Origin.Left });

            foreach (UISetting setting in settings)
            {
                if (setting.Group == group)
                    rowOf[setting.Key] = rows.Add(new SettingRow(this, setting, width));
            }

            foreach (var (of, content) in panels)
            {
                if (of != group)
                    continue;
                content.Parent?.Remove(content);
                content.Anchor = Origin.TopLeft;
                content.Pivot = Origin.TopLeft;
                rows.Add(content);
            }
        }

        Refresh();
        Filter(search.Text);
    }


    // The rows whose name, key or sentence has every word typed in it, the sections with none of them hidden
    private void Filter(string typed)
    {
        string[] words = typed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (SettingRow row in rowOf.Values)
        {
            UISetting setting = row.Setting;
            row.Visible = words.All(word => setting.Title.Contains(word, StringComparison.OrdinalIgnoreCase)
                || setting.Key.Contains(word, StringComparison.OrdinalIgnoreCase)
                || setting.Help.Contains(word, StringComparison.OrdinalIgnoreCase)
                || setting.Group.Contains(word, StringComparison.OrdinalIgnoreCase));
            if (row.Visible)
                seen.Add(setting.Group);
        }

        // A panel by the name of its section, it has no rows to search
        foreach (var (group, content) in panels)
        {
            content.Visible = words.All(word => group.Contains(word, StringComparison.OrdinalIgnoreCase));
            if (content.Visible)
                seen.Add(group);
        }

        foreach (var (name, header) in headerOf)
            header.Visible = seen.Contains(name);

        // A search that leaves nothing says so rather than leaving the page blank
        nothing.Visible = seen.Count == 0 && words.Length > 0;
        nothing.Text = $"[color:dim]No settings match \"{typed.Trim()}\"[/color]";
        nothing.TextScale = sentenceSize;
    }

    internal void Changed(string key, IRuntimeValue value)
    {
        OnChanged?.Invoke(key, value);
        Refresh(key);
    }

    internal void ButtonPressed(string key, int which) => OnButton?.Invoke(key, which);

    internal void ResetPressed(string key)
    {
        OnReset?.Invoke(key);
        Refresh(key);
    }

    /// <summary>A section's name down the left, a click scrolls to it.</summary>
    private sealed class SectionLink(SettingsPage page, string group) : Label
    {
        // How far the pointer's pill has come on, eased in and out over the hover's time so a pointer going down the list
        // leaves a little wake rather than pills blinking on and off
        private float lit;

        protected override bool HitTestVisible => true;

        protected internal override void OnClick() => page.GoTo(group);

        protected override void Update(float dt)
        {
            float time = MotionTime(DEFAULT_HOVER_TIME);
            float step = time > 0.0f ? dt / time : 1.0f;
            lit = IsHovered ? MathF.Min(1.0f, lit + step) : MathF.Max(0.0f, lit - step);
            base.Update(dt);
        }

        protected override void Paint(UIDrawList list)
        {
            // A pill under the section that's at the top of the rows, the way a sidebar has what's open, and a fainter
            // one under the pointer, a pixel in from the row's top and bottom so two side by side are apart. Always there,
            // see-through when not lit, or the quads after it shift (ToggleButton has why)
            bool current = page.CurrentGroup == group;
            float hover = lit * (2.0f - lit);
            list.Box(Bounds.Shrink(new UIEdges(0.0f, 1.0f)), list.Skin.TextColor with { W = current ? 0.1f : 0.05f * hover });
            base.Paint(list);
        }
    }

    // the sizes it was last built at, the skin's by what each text is for unless it was given scales
    private float titleSize, headingSize, nameSize, sentenceSize;

    internal float RowTitleScale => nameSize;
    internal float RowBodyScale => sentenceSize;
    internal const float RowControlHeight = CONTROL_HEIGHT;
}
