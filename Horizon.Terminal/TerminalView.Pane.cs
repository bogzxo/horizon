using System.Numerics;
using System.Runtime.InteropServices;

using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>What the bell does, a flash of the pane, the machine's own sound, or nothing.</summary>
public enum TerminalBell
{
    Flash,
    Sound,
    None
}

// The pane's own bits. Its title in a slim header along its top while its tab has more than one pane, which is also the
// handle a pane is dragged by onto another to swap the two, copy on select and paste on a right click for whoever wants
// them, and the bell.
public partial class TerminalView
{
    /// <summary>How round the corners of the pane are, a card on the window rather than a square of it. 0 for square.</summary>
    public float Radius { get; set; }

    /// <summary>What somebody renamed the pane to, null for the title the program or the profile gives it.</summary>
    public string? PaneName { get; set; }

    /// <summary>What the pane is called, its name, or what the program said, or the folder its shell is in.</summary>
    public string PaneTitle => PaneName ?? (Title is { Length: > 0 } said && !SaysNothing(said, Profile) ? said : FolderTitle);

    /// <summary>
    /// Whether a title a program set says nothing of its own, the path of a program (what the console calls itself before
    /// a shell says anything) or the name of the shell's profile. PowerShell calls itself "Windows PowerShell" for a moment
    /// as it starts, and every new tab said that for a second before it said its folder.
    /// </summary>
    public static bool SaysNothing(string title, TerminalProfile? profile) =>
        (title.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && (title.Contains('\\') || title.Contains('/')))
        || (profile is not null && title.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What it's called when neither somebody nor its program said, the folder its shell is in ("hexbuild"), the profile's
    /// name before it's in one. Every tab and every pane said "Windows PowerShell", and nobody could tell them apart.
    /// </summary>
    public string FolderTitle
    {
        get
        {
            string? folder = WorkingDirectory;
            if (folder is null)
                return Profile?.Name ?? "terminal";

            // Worked out again only when the folder is another, it's asked for every frame
            if (!ReferenceEquals(folder, folderTitleOf))
            {
                folderTitleOf = folder;
                folderTitle = Path.GetFileName(folder.TrimEnd('/', '\\')) is { Length: > 0 } name ? name : folder;
            }

            return folderTitle;
        }
    }

    private string? folderTitleOf;
    private string folderTitle = string.Empty;

    /// <summary>Whether the badge with the title is drawn, which the panel says while the tab has more than one pane.</summary>
    public bool ShowPaneTitle { get; set; }

    /// <summary>Called when the badge was dragged and let go, with where, for the panel to swap this pane with the one there.</summary>
    public Action<TerminalView, Vector2>? OnPaneDropped { get; set; }

    /// <summary>Where the badge is being dragged to, null while it isn't.</summary>
    public Vector2? PaneDraggedTo => draggingPane ? lastPointer : null;

    /// <summary>Whether a selection goes on the clipboard the moment it is made.</summary>
    public bool CopyOnSelect { get; set; }

    /// <summary>Whether a right click pastes, otherwise it copies a selection there is, or asks <see cref="OnMenu"/>.</summary>
    public bool RightClickPaste { get; set; }

    /// <summary>A right click that neither pasted nor copied, with where, for the application's menu.</summary>
    public Action<TerminalView, Vector2>? OnMenu { get; set; }

    /// <summary>What the bell does.</summary>
    public TerminalBell Bell { get; set; } = TerminalBell.Flash;

    /// <summary>Called when the program rings the bell, whatever it does.</summary>
    public Action<TerminalView>? OnBell { get; set; }

    private bool draggingPane;
    private UIRect badge;

    /// <summary>Where the badge is, nothing while it isn't drawn, for a test.</summary>
    public UIRect PaneBadge => ShowPaneTitle ? badge : default;

    private void Rang()
    {
        switch (Bell)
        {
            case TerminalBell.Flash:
                bell = BELL_FLASH;
                break;
            case TerminalBell.Sound:
                TerminalBellSound.Play();
                break;
        }

        OnBell?.Invoke(this);
    }

    /// <summary>A press on the badge starts a pane's drag, true when it was on it.</summary>
    private bool BadgePressed(Vector2 point)
    {
        if (!ShowPaneTitle || !badge.Contains(point))
            return false;
        draggingPane = true;
        return true;
    }

    private bool BadgeDragged(Vector2 point)
    {
        if (!draggingPane)
            return false;
        RequestFrame();
        return true;
    }

    private bool BadgeReleased(Vector2 point)
    {
        if (!draggingPane)
            return false;
        draggingPane = false;
        OnPaneDropped?.Invoke(this, point);
        RequestFrame();
        return true;
    }

    /// <summary>The badge let go of at a point as a drag would, for a test.</summary>
    public void DropPane(Vector2 point) => OnPaneDropped?.Invoke(this, point);

    /// <summary>A selection that was just made, copied when <see cref="CopyOnSelect"/> says so.</summary>
    private void Selected()
    {
        if (CopyOnSelect && hasSelection)
            UIClipboard.Copy(SelectionText());
    }

    /// <summary>A right click, a paste, a copy of the selection, or the application's menu.</summary>
    private void RightClicked(Vector2 point)
    {
        Focus();
        if (RightClickPaste)
        {
            UIClipboard.Paste();
            return;
        }

        if (hasSelection && !CopyOnSelect)
        {
            UIClipboard.Copy(SelectionText());
            hasSelection = false;
            RequestFrame();
            return;
        }

        OnMenu?.Invoke(this, point);
    }

    // Whether the pointer was over the pane when it was last painted, the header's title lights up under it
    private bool badgeLit;

    // How tall the header is, a line of the UI's caption it's written in and a little round it
    private static float HeaderHeight(UISkin skin) => MathF.Round(skin.Font.LineHeight * skin.TextSize(UITextRole.Caption) + 10.0f);

    // the pointer coming onto the pane or going off it lights the header's title, in a window that only draws on change
    private void UpdateBadge()
    {
        if (ShowPaneTitle && (IsHovered || draggingPane) != badgeLit)
            RequestFrame();
    }

    private void PaintBadge(UIDrawList list, TerminalPalette palette)
    {
        // A slim header along the top of the pane while its tab is split, its title quiet at the left and a hairline under
        // it, the cells starting under it. The title was a badge over the corner, on top of the prompt. It's in the UI's
        // font, not the terminal's, it's the window's caption for the pane and not something a program printed
        UISkin skin = list.Skin;
        UIFont font = skin.Font;
        string title = PaneTitle;
        float small = skin.TextSize(UITextRole.Caption), height = HeaderHeight(skin), line = font.LineHeight * small;
        float pad = MathF.Max(PADDING, Radius * 0.6f);
        badge = ShowPaneTitle ? new UIRect(new Vector2(Bounds.Min.X, Bounds.Max.Y - height), Bounds.Max) : default;
        badgeLit = IsHovered || draggingPane;
        if (!ShowPaneTitle)
            return;

        list.PushFont(font);
        list.BeginPart(-4);
        list.Rect(new UIRect(new Vector2(badge.Min.X + pad, badge.Min.Y), new Vector2(badge.Max.X - pad, badge.Min.Y + 1.0f)), palette.Foreground with { W = 0.08f });
        Vector4 ink = Vector4.Lerp(palette.Foreground, palette.Background, badgeLit ? 0.1f : 0.3f);
        list.Text(title.AsSpan(), new Vector2(badge.Min.X + pad, MathF.Round(badge.Center.Y + line * 0.5f)), small, ink, false, default, default);

        // dragged, the title goes along under the pointer in a pill
        if (draggingPane)
        {
            float width = font.Measure(title, small, markup: false).X + line + 4.0f, tall = line + 4.0f;
            var moved = new UIRect(lastPointer - new Vector2(width * 0.5f, tall * 0.5f), lastPointer + new Vector2(width * 0.5f, tall * 0.5f));
            list.RoundRect(moved, tall * 0.5f, Blend(palette.Background, palette.Selection));
            list.Text(title.AsSpan(), new Vector2(moved.Min.X + tall * 0.5f, moved.Max.Y - 2.0f), small, palette.Foreground, false, default, default);
        }

        list.PopFont();
    }
}

/// <summary>The machine's own bell, the sound Windows makes for a message, nothing elsewhere yet.</summary>
public static partial class TerminalBellSound
{
    private static long last;

    /// <summary>Rings it, once a tenth of a second at most, a program that rings a hundred times rings once.</summary>
    public static void Play()
    {
        long now = Environment.TickCount64;
        if (now - last < 100)
            return;
        last = now;
        if (OperatingSystem.IsWindows())
            MessageBeep(0);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MessageBeep(uint type);
}
