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
    private const float WIDTH = 1040.0f, HEIGHT = 700.0f;

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
    public SettingsWindow(IEnumerable<ISettingsSource> sources, Action close, IReadOnlyList<string>? parts = null, float titleScale = 0.0f, float bodyScale = 0.0f,
        IReadOnlyList<string>? sections = null)
    {
        this.sources = [.. sources];
        this.parts = parts ?? [];
        this.sections = sections ?? [];
        Page = new SettingsPage
        {
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
            OnButton = (key, which) => SourceOf(key)?.Press(key, which),

            // Escape closes it, the window has the x
            OnClose = close,
            GroupOrder = OrderOf
        };
        Page.HideClose = true;

        // A strip along the bottom the page leaves to the grip in the window's corner, or the page took the drag
        Window = new WindowPanel("Settings")
        {
            Padding = new UIEdges(0.0f, 0.0f, 0.0f, 16.0f),
            Icon = "settings-gear",
            Size = new Vector2(WIDTH, HEIGHT),
            MinSize = new Vector2(640.0f, 420.0f),
            Lift = 28.0f,
            OnClosed = close
        };
        Window.Add(Page);
    }

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

    /// <summary>A component shown as a section of its own (an extension's layout of settings), null to take it away.</summary>
    public void SetPanel(string group, UIComponent? content) => Page.SetPanel(group, content);
}
