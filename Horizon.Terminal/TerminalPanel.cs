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

    /// <summary>The pane over the whole of the tab, null while none is (see <see cref="TerminalPanel.ToggleZoom"/>).</summary>
    public SplitNode<TerminalView>? Zoomed { get; internal set; }

    internal void Refresh()
    {
        Leaves.Clear();
        Leaves.AddRange(Tree.Leaves);

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
/// <code>
/// var panel = module.AddComponent(new TerminalPanel { Fill = UIFill.Both });
/// panel.Profiles.AddRange(TerminalProfiles.Discover());
/// panel.NewTab();
/// panel.OnKey = press => press.Control &amp;&amp; press.Shift &amp;&amp; press.Key == Key.T &amp;&amp; panel.NewTab() is not null;
/// </code>
/// </summary>
public class TerminalPanel : UIComponent, IDisposable
{
    /// <summary>How long the animations take when nobody says, in seconds.</summary>
    public const float DEFAULT_ANIMATION_TIME = 0.16f;

    private const float GAP = 4.0f, FRAME = 1.5f, SLIDE_DISTANCE = 28.0f;

    // How much of the background is laid over a pane that hasn't the keyboard, while a tab is split
    private const float UNFOCUSED_DIM = 0.28f;

    private readonly TabStrip strip;
    private readonly Button add;
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

    /// <summary>The font every terminal writes in, null for the skin's. A monospaced one.</summary>
    public UIFont? Font { get; set; }

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

    /// <summary>Whether a pane closes by itself when its shell ends, as a terminal does.</summary>
    public bool CloseOnExit { get; set; } = true;

    protected override bool HitTestVisible => true;

    public TerminalPanel()
    {
        Size = new Vector2(800, 500);
        strip = Add(new TabStrip());
        // Only its icon until the pointer finds it, a box at the end of the tabs looked like a tab that wasn't one
        add = Add(new Button { Icon = "add", Tooltip = "New terminal", Style = "button_flat" });
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
        if (Profiles.Count > 1)
            ShowProfiles();
        else
            NewTab();
    }

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

        ContextMenu.Show(module, new Vector2(add.Bounds.Min.X, add.Bounds.Min.Y), menu);
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
        if (TabOf(view) is not { } tab)
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
            Profile = profile,
            Radius = paneRadius,
            Visible = false
        };
        view.Intercept = press => OnKey?.Invoke(press) ?? false;
        view.OnResized = size => Dragged(view, size);
        view.OnPaneDropped = PaneDropped;
        view.OnExited = code => Exited(view, code);
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
        if (OnExited?.Invoke(view, code) == false || !CloseOnExit)
            return;

        if (TabOf(view) is not null)
            ClosePane(view);
    }

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

    /// <summary>Helper method to hand the picture to the panes of the tab that's showing, with the room they take together.</summary>
    private void ShowBackdrop()
    {
        if (current is not { } tab)
            return;

        UIRect area = default;
        bool first = true;
        foreach (SplitNode<TerminalView> leaf in tab.Leaves)
        {
            if (leaf.Content is not { } view)
                continue;
            area = first ? view.Bounds : new UIRect(Vector2.Min(area.Min, view.Bounds.Min), Vector2.Max(area.Max, view.Bounds.Max));
            first = false;
        }

        foreach (SplitNode<TerminalView> leaf in tab.Leaves)
        {
            if (leaf.Content is not { } view)
                continue;
            view.Backdrop = Backdrop;
            view.BackdropScale = BackdropScale;
            view.BackdropOpacity = BackdropOpacity;
            view.BackdropOffset = BackdropOffset;
            view.BackdropArea = area;
        }
    }

    protected override void Update(float dt)
    {
        base.Update(dt);

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

        ShowBackdrop();

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
            shown.Title = tabs[i].Title;
            shown.Icon = tabs[i].Profile?.Icon ?? "terminal";
            shown.Tooltip = tabs[i].Profile?.Command ?? string.Empty;
            shown.Tag = tabs[i];
        }

        strip.Selected = CurrentIndex;
        RequestFrame();
    }

    private void SetEdges(TerminalTab tab)
    {
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
            view.ShowPaneTitle = leaves.Count > 1 && tab.Zoomed is null;
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
        var stripArea = new UIRect(new Vector2(content.Min.X, content.Max.Y - stripHeight), new Vector2(content.Max.X - stripHeight, content.Max.Y));
        var addArea = new UIRect(new Vector2(content.Max.X - stripHeight, content.Max.Y - stripHeight), content.Max);
        var roomArea = new UIRect(content.Min, new Vector2(content.Max.X, content.Max.Y - stripHeight - 2.0f)).Shrink(RoomPadding);
        strip.ArrangeTree(stripArea);
        add.ArrangeTree(addArea);
        room.ArrangeTree(roomArea);
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

        // Only when there is more than one to tell apart, the panes without the keyboard are dimmed a little, the way
        // iTerm has them, and the one with it gets a hairline of the accent. It was a frame of gold round the whole of it
        if (current is { Focused: { } leaf, Tree.Count: > 1, Zoomed: null } focusedTab && leaf.Content is { Visible: true })
        {
            var leaves = focusedTab.Leaves;
            for (int i = 0; i < leaves.Count; i++)
            {
                if (leaves[i] != leaf && leaves[i].Content is { Visible: true })
                    list.RoundRect(leaves[i].Bounds, paneRadius, Palette.Background with { W = UNFOCUSED_DIM });
            }

            Vector4 ink = list.Skin.HighlightColor with { W = leaf.Content.IsFocused ? 0.35f : 0.15f };
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
        Expose("animation_time", () => AnimationTime, value => AnimationTime = MathF.Max(0.0f, value));
        Expose("min_pane", () => MinPane, value => MinPane = MathF.Max(8.0f, value));
    }

    /// <summary>Where the panes live, laid out by the panel's trees rather than by their own anchors.</summary>
    private sealed class Room(TerminalPanel owner) : Panel
    {
        protected override void Arrange(UIRect content) => owner.ArrangeRoom(content);
    }
}
