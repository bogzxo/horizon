using System.Numerics;

using Horizon.HIDL;
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
/// panels put back to their sizes). A choice can have <paramref name="ChoiceLabels"/>, what each of its
/// <paramref name="Choices"/> says in the dropdown ("Fast-forward only" for ff_only), and <paramref name="ChoiceHelp"/>,
/// a line under it saying what the one picked does, the way VS Code describes the values of a setting. Both go by the
/// choices' order, and a choice with none of its own is written the way it was. <paramref name="Keywords"/> are other
/// words for it, split by spaces, that a search finds it by and the page never shows (word wrap and wordwrap for
/// wrap_code). A row that <paramref name="DependsOn"/> the key of a switch is in a little under it and faint while the
/// switch is off. A row of <see cref="UIFormKind.Pairs"/> has <paramref name="KeyPlaceholder"/> and
/// <paramref name="ValuePlaceholder"/> in the two boxes of a pair being typed, what one looks like (*.orig and csharp).
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
    bool ButtonsOnly = false,
    string Placeholder = "",
    string Unit = "",
    string[]? ChoiceLabels = null,
    string[]? ChoiceHelp = null,
    string Keywords = "",
    string DependsOn = "",
    string KeyPlaceholder = "",
    string ValuePlaceholder = "")
{
    /// <summary>What a choice says in its dropdown, its label when it has one and otherwise the choice with a capital.</summary>
    public string LabelOf(string choice) => LabelAt(Choices is null ? -1 : Array.IndexOf(Choices, choice), choice);

    /// <summary>The line that says what a choice does, empty for a choice with none.</summary>
    public string HelpOf(string choice) => HelpAt(Choices is null ? -1 : Array.IndexOf(Choices, choice));

    /// <summary>What the choice at a place in <see cref="Choices"/> says, the choice itself with a capital when it has no label.</summary>
    public string LabelAt(int at, string choice) =>
        ChoiceLabels is { } labels && at >= 0 && at < labels.Length && labels[at].Length > 0 ? labels[at] : SettingRow.Capitalised(choice);

    /// <summary>The line of the choice at a place in <see cref="Choices"/>, empty for none.</summary>
    public string HelpAt(int at) => ChoiceHelp is { } help && at >= 0 && at < help.Length ? help[at] : string.Empty;
}

/// <summary>
/// A page of settings the way VS Code has one, because it works. A search box over all of them, the sections down the
/// left (a click scrolls to one), and a row a setting, its section and its name, a sentence saying what it does at the
/// size the rest of the text is (not a tooltip), and what sets it, a toggle, a dropdown, a number, a line of text, or
/// a button a choice for a list. A setting that is not what it comes as has a line in the accent down its edge and a
/// reset beside its name. What the page shows is asked of whoever has the settings (<see cref="ValueOf"/>,
/// <see cref="IsModified"/>) and what is changed is told to them (<see cref="OnChanged"/>, <see cref="OnReset"/>), who
/// apply it, write the file and call <see cref="Refresh(string)"/>. Dawn's settings, Dusk's and an extension's are this.
/// The search is in SettingsPage.Search, the sections down the left in SettingsPage.Nav, going to one setting (a link
/// to it, the flash it gets) in SettingsPage.Reveal and getting about it with the keyboard in SettingsPage.Keys.
/// </summary>
public partial class SettingsPage : UIComponent
{
    private const float SEARCH_HEIGHT = 34.0f, NAV_WIDTH = 210.0f, GAP = 12.0f, ROW_GAP = 6.0f, CONTROL_HEIGHT = 28.0f;

    // How wide a row gets on a wide window. Its sentence stops short of the whole of it and what sets it goes in a column
    // down the right, so a wide row is room for both rather than a line too long to read. It was 1120 and on a big screen
    // the rows were a strip down the left of a window that was mostly empty
    private const float MAX_ROW = 1600.0f;

    /// <summary>
    /// How much bigger than the skin's the page draws its text and the controls and columns round it, 1 as it comes. A
    /// program drawn small on a big screen (Hex's self test, or a size picked small on a 4K screen) zooms its settings so
    /// they're read without leaning in.
    /// </summary>
    public float Zoom
    {
        get => zoomAsked;
        set
        {
            zoomAsked = Math.Clamp(value, 1.0f, 4.0f);
            FitZoom();
        }
    }

    // The zoom asked for and the one the page is drawn at, less while the page is too narrow for it. A window dragged
    // small would have had its rows give up on their column down the right
    private float zoomAsked = 1.0f, zoom = 1.0f;

    // How wide the page wants to be for every unit of zoom
    private const float ZOOM_ROOM = 1300.0f;

    /// <summary>The zoom the page is drawn at now, <see cref="Zoom"/> or less on a page too narrow for it.</summary>
    public float ZoomNow => zoom;

    // The zoom the page is drawn at worked out again from how wide it is, built again when that changes it
    private void FitZoom()
    {
        float fit = Bounds.Width > 0.0f ? Math.Clamp(Bounds.Width / ZOOM_ROOM, 1.0f, zoomAsked) : zoomAsked;
        if (MathF.Abs(fit - zoom) < 0.02f)
            return;
        zoom = fit;
        dirty = true;
    }

    // The page's sizes at its zoom
    private float NavWidth => NAV_WIDTH * zoom;
    private float SearchHeight => SEARCH_HEIGHT * zoom;
    private float ControlHeight => CONTROL_HEIGHT * zoom;

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

    /// <summary>
    /// Reset all settings asked for, off the funnel's menu or a part's right click. The program asks whoever's there
    /// first and calls <see cref="ResetAll"/> if they say yes. Null leaves it off both menus.
    /// </summary>
    public Action? OnResetAllAsked { get; set; }

    /// <summary>
    /// A short note a row has in a pill after its key, null for none. Hex's say a setting is set by the other scope
    /// ("Set by workspace"). Asked again every time a row is shown.
    /// </summary>
    public Func<string, string?>? NoteOf { get; set; }

    /// <summary>What a row's note says when the pointer rests on it, null for nothing.</summary>
    public Func<string, string?>? NoteTipOf { get; set; }

    /// <summary>
    /// The part the page is for (Dawn's Editor, Dusk's Terminal). The first page of a run folds every other part down
    /// the left so they all fit, and a search ranks its rows a little over the rest. Null for none.
    /// </summary>
    public string? HomePart { get; set; }

    /// <summary>Everything the page says of its own, English unless a program gives it its own words.</summary>
    public SettingsTexts Texts
    {
        get => texts;
        set
        {
            texts = value;
            ApplyTexts();
            dirty = true;
        }
    }

    private SettingsTexts texts = SettingsTexts.English;

    // The page's own words put on what says them, as it's made and whenever it's given others
    private void ApplyTexts()
    {
        search.Placeholder = texts.SearchPlaceholder;
        filters.Tooltip = texts.FilterTooltip;
        clear.Tooltip = texts.ClearTooltip;
        close.Tooltip = texts.CloseTooltip;
        nothingClear.Label = texts.ClearSearch;
        if (!titleGiven)
        {
            Title = texts.Title;
            titleGiven = false;
        }
    }

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
            titleGiven = true;
            title.Text = $"[b]{UIRichText.Literal(value)}[/b]";
            title.Visible = value.Length > 0;
        }
    }

    private string titleText = string.Empty;

    // Whether the title was given rather than left as the texts' own, so new texts don't write over one given
    private bool titleGiven;

    /// <summary>The x at the top right pressed (or Escape with nothing in the search), null for a page with no x.</summary>
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
        title = Add(new Label { Align = Origin.Left });
        search = Add(new TextBox { Padding = new UIEdges(10.0f, 6.0f, 10.0f, 6.0f) });
        search.OnChanged = Filter;
        search.OnKey = SearchKey;

        // How many the search found and the x that empties it, both over the right end of the box and only while it has
        // something in it
        found = Add(new Label { Align = Origin.Right, Visible = false });

        // The funnel between them, always there, what narrows a search (@modified, @id:) without having to know the words
        filters = Add(new Button { Icon = "filter", Style = "button_flat", Animated = false, OnPressed = ShowFilters });
        clear = Add(new Button { Icon = "close", Style = "button_flat", Animated = false, Visible = false, OnPressed = ClearSearch });
        close = Add(new Button { Icon = "close", Style = "button_flat", Animated = false, Visible = false, OnPressed = () => onClose?.Invoke() });
        // The sections down the left scroll as well, a few extensions with settings of their own ran them off the bottom
        navScroll = Add(new ScrollPanel { Padding = new UIEdges(0.0f, 6.0f, 2.0f, 6.0f) });
        // No gaps between the sections' rows, a pointer going down them is always on one, the gap is in their pills
        nav = navScroll.Add(new StackPanel { Spacing = 0.0f, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
        scroll = Add(new ScrollPanel());
        rows = scroll.Add(new StackPanel { Spacing = ROW_GAP, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
        nothing = Add(new Label { Align = Origin.Center, Visible = false });
        nothingClear = Add(new Button { Style = "button_flat", Animated = false, Visible = false, OnPressed = ClearSearch });
        ApplyTexts();
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
        {
            row.Show(ValueOf?.Invoke(key), IsModified?.Invoke(key) ?? false);
            row.Dimmed = ParentOff(row.Setting);
        }

        // The rows that hang off it fade or come back with it
        RefreshDependents(key);

        // A section's dot down the left goes by its rows' changes
        navStale = true;
    }

    /// <summary>What a choice's row says in its dropdown and in the line under it, nulls for a key with no such row. For a test.</summary>
    public (string? Label, string? Help) ChoiceShown(string key) =>
        rowOf.TryGetValue(key, out SettingRow? row) ? (row.ChoiceText, row.ChoiceHelpText) : (null, null);

    /// <summary>Where a row is, its name's text and its reset, nulls for a key with no row. For a test of how a row is laid out.</summary>
    public (UIRect Row, UIRect Title, UIRect Reset)? RowBounds(string key) =>
        rowOf.TryGetValue(key, out SettingRow? row) ? (row.Bounds, row.TitleBounds, row.ResetBounds) : null;

    /// <summary>What a row of pairs shows, the line being typed (-1 for none) and its two boxes, null for a key with no such row. For a test.</summary>
    public (IReadOnlyList<(string Key, string Value)> Pairs, int Editing, TextBox? KeyBox, TextBox? ValueBox)? PairsShown(string key) =>
        rowOf.TryGetValue(key, out SettingRow? row) && row.Setting.Kind == UIFormKind.Pairs ? (row.PairsShown, row.EditingPair, row.PairBoxesShown.Key, row.PairBoxesShown.Value) : null;

    /// <summary>Where a row of pairs has Add Item, and every line's pencil and bin, null for a key with no such row. For a test to press with the pointer.</summary>
    public (UIRect AddItem, UIRect[] Edits, UIRect[] Removes)? PairBounds(string key) =>
        rowOf.TryGetValue(key, out SettingRow? row) && row.Setting.Kind == UIFormKind.Pairs ? row.PairBounds : null;

    /// <summary>The width every row is laid out at now, for a test of how the page uses a big window.</summary>
    public float RowWidthNow => rowOf.Values.FirstOrDefault()?.Size.X ?? 0.0f;

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

    /// <summary>
    /// What a right click on a row offers, its reset while it's changed, its key copied and the line the file has for it
    /// copied. Null for a key with no row. The row shows it, a test asks for it.
    /// </summary>
    public Menu? MenuOf(string key)
    {
        if (!rowOf.TryGetValue(key, out SettingRow? row))
            return null;

        var menu = new Menu(row.Setting.Title);
        if (IsModified?.Invoke(key) == true)
        {
            menu.Add(texts.ResetSetting, () => ResetPressed(key), icon: "discard");
            menu.AddSeparator();
        }

        menu.Add(texts.CopyId, () => UIClipboard.Copy(key), icon: "copy");
        if (LineOf(key) is { } line)
            menu.Add(texts.CopyLine, () => UIClipboard.Copy(line), icon: "copy");
        return menu;
    }

    /// <summary>A setting the way its section of the file writes it (<c>wrap_code: true</c>), null while it has no value HIDL can write.</summary>
    public string? LineOf(string key)
    {
        if (ValueOf?.Invoke(key) is not { } value)
            return null;

        try
        {
            return $"{key}: {HIDLWriter.Write(value)}";
        }
        catch (NotSupportedException)
        {
            return null;
        }
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
        scrollBack = null;
        wentTo = group;
        wentToAt = float.NaN;

        // Asked for before the page has been built and laid out (as it opens), gone to once it has
        if (dirty || !headerOf.TryGetValue(group, out Label? header) || header.Bounds.IsEmpty)
        {
            wantedGroup = group;
            return;
        }

        wantedGroup = null;
        scroll.ScrollToTop(header);
        wentToAt = scroll.Offset;
    }

    private string? wantedGroup;

    // The section last gone to and where that left the rows scrolled, NaN while it's still waiting to be. It stays the
    // one lit down the left till the rows are scrolled from there, a section near the end can't come up to the top
    // and the one over it was lit instead
    private string? wentTo;
    private float wentToAt = float.NaN;

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
        if (wentTo is { } went)
        {
            if (headerOf.TryGetValue(went, out Label? wentHeader) && wentHeader.Visible && (float.IsNaN(wentToAt) || MathF.Abs(scroll.Offset - wentToAt) < 0.5f))
            {
                CurrentGroup = went;
                return;
            }

            wentTo = null;
        }

        float top = scroll.Bounds.Max.Y - scroll.Padding.Top;
        string? current = null;

        // Down the page the way they're showing, a search puts the sections it found the most in first
        foreach (var (group, header) in SectionsInOrder())
        {
            if (!header.Visible || header.Bounds.IsEmpty)
                continue;

            current ??= group;
            if (header.Bounds.Max.Y >= top - 12.0f)
                current = group;
        }

        CurrentGroup = current;
    }

    // How much narrower the rows are than the scroll they're in, its bar and the gap before it
    private const float ROWS_INSET = 18.0f;

    // How wide a row is in rows a width wide, all of it up to MAX_ROW
    private float RowWidth(float width) => MathF.Min(MathF.Max(240.0f, width), MAX_ROW * zoom);

    protected override void Update(float dt)
    {
        FitZoom();
        float width = scroll.Bounds.Width - ROWS_INSET;
        if (dirty || width > 0.0f && MathF.Abs(width - builtWidth) > 1.0f)
        {
            dirty = false;
            Build(MathF.Max(240.0f, width > 0.0f ? width : Size.X - NavWidth - GAP * 2.0f));
        }

        // A part asked for while the page was being made, its first section, and a section once its heading is laid out
        if (wantedPart is { } part)
            GoToPart(part);
        if (wantedGroup is { } wanted && headerOf.TryGetValue(wanted, out Label? heading) && !heading.Bounds.IsEmpty)
            GoTo(wanted);

        UpdateReveal();
        ScrollBack();
        FindCurrent();
        UpdateKeys();
        ShowFocused();
        if (navStale)
            UpdateNav();

        base.Update(dt);
    }

    protected override Vector2 Measure(UISkin skin) => Size;

    protected override void Arrange(UIRect content)
    {
        var titleArea = new UIRect(new Vector2(content.Min.X, content.Max.Y - SearchHeight), new Vector2(content.Min.X + NavWidth, content.Max.Y));
        float closeWidth = close.Visible ? SearchHeight + GAP : 0.0f;
        var scrollArea = new UIRect(new Vector2(content.Min.X + NavWidth + GAP, content.Min.Y), new Vector2(content.Max.X, content.Max.Y - SearchHeight - GAP));

        // The search ends where the rows do, so the box, the rows and the controls down their right share one edge. It ran
        // on to the window's edge past rows held to MAX_ROW and the two edges were a hand apart
        float rowsEnd = scrollArea.Min.X + scroll.Padding.Left + RowWidth(scrollArea.Width - ROWS_INSET);
        var searchArea = new UIRect(new Vector2(content.Min.X + NavWidth + GAP, content.Max.Y - SearchHeight), new Vector2(MathF.Min(content.Max.X - GAP - closeWidth, rowsEnd), content.Max.Y));
        title.ArrangeTree(titleArea);
        close.ArrangeTree(new UIRect(new Vector2(content.Max.X - SearchHeight, content.Max.Y - SearchHeight), content.Max));
        // With no title the sections go up to the top, beside the search
        float navTop = title.Visible ? content.Max.Y - SearchHeight - GAP : content.Max.Y;
        var navArea = new UIRect(new Vector2(content.Min.X, content.Min.Y), new Vector2(content.Min.X + NavWidth, navTop));
        search.ArrangeTree(searchArea);
        ArrangeSearchEnd(searchArea);

        // What a search that found nothing says, in the middle of where the rows would be
        var rowsArea = new UIRect(scrollArea.Min, new Vector2(MathF.Min(scrollArea.Max.X, rowsEnd), scrollArea.Max.Y));
        nothing.ArrangeTree(new UIRect(new Vector2(rowsArea.Min.X, rowsArea.Max.Y - 100.0f), new Vector2(rowsArea.Max.X, rowsArea.Max.Y - 40.0f)));
        float middle = (rowsArea.Min.X + rowsArea.Max.X) * 0.5f;
        nothingClear.ArrangeTree(new UIRect(new Vector2(middle - 90.0f, scrollArea.Max.Y - 136.0f), new Vector2(middle + 90.0f, scrollArea.Max.Y - 136.0f + ControlHeight)));
        navScroll.ArrangeTree(navArea);
        scroll.ArrangeTree(scrollArea);
    }

    /// <summary>Whether it covers what is under it with the skin's panel colour, for a page over the whole of a window.</summary>
    public bool Opaque { get; set; } = true;

    protected override void Paint(UIDrawList list)
    {
        // The sections down the left sit straight on the window's own background, no well or line of their own. A lighter
        // panel behind them looked like a list box dropped on the window
        if (Opaque)
            list.Rect(Bounds, list.Skin.PanelColor with { W = 1.0f });
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
            titleSize *= zoom;
            headingSize *= zoom;
            nameSize *= zoom;
            sentenceSize *= zoom;
        }

        title.TextScale = titleSize;

        // The search as big as the rows' sentences once the page is zoomed, the skin's own size otherwise as it was
        search.TextScale = zoom > 1.0f ? sentenceSize : 0.0f;
        foreach (UIComponent child in rows.Children.ToArray())
            rows.Remove(child);
        foreach (UIComponent child in nav.Children.ToArray())
            nav.Remove(child);
        rowOf.Clear();
        headerOf.Clear();
        navParts.Clear();
        shownParts = navParts;
        built.Clear();
        float rowWidth = RowWidth(width);

        // the rows of a section together, the sections in the order they first come, the panels' after them
        var groups = settings.Select(setting => setting.Group).Concat(panels.Select(panel => panel.Group)).Distinct().ToList();
        if (GroupOrder is { } order)
            groups = [.. groups.Select((group, at) => (group, at)).OrderBy(pair => order(pair.group)).ThenBy(pair => pair.at).Select(pair => pair.group)];
        foreach (string group in groups)
        {
            string named = group.Length == 0 ? texts.General : group;
            int dot = named.IndexOf(" · ", StringComparison.Ordinal);
            string top = dot < 0 ? named : named[..dot], under = dot < 0 ? named : named[(dot + 3)..];

            // The section big and the part it's of quietly in front of it
            string heading = dot < 0 ? $"[b]{named}[/b]" : $"[color:dim]{top} ·[/color] [b]{under}[/b]";
            var header = headerOf[group] = rows.Add(new Label { Text = heading, TextScale = headingSize, Align = Origin.Left, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Size = new Vector2(rowWidth, 44.0f * zoom) });
            var section = new BuiltSection(group, header);
            built.Add(section);

            AddNavLink(top, under, group);

            foreach (UISetting setting in settings)
            {
                if (setting.Group == group)
                    section.Rows.Add(rowOf[setting.Key] = rows.Add(new SettingRow(this, setting, rowWidth)));
            }

            foreach (var (of, content) in panels)
            {
                if (of != group)
                    continue;
                content.Parent?.Remove(content);
                content.Anchor = Origin.TopLeft;
                content.Pivot = Origin.TopLeft;
                section.Panels.Add(rows.Add(content));
            }
        }

        FoldForFirstOpen();
        Refresh();
        ranked = null;
        Filter(search.Text);
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

    // the sizes it was last built at, the skin's by what each text is for unless it was given scales
    private float titleSize, headingSize, nameSize, sentenceSize;

    internal float RowTitleScale => nameSize;
    internal float RowBodyScale => sentenceSize;
    internal float RowControlHeight => ControlHeight;
}
