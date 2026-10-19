using System.Numerics;
using System.Runtime.InteropServices;

using Horizon.UI.Drawing;

namespace Horizon.UI.Components;

/// <summary>What the bell does, a flash of the pane, the machine's own sound, or nothing.</summary>
public enum TerminalBell
{
    Flash,
    Sound,
    None
}

// The pane's own bits. Its title in a badge in the top right corner while its tab has more than one pane, which is also
// the handle a pane is dragged by onto another to swap the two, copy on select and paste on a right click for whoever
// wants them, and the bell.
public partial class TerminalView
{
    /// <summary>What somebody renamed the pane to, null for the title the program or the profile gives it.</summary>
    public string? PaneName { get; set; }

    /// <summary>What the pane is called, its name, or what the program said, or the profile's name.</summary>
    public string PaneTitle => PaneName ?? (Title is { Length: > 0 } said && !(said.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && (said.Contains('\\') || said.Contains('/'))) ? said : Profile?.Name ?? "terminal");

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

    private void PaintBadge(UIDrawList list, UIFont font, float scale, TerminalPalette palette)
    {
        if (!ShowPaneTitle)
        {
            badge = default;
            return;
        }

        string title = PaneTitle;
        float small = scale * 0.8f, height = font.LineHeight * small;
        float width = font.Measure(title, small, markup: false).X + height;
        var corner = new Vector2(Bounds.Max.X - 4.0f, Bounds.Max.Y - 4.0f);
        badge = new UIRect(new Vector2(corner.X - width, corner.Y - height), corner);

        list.BeginPart(-4);
        list.Rect(badge, Blend(palette.Background, palette.Foreground with { W = draggingPane ? 0.3f : 0.12f }));
        list.Text(title.AsSpan(), new Vector2(badge.Min.X + height * 0.5f, badge.Max.Y), small, Vector4.Lerp(palette.Foreground, palette.Background, 0.3f), false, default, default);

        // dragged, the badge goes along under the pointer as well
        if (draggingPane)
        {
            var moved = new UIRect(lastPointer - new Vector2(width * 0.5f, height * 0.5f), lastPointer + new Vector2(width * 0.5f, height * 0.5f));
            list.Rect(moved, Blend(palette.Background, palette.Selection));
            list.Text(title.AsSpan(), new Vector2(moved.Min.X + height * 0.5f, moved.Max.Y), small, palette.Foreground, false, default, default);
        }
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
