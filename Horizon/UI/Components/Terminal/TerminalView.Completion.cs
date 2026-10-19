using System.Numerics;

using Horizon.UI.Drawing;

using Silk.NET.Input;

namespace Horizon.UI.Components;

// The list of what could finish the word being typed, for the shells that don't have one worth the name (cmd, bash).
// Worked out when something is typed at the end of the line, drawn under the word, tab takes the one picked, up and
// down pick, escape puts it away and anything else goes to the shell as it would have and puts it away too.
public partial class TerminalView
{
    /// <summary>What finishes the word being typed, null for no list.</summary>
    public ITerminalCompleter? Completer
    {
        get => completer;
        set
        {
            completer = value;
            HideCompletion();
        }
    }

    private ITerminalCompleter? completer;
    private IReadOnlyList<TerminalLineCompletion> completions = [];
    private int completionChosen, completionWordStart, completedVersion = -1;
    private string completionWord = string.Empty;
    private bool wantCompletion;

    /// <summary>What the list shows, empty while there is none.</summary>
    public IReadOnlyList<TerminalLineCompletion> Completions => completions;

    /// <summary>Which of them is picked.</summary>
    public int CompletionChosen => completionChosen;

    public void HideCompletion()
    {
        completions = [];
        completionChosen = 0;
        wantCompletion = false;
    }

    /// <summary>The picked one typed in, the rest of the word, true when there was one.</summary>
    public bool AcceptCompletion()
    {
        if (completions.Count == 0)
            return false;

        TerminalLineCompletion picked = completions[completionChosen];
        string typed = picked.Text.StartsWith(completionWord, StringComparison.OrdinalIgnoreCase)
            ? picked.Text[completionWord.Length..]
            : new string('\u007f', completionWord.Length) + picked.Text;
        HideCompletion();
        if (typed.Length > 0)
            Typed(typed);

        // into a folder, the list of what's in it comes up next
        wantCompletion = picked.Folder;
        return true;
    }

    /// <summary>A key while the list is up, true when it was the list's.</summary>
    private bool CompletionKey(in UIKeyPress press)
    {
        if (completions.Count == 0)
            return false;

        // what the UI passes on that isn't a key at all (0) leaves it be
        if ((int)press.Key <= 0)
            return false;

        bool plain = !press.Control && !press.Alt && !press.Shift;
        switch (press.Key)
        {
            case Key.Tab when plain:
                AcceptCompletion();
                return true;
            case Key.Down when plain:
                completionChosen = (completionChosen + 1) % completions.Count;
                return true;
            case Key.Up when plain:
                completionChosen = (completionChosen + completions.Count - 1) % completions.Count;
                return true;
            case Key.Escape:
                HideCompletion();
                return true;
            case Key.ShiftLeft or Key.ShiftRight or Key.ControlLeft or Key.ControlRight or Key.AltLeft or Key.AltRight:
                return false;
            default:
                // the key goes to the shell, a backspace or a letter brings a new list once the shell has written it
                bool editing = press.Key is Key.Backspace;
                HideCompletion();
                wantCompletion = editing;
                return false;
        }
    }

    /// <summary>What was sent decides whether a list is wanted, a letter typed is, a line run or an arrow isn't.</summary>
    private void CompletionTyped(string bytes)
    {
        if (completer is null)
            return;
        if (bytes.Length == 1 && bytes[0] >= ' ' && bytes[0] != '\u007f')
        {
            wantCompletion = true;
            return;
        }

        if (bytes is "\u007f" or "\b")
            return;

        HideCompletion();
    }

    /// <summary>The list worked out for the line as it is now, once the shell has written what was typed.</summary>
    private void UpdateCompletion()
    {
        if (completer is null || !wantCompletion || screen.Version == completedVersion)
            return;
        if (lineRow < 0 || scrolled != 0)
        {
            if (lineRow < 0)
                HideCompletion();
            return;
        }

        completedVersion = screen.Version;

        // only at the end of the line, a word in the middle of one isn't being typed
        int cursor = (screen.Scrollback.Count + screen.CursorY - lineRow) * lineStride + screen.CursorX - lineColumn;
        if (cursor != lineLength)
        {
            completions = [];
            return;
        }

        string text = new(lineText, 0, lineLength);
        completions = completer.Complete(text, WorkingDirectory, out completionWordStart);
        completionWord = text[completionWordStart..];
        completionChosen = 0;
    }

    private void PaintCompletion(UIDrawList list, float scale, float top, TerminalPalette palette)
    {
        if (completions.Count == 0 || scrolled != 0 || lineRow < 0)
            return;

        int longest = 0;
        for (int i = 0; i < completions.Count; i++)
            longest = Math.Max(longest, completions[i].Text.Length);

        // Under the word, or over it when there isn't room under it
        int place = lineColumn + completionWordStart;
        int wordRow = lineRow + place / Math.Max(1, lineStride) - screen.Scrollback.Count;
        float x = paintedArea.Min.X + (place % Math.Max(1, lineStride)) * cellWidth - cellWidth;
        float width = (longest + 2) * cellWidth;
        x = Math.Clamp(x, paintedArea.Min.X, Math.Max(paintedArea.Min.X, paintedArea.Max.X - width));
        int count = completions.Count;
        bool under = wordRow + 1 + count <= screen.Rows;
        float boxTop = under ? top - (wordRow + 1) * cellHeight : top - (wordRow - count) * cellHeight;
        var box = new UIRect(new Vector2(x, boxTop - count * cellHeight), new Vector2(x + width, boxTop));

        list.BeginPart(-3);
        list.Rect(box, Blend(palette.Background, palette.Foreground with { W = 0.1f }));
        list.Frame(box, 1.0f, palette.Foreground with { W = 0.3f });
        for (int i = 0; i < count; i++)
        {
            float rowTop = boxTop - i * cellHeight;
            if (i == completionChosen)
                list.Rect(new UIRect(new Vector2(box.Min.X, rowTop - cellHeight), new Vector2(box.Max.X, rowTop)), Blend(palette.Background, palette.Selection));
            TerminalLineCompletion item = completions[i];
            Vector4 ink = item.Folder ? palette.Ansi[4] : item.Command ? palette.Ansi[2] : palette.Foreground;
            list.Text(item.Text.AsSpan(), new Vector2(box.Min.X + cellWidth, rowTop), scale, ink, false, default, default);
        }
    }
}
