using System.Numerics;

using Horizon.Input;
using Horizon.UI.Drawing;

namespace Horizon.UI.Components;

// Ghost text after the line being typed, dim, a suggestion from whoever sets it (Hex's predictions). It belongs to the
// line it was set for: once what is typed is anything else, or the caret isn't at the end, or a program that isn't a shell
// at its prompt has the screen, it isn't shown. Tab or Right takes it, which types it, Escape puts it away.
public partial class TerminalView
{
    private string? ghost, ghostFor;

    /// <summary>The line the ghost text was set for, empty or null when there is none.</summary>
    public string? GhostLine => ghostFor;

    /// <summary>The suggestion showing after the line as it is now, null when there is none to show.</summary>
    public string? GhostText => ghost is { Length: > 0 } && lineRow >= 0 && !screen.AlternateScreen && scrolled == 0 && GhostFits() ? ghost : null;

    /// <summary>Called with the text that was taken, once it has been typed.</summary>
    public Action<string>? OnGhostAccepted { get; set; }

    /// <summary>Puts a suggestion after <paramref name="line"/>, the line as it is typed now.</summary>
    public void SetGhost(string line, string? text)
    {
        string? clean = CleanGhost(text);
        ghostFor = clean is not null ? line : null;
        ghost = clean;
    }

    /// <summary>
    /// What may be shown and typed from a suggestion: line ends become a space, every other control character (below
    /// space, DEL, the C1 range) is dropped, and text with an escape in it is no suggestion at all, since it goes to the
    /// pty as keys. Null when nothing is left.
    /// </summary>
    public static string? CleanGhost(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        var clean = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\u001b' || c is >= '\u0080' and <= '\u009f')
                return null;
            if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                clean.Append(' ');
            }
            else if (c >= ' ' && c != '\u007f')
                clean.Append(c);
        }

        string result = clean.ToString().TrimEnd();
        return result.Length == 0 ? null : result;
    }

    public void ClearGhost()
    {
        ghost = ghostFor = null;
    }

    private bool GhostFits()
    {
        if (ghostFor is null || screen.AtPrompt == false && screen.MarksSeen)
            return false;

        return LineAtCaretEnd && lineText.AsSpan(0, lineLength).SequenceEqual(ghostFor);
    }

    /// <summary>Whether the typed line is there to be read and the caret is at its end, where a suggestion would go.</summary>
    public bool LineAtCaretEnd =>
        lineRow >= 0 && !screen.AlternateScreen && (screen.Scrollback.Count + screen.CursorY - lineRow) * lineStride + screen.CursorX - lineColumn == lineLength;

    /// <summary>Takes the suggestion showing, typing it. True when there was one.</summary>
    public bool AcceptGhost()
    {
        if (GhostText is not { } shown || CleanGhost(shown) is not { } text)
            return false;

        ClearGhost();
        Typed(text);
        OnGhostAccepted?.Invoke(text);
        return true;
    }

    private bool GhostKey(in UIKeyPress press)
    {
        if (ghost is null || (int)press.Key <= 0 || press.Control || press.Alt || press.Shift)
            return false;

        switch (press.Key)
        {
            case Key.Tab or Key.Right when GhostText is not null:
                AcceptGhost();
                return true;
            case Key.Escape when GhostText is not null:
                ClearGhost();
                return true;
            default:
                return false;
        }
    }

    private void PaintGhost(UIDrawList list, float scale, float top, TerminalPalette palette)
    {
        if (GhostText is not { } text)
            return;

        int place = lineColumn + lineLength, stride = Math.Max(1, lineStride);
        int row = lineRow + place / stride - screen.Scrollback.Count;
        int column = place % stride;
        if (row < 0 || row >= screen.Rows)
            return;

        // What fits on the row, the rest isn't drawn
        int room = Math.Max(0, screen.Columns - column);
        if (room == 0)
            return;

        ReadOnlySpan<char> shown = text.Length > room ? text.AsSpan(0, room) : text.AsSpan();
        Vector4 dim = Vector4.Lerp(palette.Foreground, palette.Background, 0.55f);
        list.Text(shown, new Vector2(paintedArea.Min.X + column * cellWidth, top - row * cellHeight), scale, dim, false, default, default);
    }
}
