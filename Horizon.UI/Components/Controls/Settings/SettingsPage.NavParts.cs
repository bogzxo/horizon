using System.Numerics;

using Horizon.Rendering;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

// The parts down the left of a settings page (Editor, Terminal) as the headings of a tree. Bold at about the size of a
// row's name with a chevron, a thin rule over every one but the first, a click (or Enter or Space) folds its links away
// and back. Folding only hides links, the rows stay where they are. Left folds and Right opens with the keyboard on a
// heading or on one of its links
public partial class SettingsPage
{
    /// <summary>Whether a part's links are folded away down the left, by its name. It lasts as long as the program does.</summary>
    public static bool IsPartCollapsed(string part) => collapsedParts.ContainsKey(part);

    /// <summary>A part's links folded away down the left (or opened again), as a click on its heading does.</summary>
    public void SetPartCollapsed(string part, bool collapsed)
    {
        searchOpened.Remove(part);
        if (collapsed)
            collapsedParts[part] = true;
        else
            collapsedParts.TryRemove(part, out _);

        // Told at once rather than the next update, a test asks straight after
        UpdateNav();
    }

    /// <summary>A part folded if it's open and opened if it's folded, what a click on its heading does.</summary>
    public void TogglePart(string part) => SetPartCollapsed(part, !IsPartCollapsed(part));

    // Whether a page with a part of its own has been made this run. The first one folds every other part, the sections
    // of all of them ran off the bottom and Extensions was nowhere to be seen. After that the folding is whoever's
    private static bool foldedOnce;

    // The parts but the page's own folded, the first time a page with one is made in a run
    private void FoldForFirstOpen()
    {
        if (foldedOnce || HomePart is not { } home || navParts.Count == 0)
            return;

        foldedOnce = true;
        foreach (var (part, _, _) in navParts)
        {
            if (part != home)
                collapsedParts[part] = true;
        }
    }

    /// <summary>
    /// Every part open again and the next page with a part of its own folding the others, the way a run starts. For a
    /// test of the first time the settings open.
    /// </summary>
    public static void ForgetFolding()
    {
        foldedOnce = false;
        collapsedParts.Clear();
    }

    /// <summary>The page scrolled to the first section of a part, the one it's for as it opens. Asked for before it's built, once it is.</summary>
    public void GoToPart(string part)
    {
        wantedPart = null;
        if (dirty || built.Count == 0)
        {
            wantedPart = part;
            return;
        }

        if (built.Find(section => PartOf(section.Group, texts.General) == part) is { } first)
            GoTo(first.Group);
    }

    // A part asked to be gone to before the page was built
    private string? wantedPart;

    /// <summary>Where a part's heading is down the left, empty while it isn't showing. For a test of the parts fitting.</summary>
    public UIRect PartBounds(string part) => navParts.Find(each => each.Part == part).Heading is { Visible: true } heading ? heading.Bounds : default;

    /// <summary>What of the sections down the left is in sight, the rest scrolls. For a test of the parts fitting.</summary>
    public UIRect NavViewport => navScroll.Bounds.Shrink(navScroll.Padding);

    /// <summary>What a part's heading has beside it, how many changes it has or how many rows a search found in it, 0 for nothing. For a test.</summary>
    public int PartCount(string part) => navParts.Find(each => each.Part == part).Heading?.Count ?? 0;

    /// <summary>The parts down the left whose heading is showing, for a test.</summary>
    public IReadOnlyList<string> PartsShown => [.. shownParts.Where(each => each.Heading.Visible).Select(each => each.Part)];

    /// <summary>The part whose heading has the keyboard, null when none has. For a test.</summary>
    public string? FocusedPart => Module?.Compositor.Focus is PartHeading heading && heading.Parent == nav ? heading.Part : null;

    /// <summary>The keyboard given to a part's heading down the left, false when it isn't showing.</summary>
    public bool FocusPart(string part)
    {
        if (navParts.Find(each => each.Part == part).Heading is not { Visible: true } heading)
            return false;

        heading.Focus();
        return true;
    }

    // Left on a link, its part folded and the keyboard up on the heading, the link it was on has gone
    private void FoldFromLink(SectionLink link)
    {
        SetPartCollapsed(link.Part, true);
        FocusPart(link.Part);
    }

    // The heading Tab stops on, only while no link is showing to stop on (every part folded), the first one then
    private bool IsHeadingStop(PartHeading heading) =>
        heading.Visible && commonLink is not { Visible: true } && !VisibleLinks().Any() && shownParts.FirstOrDefault(each => each.Heading.Visible).Heading == heading;

    /* Commonly used, the entry over every part */

    // The entry at the top down the left that leaves only the commonly used, null while the page has none of them
    private CommonLink? commonLink;

    // Commonly used made at the top of the sections, only when one of the keys it's given has a row
    private void AddCommonLink()
    {
        commonLink = null;
        if (!common.Any(key => settings.Any(setting => setting.Key == key)))
            return;

        commonLink = nav.Add(new CommonLink(this)
        {
            Text = texts.CommonlyUsed,
            Bold = true,
            TextScale = MathF.Max(nameSize, sentenceSize * 1.1f),
            Align = Origin.Left,
            Size = new Vector2(NavWidth - 12.0f, LinkHeight),
            Anchor = Origin.Left,
            Pivot = Origin.Left
        });
    }

    // Whether Tab stops on Commonly used rather than a section, while it's what the search is or there's no section to stop on
    private bool CommonIsStop => commonLink is { Visible: true } && (commonShown || !VisibleLinks().Any());

    /// <summary>
    /// The rows down to the commonly used (<see cref="Common"/>) in their order, @common in the search in place of what
    /// was typed, what a click on Commonly used does. False while the page has none.
    /// </summary>
    public bool ShowCommon()
    {
        if (commonLink is null)
            return false;

        Find(COMMON + " ");
        navScroll.Offset = 0.0f;
        return true;
    }

    /// <summary>Whether Commonly used is the first thing showing down the left. For a test.</summary>
    public bool CommonFirst => commonLink is { Visible: true } && nav.Children.FirstOrDefault(child => child.Visible) == commonLink;

    /// <summary>Where Commonly used is down the left, empty while it isn't showing. For a test to click.</summary>
    public UIRect CommonBounds => commonLink is { Visible: true } link ? link.Bounds : default;

    /// <summary>What Commonly used says, null while the page has none. For a test.</summary>
    public string? CommonText => commonLink?.Text;

    /// <summary>Whether Commonly used has the keyboard. For a test.</summary>
    public bool CommonFocused => commonLink is not null && Module?.Compositor.Focus == commonLink;

    /// <summary>
    /// Commonly used down the left, the first of the sections and lit while the search is @common. A click (or Enter or
    /// Space) leaves only those rows, Up and Down go on along the sections from it the way they do from a link
    /// </summary>
    private sealed class CommonLink(SettingsPage page) : Label
    {
        private float lit;

        protected override bool HitTestVisible => true;

        protected internal override bool Focusable => true;

        protected internal override bool TabStop => IsFocused || page.CommonIsStop;

        protected internal override void OnClick() => page.ShowCommon();

        // Enter or Space (or Right) shows them and goes into the first of them, the way a section's link goes into it
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
                    page.ShowCommon();
                    page.FocusFirstRow();
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

        // The pill a section's link has, lit while @common is the search, the pointer's fainter one and the keyboard's
        // ring. Always there and see-through when not wanted (ToggleButton has why)
        protected override void Paint(UIDrawList list)
        {
            float hover = lit * (2.0f - lit);
            UIRect pill = Bounds.Shrink(new UIEdges(0.0f, 1.0f));
            list.Box(pill, list.Skin.TextColor with { W = page.commonShown ? 0.1f : 0.05f * hover });
            Vector4 ring = list.Skin.Palette.Highlight;
            list.RoundOutline(pill, list.Skin.CornerRadius, 1.5f, ring with { W = IsFocused ? ring.W : 0.0f });
            base.Paint(list);
        }
    }

    /// <summary>How big a part's heading is written and how big a section's link, for a test of the headings standing out.</summary>
    public (float Part, float Section) NavScales =>
        (navParts.FirstOrDefault().Heading?.TextScale ?? 0.0f, navParts.FirstOrDefault().Links?.FirstOrDefault()?.TextScale ?? 0.0f);

    /// <summary>
    /// A part's heading down the left, a click folds its links away and back. It writes its own words rather than being a
    /// label, a label puts its text against its very edges whatever its padding, and the words sit under the gap here
    /// </summary>
    private sealed class PartHeading : UIComponent
    {
        private readonly SettingsPage page;
        private bool first;
        private readonly float gap;
        private int count;
        private bool searched;
        private float lit;

        public PartHeading(SettingsPage page, string part, bool first, float gap)
        {
            this.page = page;
            this.first = first;
            this.gap = gap;
            Part = part;

            // A right click resets every change in the part, nothing comes up for one with none
            OnContextMenu = point =>
            {
                if (Module is { } module && page.PartMenu(Part) is { Items.Count: > 0 } menu)
                    ContextMenu.Show(module, point, menu);
            };
        }

        /// <summary>Whether it's the heading at the top, with no rule over it. A search can put another part first.</summary>
        public bool First
        {
            get => first;
            set => first = value;
        }

        /// <summary>The part's name, what its sections say before their dot.</summary>
        public string Part { get; }

        /// <summary>How big its words are, 0 for the skin's.</summary>
        public float TextScale { get; set; }

        /// <summary>Whether its links are folded away, which way its chevron points.</summary>
        public bool Collapsed { get; set; }

        /// <summary>What's written beside its name, 0 for nothing.</summary>
        public int Count => count;

        // How many changes it has, written in the accent the sections' dots are, or how many rows a search found in it,
        // written dim
        public void Show(int value, bool searching) => (count, searched) = (value, searching);

        protected override bool HitTestVisible => true;

        // A click gives it the keyboard as well, Up and Down go on along the links from there
        protected internal override bool Focusable => true;

        protected internal override bool TabStop => IsFocused || page.IsHeadingStop(this);

        protected internal override void OnClick() => page.TogglePart(Part);

        // Up and Down along the headings and links, Enter or Space folds it or opens it, Left folds and Right opens, /
        // to the search
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
                case UIKeyboard.ENTER or ' ':
                    page.TogglePart(Part);
                    break;
                case UIKeyboard.LEFT or UIKeyboard.RIGHT:
                    page.SetPartCollapsed(Part, character == UIKeyboard.LEFT);
                    break;
                case '/':
                    page.FocusSearch();
                    break;
            }
        }

        protected override Vector2 Measure(UISkin skin) => Size;

        protected override void Update(float dt)
        {
            float time = MotionTime(DEFAULT_HOVER_TIME);
            float step = time > 0.0f ? dt / time : 1.0f;
            lit = IsHovered ? MathF.Min(1.0f, lit + step) : MathF.Max(0.0f, lit - step);
            base.Update(dt);
        }

        protected override void Paint(UIDrawList list)
        {
            UISkin skin = list.Skin;
            Vector4 text = skin.TextColor;

            // The thin rule across the gap over it, none over the first part at the very top. Always there and
            // see-through when not wanted, or the quads after it shift (ToggleButton has why)
            float ruleY = Bounds.Max.Y - gap * 0.5f;
            list.Rect(new UIRect(new Vector2(Bounds.Min.X + 6.0f, ruleY - 0.5f), new Vector2(Bounds.Max.X - 6.0f, ruleY + 0.5f)), text with { W = first ? 0.0f : 0.14f });

            // The band its words are in, under the gap, a faint pill under the pointer and the keyboard's ring round it
            UIRect band = Bounds.Shrink(new UIEdges(0.0f, gap, 0.0f, 0.0f));
            UIRect pill = band.Shrink(new UIEdges(0.0f, 1.0f));
            float hover = lit * (2.0f - lit);
            list.Box(pill, text with { W = 0.05f * hover });
            Vector4 ring = skin.Palette.Highlight;
            list.RoundOutline(pill, skin.CornerRadius, 1.5f, ring with { W = IsFocused ? ring.W : 0.0f });

            // Its name in bold where a link's starts, the count after it a little smaller
            float scale = TextScale > 0.0f ? TextScale : skin.TextScale;
            float height = skin.Font.LineHeight * scale;
            float width = skin.Font.MeasureLine(Part, scale, UITextStyle.Bold, default);
            var at = new Vector2(band.Min.X, band.Center.Y + height * 0.5f);
            list.Text(Part, at, scale, text, false, UITextStyle.Bold, default);
            if (count > 0)
            {
                float small = scale * 0.85f;
                Vector4 accent = skin.AccentColor;
                list.Text(count.ToString(System.Globalization.CultureInfo.InvariantCulture), new Vector2(at.X + width + 8.0f, band.Center.Y + skin.Font.LineHeight * small * 0.5f), small,
                    searched ? skin.DimTextColor : accent, false);
            }

            // The chevron at the right end where the links have their dots, down while it's open and across while it's
            // folded, the way a tree's go
            float side = MathF.Min(14.0f, band.Height * 0.5f);
            var centre = new Vector2(Bounds.Max.X - 12.0f, band.Center.Y);
            list.Symbol(Collapsed ? "chevron-right" : "chevron-down", UIRect.FromCenter(centre, new Vector2(side)), skin.DimTextColor);
        }
    }
}
