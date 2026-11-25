namespace Horizon.UI.Components;

// Whether what is typed into this shell is a secret (see TerminalSecrets), worked out from the screen: the row the cursor
// is on and what was typed.
public partial class TerminalView
{
    /// <summary>The text of the row the cursor is on, up to the cursor (what is drawn after it is not what was typed).</summary>
    public string RowBeforeCursor()
    {
        int row = screen.Scrollback.Count + screen.CursorY;
        if (row < 0 || row >= screen.TotalRows)
            return string.Empty;

        TerminalRow cells = screen.RowAt(row);
        int end = Math.Min(Math.Min(cells.Length, screen.Columns), screen.CursorX);
        var text = new System.Text.StringBuilder(end);
        for (int column = 0; column < end; column++)
        {
            TerminalCell cell = cells.Cells[column];
            if (cell.IsContinuation)
                continue;
            if (cell.CodePoint is 0 or > 0x10FFFF or (>= 0xD800 and <= 0xDFFF))
                text.Append(' ');
            else
                text.Append(char.ConvertFromUtf32(cell.CodePoint));
        }

        return text.ToString();
    }

    /// <summary>
    /// Whether the row asks for a secret by its words. A shell that marks its prompts and says it is at one isn't asking:
    /// the words are in its path or branch name.
    /// </summary>
    public bool RowAsksForSecret(string row, string typed = "") =>
        !(screen.MarksSeen && screen.AtPrompt) && TerminalSecrets.IsSecretPrompt(row, typed);

    /// <summary>
    /// Whether the cursor sits after a prompt that asks for a secret, or a program is reading what it doesn't echo, for
    /// <paramref name="typed"/>, what has been typed since the last Return. Nothing typed here is kept or predicted.
    /// </summary>
    public bool AtSecretPrompt(string typed)
    {
        if (screen.AlternateScreen)
            return false;

        string row = RowBeforeCursor();
        if (RowAsksForSecret(row, typed))
            return true;

        // Keys that went in and aren't on the cursor's row weren't echoed, unless the shell has a line of its own with them
        // in it (the caret moved back into the middle of it)
        // Echo is late (fast typing, a paste, an ssh round trip), so a row that is known to be a shell's input, as far as
        // this can tell, is never judged by what has come back so far: only a row with no known prompt is.
        return !TerminalSecrets.WasEchoed(typed, row) && TypedLine() is null;
    }
}
