using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.UI;

namespace Horizon.UI.Components;

/// <summary>
/// A program's settings in one window, every section of them in it and one search over all of them (Hex's, in both its
/// modes, how it looks, the editor's, the terminal's and the extensions'). A <see cref="SettingsPage"/> over the sources
/// given, which say what each section has and what a change does, in a window of the UI's own that is dragged about by
/// its title bar and made bigger by its corner. Hex used to have two, a page over the whole of the window and the old
/// window of what the page couldn't hold, and nobody could find anything in either.
/// </summary>
public sealed class SettingsWindow
{
    // What it starts at before it knows how big what it's in is, and the most it's sized to then
    private const float WIDTH = 1040.0f, HEIGHT = 700.0f, MOST_WIDTH = 1500.0f, MOST_HEIGHT = 1000.0f;

    // How much of what it's in it takes as it opens, across and down
    private static readonly Vector2 SHARE = new(0.72f, 0.78f);

    private readonly List<ISettingsSource> sources;

    /// <summary>The page, for a test to drive.</summary>
    public SettingsPage Page { get; }

    /// <summary>The window the page is in, for a mode to put where it wants it.</summary>
    public WindowPanel Window { get; }

    /// <param name="parts">
    /// The parts of the window in the order they're shown, what a setting's group says before its dot, whichever source
    /// it came from. A part not named goes after them, in the order it came.
    /// </param>
    /// <param name="sections">
    /// Sections by their whole names in the order they're shown inside their part, the rest of a part's after them in the
    /// order they came. A section that's only a panel (Hex's profiles) can come first this way, a panel's section went
    /// after every section that had settings.
    /// </param>
    /// <param name="texts">What the window and its page say of their own, English for null.</param>
    public SettingsWindow(IEnumerable<ISettingsSource> sources, Action close, IReadOnlyList<string>? parts = null, float titleScale = 0.0f, float bodyScale = 0.0f,
        IReadOnlyList<string>? sections = null, SettingsTexts? texts = null)
    {
        this.sources = [.. sources];
        this.parts = parts ?? [];
        this.sections = sections ?? [];
        texts ??= SettingsTexts.English;
        Page = new SettingsPage
        {
            Texts = texts,
            Title = string.Empty,
            TitleScale = titleScale,
            BodyScale = bodyScale,
            Opaque = false,
            Fill = UIFill.Both,
            Settings = Ordered(this.sources.SelectMany(source => source.Settings)),
            ValueOf = key => SourceOf(key)?.Get(key),
            IsModified = key => SourceOf(key)?.IsModified(key) ?? false,
            OnChanged = (key, value) => SourceOf(key)?.Set(key, value),
            OnReset = key => SourceOf(key)?.Reset(key),
            DefaultOf = key => SourceOf(key)?.Default(key),
            OnButton = (key, which) => SourceOf(key)?.Press(key, which),

            // Escape closes it, the window has the x
            OnClose = close,
            GroupOrder = OrderOf
        };
        Page.HideClose = true;

        // A strip along the bottom the page leaves to the grip in the window's corner, or the page took the drag
        Window = new SizedWindow(texts.Title)
        {
            Padding = new UIEdges(0.0f, 0.0f, 0.0f, 16.0f),
            Icon = "settings-gear",
            Size = new Vector2(WIDTH, HEIGHT),
            MinSize = new Vector2(640.0f, 420.0f),
            Lift = 28.0f,
            OnClosed = close,
            Ticked = KeepRowKey,
            Zoom = () => Page.ZoomNow
        };
        Window.Add(Page);

        // A wheel over a part of the window with no use for it (the title bar, the search) goes down the rows
        Window.WheelTarget = Page;
    }

    /// <summary>
    /// The key of the row the keyboard was in last, kept while the keyboard is nowhere (a click on the title bar takes it
    /// from the row), null once it went somewhere else in the page (the search). What a button of the title bar goes by.
    /// </summary>
    public string? RowKey { get; private set; }

    private void KeepRowKey()
    {
        if (Page.KeyWithKeyboard is { } key)
        {
            RowKey = key;
            return;
        }

        // In the page but on no row, the search say, it's about no row any more
        for (UIComponent? at = Page.Module?.Compositor.Focus; at is not null; at = at.Parent)
        {
            if (at == Page)
            {
                RowKey = null;
                return;
            }
        }
    }

    /// <summary>A flat button on the window's title bar left of its x, see <see cref="WindowPanel.AddTitleAction"/>.</summary>
    public void AddTitleAction(string icon, string tooltip, Action pressed) => Window.AddTitleAction(icon, tooltip, pressed);

    /// <summary>Which section of the file a setting is written in, by its key, null for a key no source has.</summary>
    public string? SectionOf(string key) => SourceOf(key)?.Section;

    // The parts of the window in the order they're shown, whichever source a setting came from (Dusk's theme is the
    // terminal's and goes with Dawn's under how Hex looks), and the sections named inside them
    private readonly IReadOnlyList<string> parts, sections;

    // Where a section goes, by its part and then by where it's named, the ones not named after those of their part
    private int OrderOf(string group)
    {
        int named = -1;
        for (int i = 0; i < sections.Count; i++)
        {
            if (sections[i] == group)
            {
                named = i;
                break;
            }
        }

        return IndexOf(group.Split(" · ")[0]) * 1000 + (named >= 0 ? named : 999);
    }

    private List<UISetting> Ordered(IEnumerable<UISetting> settings) =>
        [.. settings.Select((setting, at) => (setting, at))
            .OrderBy(pair => IndexOf(pair.setting.Group.Split(" · ")[0]))
            .ThenBy(pair => pair.at)
            .Select(pair => pair.setting)];

    private int IndexOf(string part)
    {
        for (int i = 0; i < parts.Count; i++)
        {
            if (parts[i] == part)
                return i;
        }

        return parts.Count;
    }

    // the section a row is of, by its key, which no two sections share (the editor's theme is editor_skin)
    private ISettingsSource? SourceOf(string key) => sources.Find(source => source.Settings.Any(setting => setting.Key == key));

    /// <summary>Every row showing what its setting is now, after the file changed somewhere else.</summary>
    public void Refresh() => Page.Refresh();

    /// <summary>A setting set by its key as the page would, for a test.</summary>
    public bool Enter(string key, IRuntimeValue value) => Page.Enter(key, value);

    /// <summary>Goes straight to one setting by its key, scrolled to and flashed with the keyboard on it, see <see cref="SettingsPage.Reveal"/>.</summary>
    public bool Reveal(string key) => Page.Reveal(key);

    /// <summary>A component shown as a section of its own (an extension's layout of settings), null to take it away.</summary>
    public void SetPanel(string group, UIComponent? content) => Page.SetPanel(group, content);

    /// <summary>
    /// How big the window opens in what it's in, most of it (a share across and down) between its least size and a
    /// most that keeps it from swallowing a big screen. It was 1040 by 700 whatever the screen, a postage stamp on 4K.
    /// </summary>
    /// <param name="scale">How many units of what it's in there are to one of the system's, the most is in the system's.</param>
    public static Vector2 SizeFor(Vector2 room, Vector2 least, float scale = 1.0f) => new(
        Fit(room.X, SHARE.X, least.X, MOST_WIDTH * scale, BIG_WIDTH * scale),
        Fit(room.Y, SHARE.Y, least.Y, MOST_HEIGHT * scale, BIG_HEIGHT * scale));

    // The most of a big screen it opens at, and how much of it, once the share above would run past the usual most. On
    // a 4K screen that most left the window at a third of it, and the rows a strip down its left
    private const float BIG_SHARE = 0.8f, BIG_WIDTH = 3200.0f, BIG_HEIGHT = 1800.0f;

    // One side, its share of the room between its least and its most. A room so big the share runs past the most gets
    // the bigger share of a big screen instead, up to its own most, so a laptop's window is the size it always was
    private static float Fit(float room, float share, float least, float most, float big)
    {
        if (room * share <= most)
            return MathF.Max(least, room * share);
        return MathF.Max(MathF.Max(least, most), MathF.Min(room * BIG_SHARE, big));
    }

    /// <summary>
    /// How many units of a module there are to one of the system's on the screen the window is on. A module laid out in
    /// pixels (Hex's, which scales its text itself) has 1.5 of them to one at 150%, and the most it opens at was a
    /// third of a 4K screen. One that's drawn at the screen's scale already has 1.
    /// </summary>
    public static float ScaleIn(UIModule? module)
    {
        float screen = Horizon.Engine.GameEngine.Instance?.WindowManager.DisplayScale ?? 1.0f;
        return MathF.Max(1.0f, screen / MathF.Max(0.01f, module?.UnitScale ?? 1.0f));
    }

    // The window, sized to what it's in the first update it's in something laid out, in the middle of it. Once only, a
    // size dragged with its corner stays
    private sealed class SizedWindow(string title) : WindowPanel(title)
    {
        private bool sized;

        // Called every update, for the window to keep what it goes by
        public Action? Ticked { get; init; }

        // The zoom the page is drawn at, which the title bar goes with
        public Func<float>? Zoom { get; init; }

        // The title bar every window has (WindowPanel.TITLE_HEIGHT) at the page's zoom, so it grows with the page

        protected override void Update(float dt)
        {
            Ticked?.Invoke();

            float zoom = Zoom?.Invoke() ?? 1.0f;
            TitleHeight = MathF.Round(TITLE_HEIGHT * zoom);
            if (Module?.Compositor.Skin is { } skin)
                TextScale = skin.TextScale * TITLE_TEXT * zoom;
            if (!sized && Parent is { Bounds.IsEmpty: false } parent)
            {
                sized = true;
                Size = SizeFor(parent.Bounds.Size, MinSize, ScaleIn(Module));
                Position = Vector2.Zero;
            }

            base.Update(dt);
        }
    }
}
