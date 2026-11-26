using System.Numerics;

using Horizon.Core.Processes;
using Horizon.Core.Tweening;
using Horizon.Engine;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>One tab of a <see cref="TerminalPanel"/>, a tree of terminals split up however it was.</summary>
public sealed class TerminalTab
{
    /// <summary>The terminals in it and how they share the room.</summary>
    public SplitTree<TerminalView> Tree { get; } = new();

    /// <summary>The pane the keyboard goes to, null while the tab is empty.</summary>
    public SplitNode<TerminalView>? Focused { get; internal set; }

    /// <summary>
    /// What the tab is called when nobody renamed it, the title the program set or the folder its shell is in (the
    /// profile's name before it's in one). A title that says nothing of its own counts as none, see
    /// <see cref="TerminalView.SaysNothing"/>.
    /// </summary>
    public string Title => Renamed ?? (Focused?.Content is { Title: { Length: > 0 } said } view && !TerminalView.SaysNothing(said, view.Profile) ? said : Focused?.Content?.FolderTitle ?? Profile?.Name ?? "terminal");

    /// <summary>What somebody renamed it to, null for the title the program or its profile gives it.</summary>
    public string? Renamed { get; set; }

    /// <summary>The profile the tab was opened with, its icon is the tab's.</summary>
    public TerminalProfile? Profile { get; internal set; }

    /// <summary>Whatever the application wants to keep with it.</summary>
    public object? Tag { get; set; }

    /// <summary>The terminal with the keyboard, null while the tab is empty.</summary>
    public TerminalView? FocusedView => Focused?.Content;

    /// <summary>Every terminal in it, left to right and top to bottom.</summary>
    public IEnumerable<TerminalView> Views => Tree.Leaves.Select(leaf => leaf.Content!);

    // The leaves as a list, read again whenever the tree changes, so a tick walks no iterator
    internal readonly List<SplitNode<TerminalView>> Leaves = [];

    // Bumped whenever the tree structure changes, so panel per-tick checks skip unchanged trees
    internal int TreeVersion;

    /// <summary>The pane over the whole of the tab, null while none is (see <see cref="TerminalPanel.ToggleZoom"/>).</summary>
    public SplitNode<TerminalView>? Zoomed { get; internal set; }

    /// <summary>What its tab in the strip says of it, worked out every tick from its panes, see <see cref="TerminalTabState"/>.</summary>
    public TerminalTabState State { get; internal set; }

    // The bells its panes had rung when it was last open, more than these while it isn't is a bell nobody saw
    internal int BellsSeen;

    internal void Refresh()
    {
        Leaves.Clear();
        Leaves.AddRange(Tree.Leaves);
        TreeVersion++;

        // A split or a pane closed takes the zoom off, what was zoomed is part of a different tree now
        Zoomed = null;
    }
}

/// <summary>
/// Tabs of terminals and splits inside a tab, the panel of Dusk and of Hex's terminal page. A strip of tabs along the
/// top with a plus at its end, under it the terminals of the tab that is open, split side by side and one over the other
/// as deep as anybody likes (a <see cref="SplitTree{T}"/>), the one with the keyboard framed in the accent, the edges
/// between them dragged with the engine's resizable edges. A new tab or split starts a <see cref="TerminalProfile"/>,
/// the first of <see cref="Profiles"/> unless told which, the plus lists them when there is more than one. Keys the
/// application wants for itself (its tabs and splits) come through <see cref="OnKey"/> before the shell sees them.
/// Everything that moves is a tween of <see cref="UIComponent.Tweens"/>, a split sliding open, a tab's room sliding
/// over as another is opened, a tab folding away as it closes, all of it <see cref="AnimationTime"/> long and off at 0.
/// <code><![CDATA[
/// var panel = module.AddComponent(new TerminalPanel { Fill = UIFill.Both });
/// panel.Profiles.AddRange(TerminalProfiles.Discover());
/// panel.NewTab();
/// panel.OnKey = press => press.Control && press.Shift && press.Key == Key.T && panel.NewTab() is not null;
/// ]]></code>
/// </summary>
public partial class TerminalPanel : UIComponent, IDisposable
{
    /// <summary>How long the animations take when nobody says, in seconds.</summary>
    public const float DEFAULT_ANIMATION_TIME = 0.16f;

    private const float GAP = 4.0f, FRAME = 1.5f, SLIDE_DISTANCE = 28.0f;

    // How much of the background is laid over a pane that hasn't the keyboard, while a tab is split
    private const float UNFOCUSED_DIM = 0.28f;

    // How much of the text colour the hairline round a pane that is a card is
    private const float PANE_EDGE = 0.08f;

    private readonly TabStrip strip;
    private readonly Button add;
    private readonly Button profilePicker;
    private readonly Room room;
    private readonly List<TerminalTab> tabs = [];
    private TerminalTab? current;
    private TerminalView? focusedShown;
    private bool stripDirty = true;

    /// <summary>The tabs, in their order.</summary>
    public IReadOnlyList<TerminalTab> Tabs => tabs;

    /// <summary>The tab that is open, null while there is none.</summary>
    public TerminalTab? Current => current;

    public int CurrentIndex => current is null ? -1 : tabs.IndexOf(current);

    /// <summary>Where a tab is in the strip, -1 for one that isn't here.</summary>
    public int IndexOf(TerminalTab tab) => tabs.IndexOf(tab);

    /// <summary>The terminal with the keyboard in the open tab, null while there is none.</summary>
    public TerminalView? FocusedView => current?.FocusedView;

    /// <summary>The shells a tab or a split can be opened with, the first is the usual one. Fill it with <see cref="TerminalProfiles.Discover()"/>.</summary>
    public List<TerminalProfile> Profiles { get; } = [];

    /// <summary>The profile a new tab gets when nobody picked one, the first of <see cref="Profiles"/> unless set.</summary>
    public TerminalProfile? DefaultProfile { get; set; }

    /// <summary>Where a new terminal starts, null for the process's own folder. The focused terminal's own folder isn't known, a shell doesn't say.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// The font every terminal writes in, null for the skin's. A monospaced one. Set on the terminals there are as well:
    /// one made before it was set (a task's, or the first shell, opened before the font was loaded) kept the skin's
    /// proportional font, its columns every width but a cell's.
    /// </summary>
    public UIFont? Font
    {
        get => font;
        set
        {
            font = value;
            foreach (TerminalTab tab in tabs)
            {
                foreach (TerminalView view in tab.Views)
                    view.Font = value;
            }

            RequestFrame();
        }
    }

    private UIFont? font;

    /// <summary>The scale of the terminals' text, 0 for the skin's.</summary>
    public float TextScale { get; set; }

    /// <summary>The colours of every terminal, handed to each as it is made and when this is set again (<see cref="ApplyPalette"/>).</summary>
    public TerminalPalette Palette { get; set; } = new();

    /// <summary>Where the panes are, under the tabs, for a program that draws round them (Dusk's field of light).</summary>
    public UIRect PanesBounds => room.Bounds;

    /// <summary>What is painted behind the row of tabs, null for nothing (the window shows through).</summary>
    public Vector4? StripColor { get; set; }

    /// <summary>
    /// How round the panes are, each a card of its own on what's behind the panel with the gaps between them showing it.
    /// 0 for square panes on the panel's own dark, the way they were.
    /// </summary>
    public float PaneRadius
    {
        get => paneRadius;
        set
        {
            paneRadius = value;
            foreach (TerminalTab tab in tabs)
            {
                foreach (TerminalView view in tab.Views)
                    view.Radius = value;
            }
        }
    }

    private float paneRadius;

    /// <summary>
    /// Whether every pane has its title in a header along its top, one on its own as well, so the text starts in the same
    /// place split or not. Off, only a tab split into more than one has them.
    /// </summary>
    public bool PaneTitlesAlways { get; set; }

    /// <summary>The room round the panes inside the panel, under the row of tabs, for panes that float as cards.</summary>
    public UIEdges RoomPadding { get; set; }

    /// <summary>How many rows every terminal keeps of what went off the top.</summary>
    public int Scrollback { get; set; } = 5000;

    /// <summary>How long a split sliding open, a tab sliding over or folding away takes, 0 for none of it.</summary>
    public float AnimationTime { get; set; } = DEFAULT_ANIMATION_TIME;

    /// <summary>Whether the terminals' own small movements (the cursor's glide) are on as well. Off along with <see cref="AnimationTime"/> at 0.</summary>
    public bool Animated => AnimationTime > 0.0f;

    /// <summary>How long every terminal's cursor takes to glide to its next cell, 0 for it jumping there, see <see cref="TerminalView.CursorGlide"/>.</summary>
    public float CursorGlide { get; set; } = TerminalView.DEFAULT_CURSOR_GLIDE;

    /// <summary>Whether every terminal has its rows drawn small down its right, see <see cref="TerminalView.Minimap"/>. Set on the terminals there are as well.</summary>
    public bool Minimap
    {
        get => minimap;
        set
        {
            minimap = value;
            foreach (TerminalTab tab in Tabs)
            {
                foreach (TerminalView view in tab.Views)
                    view.Minimap = value;
            }

            RequestFrame();
        }
    }

    private bool minimap = true;

    /// <summary>The least a pane can be dragged or squeezed to, along the split's axis.</summary>
    public float MinPane { get; set; } = 48.0f;

    /// <summary>
    /// A key pressed in any of the terminals, before the shell gets it. True to keep it from the shell, which is
    /// how an application puts its own keys (a new tab, a split) over the terminals.
    /// </summary>
    public Func<UIKeyPress, bool>? OnKey { get; set; }

    /// <summary>
    /// Asked to start a profile that isn't a process (<see cref="TerminalProfileKind.Embedded"/>, the REPL) in a terminal.
    /// False when it can't, in which case the terminal stays empty.
    /// </summary>
    public Func<TerminalProfile, TerminalView, bool>? OnStartProfile { get; set; }

    /// <summary>Called when a tab is opened, closed, renamed or another one is selected, for the window's title.</summary>
    public Action? OnChanged { get; set; }

    /// <summary>Called when the keyboard moves to another terminal.</summary>
    public Action<TerminalView?>? OnFocusChanged { get; set; }

    /// <summary>The command line a profile is really started with, its own when this is null, the shell integration's for the host.</summary>
    public Func<TerminalProfile, string>? CommandOf { get; set; }

    /// <summary>What a profile's process gets on top of the machine's environment, null for nothing more.</summary>
    public Func<TerminalProfile, IReadOnlyDictionary<string, string>?>? EnvironmentOf { get; set; }

    /// <summary>Called with every terminal as it is made, before it is started, for whatever the application wants on each (a history, a link handler).</summary>
    public Action<TerminalView>? OnViewMade { get; set; }

    /// <summary>A right click on a tab, with the tab and where, for a menu of the application's. Nothing comes up otherwise.</summary>
    public Action<TerminalTab, Vector2>? OnTabMenu { get; set; }

    /// <summary>A terminal whose process ended, with the view and the code, before the pane is closed. Return false to keep the pane.</summary>
    public Func<TerminalView, int, bool>? OnExited { get; set; }

    /// <summary>
    /// Whether a pane closes by itself when its shell ends well, as a terminal does. One that ended with an error stays
    /// whatever this says, with what it wrote and how it ended, and Enter starts it again (Windows Terminal's "graceful").
    /// A shell that failed to start, or crashed, went with the tab and nobody saw why.
    /// </summary>
    public bool CloseOnExit { get; set; } = true;

    /// <summary>What an ended pane says of its code, "Exited with code 1", see <see cref="TerminalView.EndedWords"/>. Null for the engine's words.</summary>
    public Func<int, string>? EndedWords { get; set; }

    /// <summary>What an ended pane says starts it again, see <see cref="TerminalView.RestartHint"/>.</summary>
    public string RestartHint { get; set; } = "Press Enter to restart";

    protected override bool HitTestVisible => true;

    public TerminalPanel()
    {
        Size = new Vector2(800, 500);
        strip = Add(new TabStrip());
        // Only its icon until the pointer finds it, a box at the end of the tabs looked like a tab that wasn't one
        add = Add(new Button { Icon = "add", Tooltip = "New terminal with the default profile", Style = "button_flat", Animated = false });
        profilePicker = Add(new Button { Icon = "chevron-down", Tooltip = "New terminal with profile...", Style = "button_flat", Animated = false, Visible = false, OnPressed = ShowProfiles });
        BuildOverflow();
        room = Add(new Room(this));

        strip.OnSelected = index => Select(index);
        strip.OnMoved = TabDragged;
        strip.OnClose = index =>
        {
            if (index >= 0 && index < tabs.Count)
                CloseTab(tabs[index]);
        };
        strip.OnContextMenu = point =>
        {
            int index = strip.TabAt(point);
            if (index >= 0 && index < tabs.Count)
                OnTabMenu?.Invoke(tabs[index], point);
        };
        add.OnPressed = AddPressed;
        add.OnContextMenu = _ => ShowProfiles();
    }

    private void AddPressed()
    {
        NewTab();
    }

    /// <summary>The profile dropdown beside New Tab; empty when only one profile is available.</summary>
    public UIRect ProfilePickerBounds => profilePicker.Visible ? profilePicker.Bounds : default;

    /// <summary>The profiles as a menu at the plus, a pick opens a tab of that.</summary>
    public void ShowProfiles()
    {
        if (Module is not { } module || Profiles.Count == 0)
        {
            NewTab();
            return;
        }

        var menu = new Menu("profiles");
        foreach (TerminalProfile profile in Profiles)
        {
            TerminalProfile picked = profile;
            menu.Add(profile.Name, () => NewTab(picked), icon: profile.Icon);
        }

        Button anchor = profilePicker.Visible ? profilePicker : add;
        ContextMenu.Show(module, new Vector2(anchor.Bounds.Min.X, anchor.Bounds.Min.Y), menu);
    }

    /* Tabs */

    /// <summary>Opens a tab with a terminal of a profile (the usual one when none is given) in it, selects it and gives it the keyboard.</summary>
    public TerminalTab NewTab(TerminalProfile? profile = null, string? workingDirectory = null)
    {
        profile ??= DefaultProfile ?? Profiles.FirstOrDefault();
        var tab = new TerminalTab { Profile = profile };
        TerminalView view = MakeView(profile);
        tab.Focused = tab.Tree.Add(view);
        tab.Refresh();
        tabs.Add(tab);
        Select(tab);
        StartIn(view, profile, workingDirectory);
        return tab;
    }

    /// <summary>
    /// Opens a tab on a process that's running already (a console Windows handed over to the terminal), selects it and
    /// gives it the keyboard. It has no profile, its title is what the program says or else what it's called.
    /// </summary>
    public TerminalTab AdoptTab(PseudoTerminal running, string? workingDirectory = null)
    {
        var tab = new TerminalTab();
        TerminalView view = MakeView(null);
        tab.Focused = tab.Tree.Add(view);
        tab.Refresh();
        tabs.Add(tab);
        Select(tab);
        view.Attach(running, workingDirectory ?? WorkingDirectory);
        return tab;
    }

    /// <summary>Closes a tab, its shells ended. Folds away first when the animations are on.</summary>
    public void CloseTab(TerminalTab tab)
    {
        if (!tabs.Contains(tab) || closing.Contains(tab))
            return;

        // The shells go now, the picture of them folds away
        foreach (TerminalView view in tab.Views)
            view.Dispose();

        closing.Add(tab);
        if (tab != current || !Animated)
        {
            DropTab(tab);
            return;
        }

        room.Tweens.Play(Tween.To(() => room.VisualScale, value => room.VisualScale = value, new Vector2(1.0f, 0.0f), AnimationTime).SetEasing(Easing.InCubic), SLIDE);
        room.Tweens.Play(Tween.To(() => room.Opacity, value => room.Opacity = value, 0.0f, AnimationTime).SetEasing(Easing.InCubic).OnComplete(() => DropTab(tab)), FADE);
    }

    private readonly HashSet<TerminalTab> closing = [];
    private static readonly object SLIDE = "slide", FADE = "fade";

    private void DropTab(TerminalTab tab)
    {
        closing.Remove(tab);
        int index = tabs.IndexOf(tab);
        if (index < 0)
            return;

        foreach (TerminalView view in tab.Views)
            room.Remove(view);

        tabs.RemoveAt(index);
        if (current == tab)
        {
            current = null;
            room.VisualScale = Vector2.One;
            room.Opacity = 1.0f;
            Select(tabs.Count == 0 ? null : tabs[Math.Min(index, tabs.Count - 1)], from: 1);
        }

        stripDirty = true;
        OnChanged?.Invoke();
    }

    /// <summary>Brings a tab up, its room sliding in from the side of the tab that was up.</summary>
    public void Select(TerminalTab? tab)
    {
        int from = tab is null || current is null ? 0 : Math.Sign(tabs.IndexOf(tab) - tabs.IndexOf(current));
        Select(tab, from);
    }

    public void Select(int index) => Select(index >= 0 && index < tabs.Count ? tabs[index] : null);

    private void Select(TerminalTab? tab, int from)
    {
        if (tab == current)
            return;

        if (current is not null)
        {
            foreach (TerminalView view in current.Views)
                view.Visible = false;
        }

        current = tab;
        if (tab is not null)
        {
            ShowPanes(tab);

            tab.FocusedView?.Focus();

            if (Animated && from != 0)
            {
                room.VisualOffset = new Vector2(from * SLIDE_DISTANCE, 0.0f);
                room.Opacity = 0.0f;
                room.VisualScale = Vector2.One;
                room.Tweens.Play(Tween.To(() => room.VisualOffset, value => room.VisualOffset = value, Vector2.Zero, AnimationTime).SetEasing(Easing.OutCubic), SLIDE);
                room.Tweens.Play(Tween.To(() => room.Opacity, value => room.Opacity = value, 1.0f, AnimationTime).SetEasing(Easing.OutCubic), FADE);
            }
        }

        stripDirty = true;
        OnChanged?.Invoke();
        RequestFrame();
    }

    /// <summary>The tab after the open one, round to the first past the last.</summary>
    public void NextTab() => StepTab(1);

    public void PreviousTab() => StepTab(-1);

    private void StepTab(int by)
    {
        if (tabs.Count < 2 || current is null)
            return;

        int index = (tabs.IndexOf(current) + by + tabs.Count) % tabs.Count;
        Select(tabs[index], by);
    }

    /// <summary>Puts a tab somewhere else in the strip, 0 for first.</summary>
    public void MoveTab(TerminalTab tab, int index)
    {
        if (!tabs.Remove(tab))
            return;

        tabs.Insert(Math.Clamp(index, 0, tabs.Count), tab);
        stripDirty = true;
        OnChanged?.Invoke();
    }

    /// <summary>A tab dragged along the strip, the tabs put in the strip's new order.</summary>
    private void TabDragged(int from, int to)
    {
        if (from < 0 || from >= tabs.Count || to < 0 || to >= tabs.Count)
            return;
        TerminalTab tab = tabs[from];
        tabs.RemoveAt(from);
        tabs.Insert(to, tab);
        stripDirty = true;
        OnChanged?.Invoke();
    }

    /// <summary>The open tab moved along the strip by a step, -1 to the left.</summary>
    public void MoveCurrent(int by)
    {
        if (current is { } tab)
            MoveTab(tab, tabs.IndexOf(tab) + by);
    }

    /// <summary>Renames a pane, null or empty for the title the program gives it.</summary>
    public void RenamePane(TerminalView view, string? name)
    {
        view.PaneName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        OnChanged?.Invoke();
        RequestFrame();
    }

    /// <summary>Two panes of a tab swapped, each taking the other's place, the keyboard staying with the one it was with.</summary>
    public void SwapPanes(TerminalView a, TerminalView b)
    {
        if (TabOf(a) is not { } tab || tab != TabOf(b) || tab.Tree.Find(a) is not { } first || tab.Tree.Find(b) is not { } second)
            return;

        TerminalView? focused = tab.FocusedView;
        tab.Tree.Swap(first, second);
        tab.Refresh();
        ShowPanes(tab);
        if (focused is not null && tab.Tree.Find(focused) is { } leaf)
            FocusPane(tab, leaf);
        OnChanged?.Invoke();
        RequestFrame();
    }

    /// <summary>The focused pane swapped with the one next to it in a direction, false when there is none that way.</summary>
    public bool SwapNeighbour(SplitDirection direction)
    {
        if (current is not { Focused: { } leaf } tab || tab.Tree.Neighbour(leaf, direction) is not { Content: { } other })
            return false;

        SwapPanes(leaf.Content!, other);
        return true;
    }

    // A pane's badge let go of over another pane of its tab
    private void PaneDropped(TerminalView view, Vector2 point)
    {
        // Nothing to swap with while one pane has the whole tab, the others are out of sight under it
        if (TabOf(view) is not { Zoomed: null } tab)
            return;

        var leaves = tab.Leaves;
        for (int i = 0; i < leaves.Count; i++)
        {
            if (leaves[i].Content != view && leaves[i].Bounds.Contains(point))
            {
                SwapPanes(view, leaves[i].Content!);
                return;
            }
        }
    }

    /// <summary>Renames a tab, null or empty for the title the program gives it.</summary>
    public void Rename(TerminalTab tab, string? title)
    {
        tab.Renamed = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        stripDirty = true;
        OnChanged?.Invoke();
    }

    /* Splits */

    /// <summary>
    /// Splits the focused pane of the open tab, a new terminal of a profile (the pane's own when none is given) on its
    /// right or under it, sliding open when the animations are on. Null when there is no tab to split.
    /// </summary>
    public TerminalView? Split(SplitOrientation orientation, TerminalProfile? profile = null, string? workingDirectory = null)
    {
        if (current is not { Focused: { } leaf } tab)
            return null;

        profile ??= leaf.Content!.Profile ?? tab.Profile ?? DefaultProfile ?? Profiles.FirstOrDefault();

        // The new pane starts where the one split is, the folder its shell said it's in, when that's a folder here (a
        // shell in WSL says its own sort of path). It started where Dusk did, every split a cd away from the work
        if (workingDirectory is null && leaf.Content!.WorkingDirectory is { } there && Directory.Exists(there))
            workingDirectory = there;

        TerminalView view = MakeView(profile);
        view.Visible = tab == current;
        bool slide = Animated;
        SplitNode<TerminalView> added = tab.Tree.Split(leaf, orientation, view, share: slide ? 0.0f : 0.5f);
        tab.Refresh();
        SplitNode<TerminalView> split = added.Parent!;
        FocusPane(tab, added);

        if (!slide)
        {
            StartIn(view, profile, workingDirectory);
            return view;
        }

        // The newcomer is the second half, the ratio of the first comes down from all of it to half
        split.Animating = true;
        Tweens.Play(Tween.To(() => split.Ratio, value => split.Ratio = value, 0.5f, AnimationTime)
            .SetEasing(Easing.OutCubic)
            .OnComplete(() =>
            {
                split.Animating = false;
                StartIn(view, profile, workingDirectory);
            }), split);
        return view;
    }

    /// <summary>Closes a pane (the focused one when none is given), its shell ended, the room going to its neighbour. A tab's last pane closes the tab.</summary>
    public void ClosePane(TerminalView? view = null)
    {
        TerminalTab? tab = view is null ? current : TabOf(view);
        if (tab is null)
            return;

        SplitNode<TerminalView>? leaf = view is null ? tab.Focused : tab.Tree.Find(view);
        if (leaf is null)
            return;

        if (tab.Tree.Count == 1)
        {
            CloseTab(tab);
            return;
        }

        leaf.Content!.Dispose();
        SplitNode<TerminalView> split = leaf.Parent!;
        bool first = ReferenceEquals(split.First, leaf);
        var sibling = first ? split.Second! : split.First!;

        // The keyboard goes to the nearest pane on the other side before this one is gone
        if (tab.Focused == leaf)
            FocusPane(tab, sibling.Leaves().Last());

        if (!Animated)
        {
            DropPane(tab, leaf);
            return;
        }

        split.Animating = true;
        Tweens.Play(Tween.To(() => split.Ratio, value => split.Ratio = value, first ? 0.0f : 1.0f, AnimationTime)
            .SetEasing(Easing.InCubic)
            .OnComplete(() =>
            {
                split.Animating = false;
                DropPane(tab, leaf);
            }), split);
    }

    private void DropPane(TerminalTab tab, SplitNode<TerminalView> leaf)
    {
        TerminalView view = leaf.Content!;
        if (tab.Tree.Remove(leaf))
            room.Remove(view);
        tab.Refresh();

        if (tab.Tree.IsEmpty)
        {
            CloseTab(tab);
            return;
        }

        if (tab.Focused == leaf || tab.Focused is null)
            FocusPane(tab, tab.Tree.Leaves.Last());

        OnChanged?.Invoke();
    }

    /// <summary>Moves the keyboard to the pane next to the focused one in a direction, if there is one there.</summary>
    public bool FocusNeighbour(SplitDirection direction)
    {
        if (current is not { Focused: { } leaf } tab || tab.Tree.Neighbour(leaf, direction) is not { } next)
            return false;

        FocusPane(tab, next);
        return true;
    }

    /// <summary>Gives a pane the keyboard.</summary>
    public void FocusPane(TerminalView view)
    {
        if (TabOf(view) is { } tab && tab.Tree.Find(view) is { } leaf)
        {
            if (tab != current)
                Select(tab);
            FocusPane(tab, leaf);
        }
    }

    private void FocusPane(TerminalTab tab, SplitNode<TerminalView> leaf)
    {
        tab.Focused = leaf;
        if (tab == current)
            leaf.Content!.Focus();
        RequestFrame();
    }

    /// <summary>The tab a terminal is in, null for one that isn't here.</summary>
    public TerminalTab? TabOf(TerminalView view) => tabs.Find(tab => tab.Tree.Find(view) is not null);

    /// <summary>Hands the palette to every terminal there is, after it was changed.</summary>
    public void ApplyPalette()
    {
        foreach (TerminalTab tab in tabs)
        {
            foreach (TerminalView view in tab.Views)
                view.Palette = Palette;
        }

        RequestFrame();
    }

    private TerminalView MakeView(TerminalProfile? profile)
    {
        var view = new TerminalView
        {
            Font = Font,
            TextScale = TextScale,
            Palette = Palette,
            Scrollback = Scrollback,
            Animated = Animated,
            CursorGlide = CursorGlide,
            Minimap = Minimap,
            Profile = profile,
            Radius = paneRadius,
            Visible = false
        };
        view.Intercept = press => OnKey?.Invoke(press) ?? false;
        view.OnResized = size => Dragged(view, size);
        view.OnPaneDropped = PaneDropped;
        view.OnExited = code => Exited(view, code);
        view.EndedWords = EndedWords;
        view.RestartHint = RestartHint;
        view.OnRestart = view => Restart(view);
        view.OnTitle = _ =>
        {
            stripDirty = true;
            OnChanged?.Invoke();
        };
        room.Add(view);
        OnViewMade?.Invoke(view);
        return view;
    }

    private void StartIn(TerminalView view, TerminalProfile? profile, string? workingDirectory)
    {
        if (profile is { Kind: TerminalProfileKind.Embedded })
        {
            if (OnStartProfile?.Invoke(profile, view) != true)
                view.Feed($"\u001b[31m{profile.Name} couldn't be started here.\u001b[0m\r\n");
            return;
        }

        try
        {
            string? command = profile is { Command.Length: > 0 } ? CommandOf?.Invoke(profile) ?? profile.Command : null;
            view.Start(command, workingDirectory ?? profile?.WorkingDirectory ?? WorkingDirectory, profile is null ? null : EnvironmentOf?.Invoke(profile));
        }
        catch (Exception e)
        {
            view.Feed($"\u001b[31m{e.Message}\u001b[0m\r\n");
        }
    }

    private void Exited(TerminalView view, int code)
    {
        stripDirty = true;
        if (OnExited?.Invoke(view, code) == false || !CloseOnExit || code != 0)
            return;

        if (TabOf(view) is not null)
            ClosePane(view);
    }

    /// <summary>
    /// An ended pane's profile started in it again, in the folder its shell was last in, the way Enter does it in the pane.
    /// False for a pane that isn't one of this panel's or whose program still runs.
    /// </summary>
    public bool Restart(TerminalView view)
    {
        if (TabOf(view) is null || view.EndedWith is null)
            return false;

        string? folder = view.WorkingDirectory is { } there && Directory.Exists(there) ? there : null;
        StartIn(view, view.Profile, folder);
        stripDirty = true;
        OnChanged?.Invoke();
        RequestFrame();
        return true;
    }

    /// <summary>What a tab's panes come to, the worst of them first: ended badly, ended, rang unseen, running a command.</summary>
    private static TerminalTabState StateOf(TerminalTab tab, bool open)
    {
        var leaves = tab.Leaves;
        bool ended = false, busy = false, failed = false;
        int bells = 0;
        for (int i = 0; i < leaves.Count; i++)
        {
            if (leaves[i].Content is not { } view)
                continue;
            if (view.EndedWith is { } code)
            {
                failed |= code != 0;
                ended = true;
            }

            busy |= view.Screen.CommandRunning;
            bells += view.Bells;
        }

        if (open || bells < tab.BellsSeen)
            tab.BellsSeen = bells;
        return failed ? TerminalTabState.Failed
            : ended ? TerminalTabState.Ended
            : bells > tab.BellsSeen ? TerminalTabState.Rang
            : busy ? TerminalTabState.Busy
            : TerminalTabState.Idle;
    }

    // The skin the strip's colours of the states were taken from, a skin put on takes them again
    private UISkin? stateSkin;

    /* Every tick */

    /// <summary>
    /// A picture drawn faintly behind the terminals, in the bottom right corner of the room the panes of a tab take (one
    /// picture however the tab is split), Dawn's picture behind the code. Null for none, which is what there is unless
    /// somebody sets one. See <see cref="TerminalView.Backdrop"/>.
    /// </summary>
    public Horizon.Graphics.Texture? Backdrop { get; set; }

    /// <summary>How tall the picture is drawn, as a share of the window's height.</summary>
    public float BackdropScale { get; set; } = 0.5f;

    /// <summary>How much of the picture shows through the text.</summary>
    public float BackdropOpacity { get; set; } = 0.2f;

    /// <summary>How far the picture is in from the right edge and up from the bottom one, as shares of the window's width and height.</summary>
    public Vector2 BackdropOffset { get; set; }

    /// <summary>How wide the picture is against its own shape, 1 for the shape it has.</summary>
    public float BackdropStretch { get; set; } = 1.0f;

    // Cached inputs for ShowBackdrop and SetEdges so idle ticks skip redundant tree walks and property writes
    private TerminalTab? backdropTab;
    private int backdropTreeVersion = -1;
    private Horizon.Graphics.Texture? backdropTexture;
    private UIRect backdropArea;
    private float backdropScale, backdropOpacity, backdropStretch;
    private Vector2 backdropOffset;

    private TerminalTab? edgesTab;
    private int edgesTreeVersion = -1;
    private SplitNode<TerminalView>? edgesZoomed;
    private bool edgesTitlesAlways;
    private float edgesMinPane;

    /// <summary>Helper method to hand the picture to the panes of the tab that's showing, with the room they take together.</summary>
    private void ShowBackdrop()
    {
        if (current is not { } tab)
            return;

        UIRect area = default;
        bool first = true;
        var leaves = tab.Leaves;
        for (int i = 0; i < leaves.Count; i++)
        {
            if (leaves[i].Content is not { } view)
                continue;
            area = first ? view.Bounds : new UIRect(Vector2.Min(area.Min, view.Bounds.Min), Vector2.Max(area.Max, view.Bounds.Max));
            first = false;
        }

        if (ReferenceEquals(backdropTab, tab) && backdropTreeVersion == tab.TreeVersion
            && ReferenceEquals(backdropTexture, Backdrop) && backdropArea == area
            && backdropScale == BackdropScale && backdropOpacity == BackdropOpacity
            && backdropOffset == BackdropOffset && backdropStretch == BackdropStretch)
            return;

        backdropTab = tab;
        backdropTreeVersion = tab.TreeVersion;
        backdropTexture = Backdrop;
        backdropArea = area;
        backdropScale = BackdropScale;
        backdropOpacity = BackdropOpacity;
        backdropOffset = BackdropOffset;
        backdropStretch = BackdropStretch;

        for (int i = 0; i < leaves.Count; i++)
        {
            if (leaves[i].Content is not { } view)
                continue;
            view.Backdrop = Backdrop;
            view.BackdropScale = BackdropScale;
            view.BackdropOpacity = BackdropOpacity;
            view.BackdropOffset = BackdropOffset;
            view.BackdropStretch = BackdropStretch;
            view.BackdropArea = area;
        }
    }

    protected override void Update(float dt)
    {
        base.Update(dt);
        bool showPicker = Profiles.Count > 1;
        if (profilePicker.Visible != showPicker)
            profilePicker.Visible = showPicker;

        // Clicking into a pane gives it the keyboard, the tab's focused pane follows that
        if (current is { } tab && Module?.Compositor.Focus is TerminalView focused && focused != tab.FocusedView && tab.Tree.Find(focused) is { } leaf)
        {
            tab.Focused = leaf;
            RequestFrame();
        }

        TerminalView? focusedNow = current?.FocusedView;
        if (focusedNow != focusedShown)
        {
            focusedShown = focusedNow;
            OnFocusChanged?.Invoke(focusedNow);
        }

        if (stripDirty)
            RebuildStrip();
        UpdateOverflow();
        UpdateFind(dt);

        ShowBackdrop();

        // How every tab's panes are, the strip read again when one changed or the skin did
        if (Module?.Compositor.Skin is { } skin && skin != stateSkin)
        {
            stateSkin = skin;
            stripDirty = true;
        }

        for (int i = 0; i < tabs.Count; i++)
        {
            TerminalTabState state = StateOf(tabs[i], tabs[i] == current);
            if (state != tabs[i].State)
            {
                tabs[i].State = state;
                stripDirty = true;
            }
        }

        // A title the program sets changes as it goes, the strip is read again when any does
        for (int i = 0; i < tabs.Count; i++)
        {
            if (i < strip.Tabs.Count && strip.Tabs[i].Title != tabs[i].Title)
            {
                stripDirty = true;
                break;
            }
        }

        // The edges between panes, which the engine's resizable edges drag
        if (current is not null)
            SetEdges(current);
    }

    private void RebuildStrip()
    {
        stripDirty = false;
        while (strip.Tabs.Count > tabs.Count)
            strip.Tabs.RemoveAt(strip.Tabs.Count - 1);
        while (strip.Tabs.Count < tabs.Count)
            strip.Tabs.Add(new StripTab());

        for (int i = 0; i < tabs.Count; i++)
        {
            StripTab shown = strip.Tabs[i];
            TerminalTab tab = tabs[i];
            shown.Title = tab.Title;
            shown.Tag = tab;

            // The profile's icon, or what's become of its panes in its place: a cross in the error's colour for one that
            // ended badly, a tick for one that ended, a bell for one that rang while the tab was shut, and the icon in the
            // accent while a command runs. A state that changes the icon has an icon of its own, the colour never says it alone
            string icon = tab.Profile?.Icon ?? "terminal";
            (shown.Icon, shown.IconColor) = tab.State switch
            {
                TerminalTabState.Failed => ("error", stateSkin?.ErrorColor),
                TerminalTabState.Ended => ("pass", stateSkin?.SuccessColor),
                TerminalTabState.Rang => ("bell-dot", stateSkin?.WarningColor),
                TerminalTabState.Busy => (icon, stateSkin?.AccentColor),
                _ => (icon, (Vector4?)null)
            };
            shown.Tooltip = tab.State is TerminalTabState.Failed or TerminalTabState.Ended && EndedView(tab) is { } ended
                ? ended.EndedSaid
                : tab.Profile?.Command ?? string.Empty;
        }

        strip.Selected = CurrentIndex;
        RequestFrame();
    }

    // The ended pane a tab's tooltip speaks of, one that ended badly before one that ended well
    private static TerminalView? EndedView(TerminalTab tab)
    {
        var leaves = tab.Leaves;
        TerminalView? found = null;
        for (int i = 0; i < leaves.Count; i++)
        {
            if (leaves[i].Content is { EndedWith: { } code } view && (found is null || code != 0))
                found = view;
        }

        return found;
    }

    private void SetEdges(TerminalTab tab)
    {
        if (ReferenceEquals(edgesTab, tab) && edgesTreeVersion == tab.TreeVersion
            && ReferenceEquals(edgesZoomed, tab.Zoomed)
            && edgesTitlesAlways == PaneTitlesAlways && edgesMinPane == MinPane)
            return;

        edgesTab = tab;
        edgesTreeVersion = tab.TreeVersion;
        edgesZoomed = tab.Zoomed;
        edgesTitlesAlways = PaneTitlesAlways;
        edgesMinPane = MinPane;

        var leaves = tab.Leaves;
        for (int i = 0; i < leaves.Count; i++)
        {
            var leaf = leaves[i];
            TerminalView view = leaf.Content!;
            UIResize edges = UIResize.None;
            if (tab.Tree.EdgeOwner(leaf, SplitDirection.Left) is not null) edges |= UIResize.Left;
            if (tab.Tree.EdgeOwner(leaf, SplitDirection.Right) is not null) edges |= UIResize.Right;
            if (tab.Tree.EdgeOwner(leaf, SplitDirection.Up) is not null) edges |= UIResize.Top;
            if (tab.Tree.EdgeOwner(leaf, SplitDirection.Down) is not null) edges |= UIResize.Bottom;
            view.Resizable = tab.Zoomed is null ? edges : UIResize.None;
            view.ShowPaneTitle = PaneTitlesAlways || (leaves.Count > 1 && tab.Zoomed is null);
            view.MinSize = new Vector2(MinPane, MinPane);
        }
    }

    /// <summary>A pane's edge dragged, the ratio of the split that owns that edge moved by as far as the edge went.</summary>
    private void Dragged(TerminalView view, Vector2 size)
    {
        if (TabOf(view) is not { } tab || tab.Tree.Find(view) is not { } leaf)
            return;

        UIResize grip = view.ActiveGrip;
        Vector2 moved = size - leaf.Bounds.Size;

        if ((grip & UIResize.Horizontal) != 0 && moved.X != 0.0f)
        {
            SplitDirection side = (grip & UIResize.Right) != 0 ? SplitDirection.Right : SplitDirection.Left;
            if (tab.Tree.EdgeOwner(leaf, side) is var (split, inFirst))
                Shift(split, inFirst ? moved.X : -moved.X, split.Bounds.Width);
        }

        if ((grip & UIResize.Vertical) != 0 && moved.Y != 0.0f)
        {
            SplitDirection side = (grip & UIResize.Top) != 0 ? SplitDirection.Up : SplitDirection.Down;
            if (tab.Tree.EdgeOwner(leaf, side) is var (split, inFirst))
                Shift(split, inFirst ? moved.Y : -moved.Y, split.Bounds.Height);
        }

        RequestFrame();
    }

    private static void Shift(SplitNode<TerminalView> split, float by, float length)
    {
        float room = length - GAP;
        if (room <= 0.0f)
            return;

        float first = split.First!.Bounds.Width;
        if (split.Orientation == SplitOrientation.Vertical)
            first = split.First.Bounds.Height;
        split.Ratio = Math.Clamp((first + by) / room, 0.0f, 1.0f);
    }

    /* Layout and painting */

    protected override Vector2 Measure(UISkin skin) => Size;

    protected override void Arrange(UIRect content)
    {
        float stripHeight = strip.DesiredSize.Y;
        float profileWidth = profilePicker.Visible ? stripHeight : 0.0f;
        // The chevron that lists the tabs, while they don't all fit, goes between the strip and the plus
        float listing = TabsOverflow ? stripHeight : 0.0f;
        float controls = content.Max.X - profileWidth;
        var stripArea = new UIRect(new Vector2(content.Min.X, content.Max.Y - stripHeight), new Vector2(controls - stripHeight - listing, content.Max.Y));
        var addArea = new UIRect(new Vector2(controls - stripHeight, content.Max.Y - stripHeight), new Vector2(controls, content.Max.Y));
        profilePicker.ArrangeTree(profilePicker.Visible ? new UIRect(new Vector2(controls, content.Max.Y - stripHeight), content.Max).Shrink(new UIEdges(2.0f, 3.0f)) : default);
        more?.ArrangeTree(TabsOverflow ? new UIRect(new Vector2(controls - stripHeight - listing, content.Max.Y - stripHeight), new Vector2(controls - stripHeight, content.Max.Y)) : default);
        float paneTop = content.Max.Y - stripHeight - 2.0f;
        var roomArea = new UIRect(content.Min, new Vector2(content.Max.X, paneTop)).Shrink(RoomPadding);
        strip.ArrangeTree(stripArea);

        // In from the strip's edges as far as a tab's pill is, so the plus lights the shape the tabs do
        add.ArrangeTree(addArea.Shrink(new UIEdges(2.0f, 3.0f)));
        room.ArrangeTree(roomArea);
        ArrangeFind();
    }

    /// <summary>The panes of the open tab laid out by its tree, the others out of sight where they were.</summary>
    private void ArrangeRoom(UIRect content)
    {
        for (int t = 0; t < tabs.Count; t++)
        {
            TerminalTab tab = tabs[t];
            tab.Tree.Layout(content, GAP, MinPane);
            if (tab != current && !closing.Contains(tab))
                continue;

            // A zoomed pane takes the whole room, the others are out of sight under it
            var leaves = tab.Leaves;
            for (int i = 0; i < leaves.Count; i++)
                leaves[i].Content!.ArrangeTree(tab.Zoomed is null ? leaves[i].Bounds : leaves[i] == tab.Zoomed ? content : default);
        }
    }

    /// <summary>The focused pane over the whole of its tab and back, the others hidden under it meanwhile.</summary>
    public void ToggleZoom()
    {
        if (current is not { Focused: { } leaf } tab)
            return;

        tab.Zoomed = tab.Zoomed is null && tab.Tree.Count > 1 ? leaf : null;
        ShowPanes(tab);
        leaf.Content!.Focus();
        OnChanged?.Invoke();
        RequestFrame();
    }

    /// <summary>The panes of a tab shown, all of them or only the zoomed one.</summary>
    private static void ShowPanes(TerminalTab tab)
    {
        var leaves = tab.Leaves;
        for (int i = 0; i < leaves.Count; i++)
            leaves[i].Content!.Visible = tab.Zoomed is null || leaves[i] == tab.Zoomed;
    }

    protected override void Paint(UIDrawList list)
    {
        // The row of tabs tinted when it's asked for, always painted so the quads after it don't shift
        list.Rect(new UIRect(new Vector2(Bounds.Min.X, room.Bounds.Max.Y), Bounds.Max), StripColor ?? Vector4.Zero);

        // The room's dark behind the panes, so the gaps between them read as gaps, nothing when the panes are cards of
        // their own and the gaps show what's behind the panel
        list.Rect(room.Bounds, Palette.Background with { W = paneRadius > 0.0f ? 0.0f : 0.6f });
    }

    protected override void PaintChildren(UIDrawList list)
    {
        base.PaintChildren(list);

        // Panes that are cards have the same quiet hairline round every one of them, one pane or several, the way Dawn's
        // cards have their edge. A lone pane had none and split ones had theirs only while they had the keyboard
        bool split = current is { Focused: { Content.Visible: true }, Tree.Count: > 1, Zoomed: null };
        if (current is { } shown && paneRadius > 0.0f)
        {
            var leaves = shown.Leaves;
            Vector4 edge = Palette.Foreground with { W = PANE_EDGE };
            for (int i = 0; i < leaves.Count; i++)
            {
                if (leaves[i].Content is { Visible: true } view && (!split || leaves[i] != shown.Focused))
                    list.RoundOutline(view.Bounds, paneRadius, 1.0f, edge);
            }
        }

        // Only when there is more than one to tell apart, the panes without the keyboard are dimmed a little, the way
        // iTerm has them, and the one with it gets a hairline of the accent. It was a frame of gold round the whole of it
        if (split && current is { Focused: { } leaf } focusedTab)
        {
            var leaves = focusedTab.Leaves;
            for (int i = 0; i < leaves.Count; i++)
            {
                if (leaves[i] != leaf && leaves[i].Content is { Visible: true })
                    list.RoundRect(leaves[i].Bounds, paneRadius, Palette.Background with { W = UNFOCUSED_DIM });
            }

            Vector4 ink = list.Skin.HighlightColor with { W = leaf.Content!.IsFocused ? 0.35f : 0.15f };
            list.RoundOutline(leaf.Bounds, paneRadius, 1.0f, ink);
        }

        // A pane being dragged by its badge, the pane it would swap with outlined
        if (current is { } tab)
        {
            var leaves = tab.Leaves;
            for (int i = 0; i < leaves.Count; i++)
            {
                if (leaves[i].Content!.PaneDraggedTo is not { } at)
                    continue;
                for (int j = 0; j < leaves.Count; j++)
                {
                    if (j != i && leaves[j].Bounds.Contains(at))
                        list.RoundOutline(leaves[j].Bounds, 0.0f, FRAME * 2.0f, list.Skin.AccentColor with { W = 0.7f });
                }
            }
        }
    }

    private static void RequestFrame() => GameEngine.Instance?.WindowManager.RequestFrames();

    public void Dispose()
    {
        foreach (TerminalTab tab in tabs)
        {
            foreach (TerminalView view in tab.Views)
                view.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("text_scale", () => TextScale, value => TextScale = value);
        Expose("scrollback", () => (float)Scrollback, value => Scrollback = (int)value);
        Expose("minimap", () => Minimap, value => Minimap = value);
        Expose("animation_time", () => AnimationTime, value => AnimationTime = MathF.Max(0.0f, value));
        Expose("min_pane", () => MinPane, value => MinPane = MathF.Max(8.0f, value));
    }

    /// <summary>Where the panes live, laid out by the panel's trees rather than by their own anchors.</summary>
    private sealed class Room(TerminalPanel owner) : Panel
    {
        protected override void Arrange(UIRect content) => owner.ArrangeRoom(content);
    }
}

/// <summary>What a tab's panes come to, which its icon in the strip says, see <see cref="TerminalTab.State"/>.</summary>
public enum TerminalTabState : byte
{
    /// <summary>Its shells at their prompts, or nothing known of them.</summary>
    Idle,

    /// <summary>A command typed at a prompt is running, as its shell's marks say.</summary>
    Busy,

    /// <summary>A program in it rang the bell while the tab wasn't open, until it is.</summary>
    Rang,

    /// <summary>A program in it ended well and its pane stayed.</summary>
    Ended,

    /// <summary>A program in it ended with an error, its pane kept to read.</summary>
    Failed
}
