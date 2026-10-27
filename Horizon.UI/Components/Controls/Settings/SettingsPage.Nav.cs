using System.Collections.Concurrent;
using System.Numerics;

using Horizon.Rendering;
using Horizon.UI.Drawing;

namespace Horizon.UI.Components;

// The sections down the left of a settings page, a click scrolls to one. While a search is typed they're the ones it
// found something in with how many, the rest of the time a section with a change in it has a dot of the accent. The
// keyboard goes down them as well, and a right click resets every change in one. The parts they're under (Editor,
// Terminal) are a tree's headings, bold with a chevron, a click folds a part's links away and the count beside one is
// how many changes it has (or how many rows a search found in it). The part headings are in SettingsPage.NavParts
public partial class SettingsPage
{
    // How tall a section's link is, and the gap over a part's heading, smaller over the first one at the very top. The
    // gap has the thin rule over a part in the middle of it
    private const float LINK_HEIGHT = 30.0f, PART_GAP = 18.0f, FIRST_PART_GAP = 2.0f;

    // A link as tall as the page is zoomed
    private float LinkHeight => LINK_HEIGHT * zoom;

    // The parts down the left (Terminal, Editor), each its heading and the links of its sections, so a search can hide
    // the ones it found nothing in and a heading with all of its sections gone
    private readonly List<(string Part, PartHeading Heading, List<SectionLink> Links)> navParts = [];

    // Whether the links need telling again what's found and what's changed, after a search or a row shown afresh
    private bool navStale = true;

    // The parts folded away, by name, for as long as the program runs, so a part folded stays folded the next time the
    // settings open. Every page shares them, a window closed and opened again is a new page
    private static readonly ConcurrentDictionary<string, bool> collapsedParts = new(StringComparer.Ordinal);

    // What was typed the last time the parts with something found were opened, null while nothing is typed. A part
    // folded while a search is up stays folded till the search changes
    private string? expandedFor;

    // The parts a search opened that were folded, folded again once it's emptied. One folded or opened by hand meanwhile
    // is left the way it was put
    private readonly HashSet<string> searchOpened = new(StringComparer.Ordinal);

    // A section's link down the left, under the heading of the part it's of, that heading first if it's a new part
    private void AddNavLink(string top, string under, string group)
    {
        // The part a section is of once, a heading of the tree in bold at about the size of a row's name with a chevron
        // at its right, its sections under it. Its words sit in a band as tall as a link with the gap over that, the
        // same rhythm as the links under it, and the gap over every part but the first has a thin rule across it
        if (navParts.Count == 0 || navParts[^1].Part != top)
        {
            bool first = nav.Children.Count == 0;
            float gap = first ? FIRST_PART_GAP : PART_GAP;
            var heading = nav.Add(new PartHeading(this, top, first, gap)
            {
                // A row's name is often the size the links are, a breath bigger than both so the parts read as headings
                TextScale = MathF.Max(nameSize, sentenceSize * 1.1f),
                Size = new Vector2(NavWidth - 12.0f, gap + LinkHeight),
                Anchor = Origin.Left,
                Pivot = Origin.Left
            });
            navParts.Add((top, heading, []));
        }

        var link = nav.Add(new SectionLink(this, group, under, top) { TextScale = sentenceSize, Align = Origin.Left, Size = new Vector2(NavWidth - 12.0f, LinkHeight), Padding = new UIEdges(10.0f, 0.0f, 0.0f, 0.0f), Anchor = Origin.Left, Pivot = Origin.Left });
        navParts[^1].Links.Add(link);
    }

    // Each link told how many rows of its section the search left (hidden when none) or whether its section has a
    // change, hidden as well while its part is folded. Each heading told how many changes its part has, or how many rows
    // the search found in it
    private void UpdateNav()
    {
        navStale = false;
        var shown = new Dictionary<string, int>(StringComparer.Ordinal);
        var changed = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (SettingRow row in rowOf.Values)
        {
            string group = row.Setting.Group;
            if (row.Visible)
                shown[group] = shown.GetValueOrDefault(group) + 1;
            if (row.Modified)
                changed[group] = changed.GetValueOrDefault(group) + 1;
        }

        // A panel the search found shows its section, with nothing to count
        foreach (var (group, content) in panels)
        {
            if (content.Visible)
                shown.TryAdd(group, 0);
        }

        // A search typed afresh opens every part it found something in, a folded part with hits in it wasn't any use.
        // Only while it's up, emptied the parts it opened fold back the way they were
        bool fresh = false;
        if (!searching)
        {
            foreach (string part in searchOpened)
                collapsedParts[part] = true;
            searchOpened.Clear();
            expandedFor = null;
        }
        else if (search.Text != expandedFor)
        {
            expandedFor = search.Text;
            fresh = true;
        }

        // Commonly used stays at the top while nothing's typed or while it's what the search is, a search for words has
        // its own sections down the left
        if (commonLink is not null)
            commonLink.Visible = !searching || commonShown;

        foreach (var (part, heading, links) in navParts)
        {
            bool any = false;
            int hits = 0, changes = 0;
            foreach (SectionLink link in links)
            {
                any |= !searching || shown.ContainsKey(link.Group);
                hits += shown.GetValueOrDefault(link.Group);
                changes += changed.GetValueOrDefault(link.Group);
            }

            if (fresh && any && collapsedParts.TryRemove(part, out _))
                searchOpened.Add(part);

            bool open = !IsPartCollapsed(part);
            foreach (SectionLink link in links)
            {
                link.Visible = open && (!searching || shown.ContainsKey(link.Group));
                link.Count = searching ? shown.GetValueOrDefault(link.Group) : 0;
                link.Changed = !searching && changed.ContainsKey(link.Group);
            }

            heading.Visible = any;
            heading.Collapsed = !open;
            heading.Show(searching ? hits : changes, searching);
        }

        OrderNav();
    }

    // The parts and their links in the order they're down the left, the order they were made in until a search puts the
    // ones with its best rows first. The keyboard goes down them in this order too
    private List<(string Part, PartHeading Heading, List<SectionLink> Links)> shownParts = [];

    // While a search is typed the parts go by the best section in each and the links in a part by their own section's
    // place, the way the rows are ranked, so what's at the top of the rows is at the top of the left too. VS Code's
    // table of contents does the same. Emptied it's the tree as it was made
    private void OrderNav()
    {
        List<(string Part, PartHeading Heading, List<SectionLink> Links)> order;
        if (searching && ranked is { } best)
        {
            var place = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < best.Count; i++)
                place[best[i].Group] = i;

            int Rank(SectionLink link) => place.TryGetValue(link.Group, out int at) ? at : int.MaxValue;
            int Best(List<SectionLink> links) => links.Where(link => link.Visible || link.Count > 0).Select(Rank).DefaultIfEmpty(int.MaxValue).Min();

            order = [.. navParts.Select((part, at) => (part, at))
                .OrderBy(pair => Best(pair.part.Links)).ThenBy(pair => pair.at)
                .Select(pair => (pair.part.Part, pair.part.Heading, (List<SectionLink>)[.. pair.part.Links.Select((link, at) => (link, at)).OrderBy(each => Rank(each.link)).ThenBy(each => each.at).Select(each => each.link)]))];
        }
        else
        {
            order = navParts;
        }

        shownParts = order;
        int index = 0;

        // Commonly used over everything, the first part then has its rule over it to keep the two apart
        bool firstShown = commonLink is not { Visible: true };
        if (commonLink is not null)
            Place(commonLink);
        foreach (var (_, heading, links) in order)
        {
            Place(heading);
            heading.First = heading.Visible && firstShown;
            firstShown &= !heading.Visible;
            foreach (SectionLink link in links)
                Place(link);
        }

        void Place(UIComponent child)
        {
            IReadOnlyList<UIComponent> children = nav.Children;
            if (index >= children.Count || !ReferenceEquals(children[index], child))
                nav.MoveChild(child, index);
            index++;
        }
    }

    /// <summary>The sections down the left that are showing, by their whole names, for a test. A folded part's aren't.</summary>
    public IReadOnlyList<string> SectionsShown => [.. VisibleLinks().Select(link => link.Group)];

    /// <summary>The sections down the left that are showing in the order they're shown, each with how many rows a search left in it (0 with none typed), for a test.</summary>
    public IReadOnlyList<(string Group, int Count)> SectionCounts => [.. VisibleLinks().Select(link => (link.Group, link.Count))];

    /// <summary>The sections down the left that have a dot for a change in them, for a test.</summary>
    public IReadOnlyList<string> SectionsChanged => [.. VisibleLinks().Where(link => link.Changed).Select(link => link.Group)];

    private IEnumerable<SectionLink> VisibleLinks() => shownParts.SelectMany(part => part.Links).Where(link => link.Visible);

    /// <summary>
    /// What a right click on a section down the left offers, every change in it reset in one go while it has some. Empty
    /// for a section with nothing changed. The link shows it, a test asks for it.
    /// </summary>
    public Menu SectionMenu(string group)
    {
        string named = group.Length == 0 ? texts.General : group;
        int dot = named.IndexOf(" · ", StringComparison.Ordinal);
        string under = dot < 0 ? named : named[(dot + 3)..];

        var menu = new Menu(under);
        string[] changed = [.. rowOf.Values.Where(row => row.Setting.Group == group && IsModified?.Invoke(row.Setting.Key) == true).Select(row => row.Setting.Key)];
        if (changed.Length > 0)
        {
            menu.Add(SettingsTexts.Counted(texts.ResetSectionOne, texts.ResetSectionMany, changed.Length, ("section", under)), () =>
            {
                foreach (string key in changed)
                    ResetPressed(key);
            }, icon: "discard");
        }

        return menu;
    }

    /// <summary>
    /// What a right click on a part's heading down the left offers, every change in the part reset in one go while it has
    /// some, and every change on the page while a program asks first (<see cref="OnResetAllAsked"/>). Empty for a part
    /// with nothing changed and nobody to ask. The heading shows it, a test asks for it.
    /// </summary>
    public Menu PartMenu(string part)
    {
        var menu = new Menu(part);
        int changes = ChangedKeys(part).Length;
        if (changes > 0)
            menu.Add(SettingsTexts.Counted(texts.ResetPartOne, texts.ResetPartMany, changes, ("part", part)), () => ResetPart(part), icon: "discard");
        AddResetAll(menu);
        return menu;
    }

    /// <summary>Every change in a part (Editor, Terminal) put back to what it comes as, how many that was.</summary>
    public int ResetPart(string part)
    {
        string[] changed = ChangedKeys(part);
        foreach (string key in changed)
            ResetPressed(key);
        return changed.Length;
    }

    /// <summary>Every change on the page put back to what it comes as, how many that was.</summary>
    public int ResetAll()
    {
        string[] changed = ChangedKeys(null);
        foreach (string key in changed)
            ResetPressed(key);
        return changed.Length;
    }

    // The keys of the rows that aren't what they come as, in one part or in all of them for null
    private string[] ChangedKeys(string? part) =>
        [.. rowOf.Values.Where(row => (part is null || PartOf(row.Setting.Group, texts.General) == part) && IsModified?.Invoke(row.Setting.Key) == true).Select(row => row.Setting.Key)];

    /// <summary>The part a section is in, what its name says before the dot ("Editor" of "Editor · Text"), General for none.</summary>
    public static string PartOf(string group) => PartOf(group, SettingsTexts.English.General);

    // The part a section is in, what a section with no name is called given
    private static string PartOf(string group, string general)
    {
        string named = group.Length == 0 ? general : group;
        int dot = named.IndexOf(" · ", StringComparison.Ordinal);
        return dot < 0 ? named : named[..dot];
    }

    // The link Tab stops on, the one lit or the first showing when that's hidden, so Tab goes over the sections in one
    // step rather than thirty and Shift+Tab from the first row comes back to them
    private bool IsNavStop(SectionLink link)
    {
        if (!link.Visible || CommonIsStop)
            return false;

        SectionLink? lit = null, first = null;
        foreach (SectionLink each in VisibleLinks())
        {
            first ??= each;
            if (each.Group == CurrentGroup)
                lit = each;
        }

        return link == (lit ?? first);
    }

    // What the keyboard goes along down the left, a part's heading then its links that are showing
    private List<UIComponent> NavStops()
    {
        var stops = new List<UIComponent>();
        if (commonLink is { Visible: true })
            stops.Add(commonLink);
        foreach (var (_, heading, links) in shownParts)
        {
            if (!heading.Visible)
                continue;
            stops.Add(heading);
            stops.AddRange(links.Where(link => link.Visible));
        }

        return stops;
    }

    // The keyboard on the heading or link a step down (or up) from one, the ends of the list stopping it
    private void MoveLink(UIComponent from, int delta)
    {
        var stops = NavStops();
        int at = stops.IndexOf(from);
        if (at < 0 || stops.Count == 0)
            return;

        stops[Math.Clamp(at + delta, 0, stops.Count - 1)].Focus();
    }

    // The links' ends, Home and End. Home goes up to Commonly used while it's there
    private void LinkAtEnd(bool last)
    {
        if (!last && commonLink is { Visible: true })
        {
            commonLink.Focus();
            return;
        }

        var links = VisibleLinks().ToList();
        if (links.Count > 0)
            (last ? links[^1] : links[0]).Focus();
    }

    /// <summary>A section's name down the left, a click scrolls to it.</summary>
    private sealed class SectionLink : Label
    {
        private readonly SettingsPage page;
        private readonly string name;
        private int count;

        // How far the pointer's pill has come on, eased in and out over the hover's time so a pointer going down the list
        // leaves a little wake rather than pills blinking on and off
        private float lit;

        public SectionLink(SettingsPage page, string group, string name, string part)
        {
            this.page = page;
            this.name = name;
            Group = group;
            Part = part;
            Text = name;

            // A right click resets every change in it, nothing comes up for a section with none
            OnContextMenu = point =>
            {
                if (Module is { } module && page.SectionMenu(Group) is { Items.Count: > 0 } menu)
                    ContextMenu.Show(module, point, menu);
            };
        }

        /// <summary>The section's whole name, its group.</summary>
        public string Group { get; }

        /// <summary>The part it's under, the heading over it.</summary>
        public string Part { get; }

        /// <summary>How many rows of it a search left, written dim after its name, 0 for nothing written.</summary>
        public int Count
        {
            get => count;
            set
            {
                if (value == count)
                    return;
                count = value;
                Text = value > 0 ? $"{name}  [color:dim]{value}[/color]" : name;
            }
        }

        /// <summary>Whether a setting in it isn't what it comes as, a dot of the accent at its right.</summary>
        public bool Changed { get; set; }

        protected override bool HitTestVisible => true;

        // A click gives it the keyboard as well, Up and Down go on down the list from there
        protected internal override bool Focusable => true;

        protected internal override bool TabStop => IsFocused || page.IsNavStop(this);

        protected internal override void OnClick() => page.GoTo(Group);

        // Up and Down along the links and headings, Enter or Space (or Right) into the section's first row, Left folds
        // the part it's in with the keyboard going up to its heading, Home and End to the ends and / to the search
        protected internal override void OnTextInput(char character)
        {
            switch (character)
            {
                case UIKeyboard.UP or UIKeyboard.DOWN:
                    page.MoveLink(this, character == UIKeyboard.DOWN ? 1 : -1);
                    break;
                case UIKeyboard.HOME or UIKeyboard.END:
                    page.LinkAtEnd(character == UIKeyboard.END);
                    break;
                case UIKeyboard.ENTER or ' ' or UIKeyboard.RIGHT:
                    page.EnterSection(Group);
                    break;
                case UIKeyboard.LEFT:
                    page.FoldFromLink(this);
                    break;
                case '/':
                    page.FocusSearch();
                    break;
            }
        }

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
            bool current = page.CurrentGroup == Group;
            float hover = lit * (2.0f - lit);
            UIRect pill = Bounds.Shrink(new UIEdges(0.0f, 1.0f));
            list.Box(pill, list.Skin.TextColor with { W = current ? 0.1f : 0.05f * hover });

            // The open one has a bar of the text's colour down its left as well, the activity bar's, so it can't be taken
            // for the one the pointer happens to be on. The two pills alone were two rows lit near enough the same
            Vector4 bar = list.Skin.TextColor with { W = current ? 0.85f : 0.0f };
            list.Rect(new UIRect(new Vector2(pill.Min.X, pill.Min.Y + 4.0f), new Vector2(pill.Min.X + 2.0f, pill.Max.Y - 4.0f)), bar);

            // The ring of the keyboard round the pill, inside it so the sidebar's edge doesn't cut it off, there and
            // see-through without the keyboard for the same reason
            Vector4 ring = list.Skin.Palette.Highlight;
            list.RoundOutline(pill, list.Skin.CornerRadius, 1.5f, ring with { W = IsFocused ? ring.W : 0.0f });

            // The dot for a change in it at the pill's right, there and see-through without one for the same reason
            const float DOT = 3.0f;
            var centre = new Vector2(Bounds.Max.X - 12.0f, Bounds.Center.Y);
            Vector4 accent = list.Skin.AccentColor;
            list.RoundRect(new UIRect(centre - new Vector2(DOT), centre + new Vector2(DOT)), DOT, accent with { W = Changed ? accent.W : 0.0f });
            base.Paint(list);
        }
    }
}
