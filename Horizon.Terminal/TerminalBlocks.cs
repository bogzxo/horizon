using System.Text;

namespace Horizon.UI.Components;

/// <summary>
/// What the shell integration marks (FinalTerm's OSC 133) said of a row. The prompt starts on it (A), what is typed
/// starts on it at a column (B), the command's output starts on it (C), and for a prompt row, how the command typed at
/// it ended (D, the exit code, null while it hasn't or the shell never said).
/// </summary>
public struct TerminalRowMark
{
    public bool Prompt;

    /// <summary>The column what is typed starts at, plus one, 0 for none.</summary>
    public int InputAt;

    public bool Output;

    /// <summary>For a prompt row, the exit code of the command typed at it, null while it isn't known.</summary>
    public int? Exit;

    /// <summary>Whether the prompt was guessed (a shell without the marks, the row the cursor was on when Return went).</summary>
    public bool Guessed;

    /// <summary>Whether the screen was cleared from this row down (cls), which ends whatever block was going above it.</summary>
    public bool Cleared;

    public readonly bool IsEmpty => !Prompt && InputAt == 0 && !Output && Exit is null && !Cleared;

    /// <summary>Two marks of rows that became one, the first's prompt and input, either's output and exit.</summary>
    public readonly TerminalRowMark With(in TerminalRowMark other) => new()
    {
        Prompt = Prompt || other.Prompt,
        InputAt = InputAt != 0 ? InputAt : other.InputAt,
        Output = Output || other.Output,
        Exit = Exit ?? other.Exit,
        Guessed = Guessed && (other.Guessed || !other.Prompt),
        Cleared = Cleared || other.Cleared
    };
}

/// <summary>
/// A command with its output, one block of the scrollback between two prompts, by the rows of the whole (scrollback and
/// grid). Worked out from the marks every time it is asked for, the scrollback stays what the shell wrote.
/// </summary>
public readonly record struct TerminalBlock(int PromptRow, int InputRow, int InputColumn, int OutputRow, int EndRow, int? Exit, bool Guessed)
{
    /// <summary>Whether the command ended in an error, a code that isn't 0.</summary>
    public bool Failed => Exit is { } code && code != 0;
}

public sealed partial class TerminalScreen
{
    /// <summary>Whether the shell has said any OSC 133 mark, which is when the blocks are the shell's and not guessed.</summary>
    public bool MarksSeen { get; private set; }

    // The row of the last prompt, whose command's exit code the next D is
    private TerminalRow? lastPrompt;

    // Where what is typed starts on the prompt line, from B until C
    private TerminalRow? inputRow;
    private int inputColumn;

    /// <summary>
    /// A row erased whole (cls, Clear-Host, clear) loses what the marks said of it, or the green and red bars of the
    /// commands before the clear stayed down the side of an empty screen. The prompt it was goes too, so the exit code
    /// of the command that cleared it (cls itself) doesn't put a bar back on the blank row it was typed at.
    /// </summary>
    private void ForgetMarks(TerminalRow row)
    {
        if (row.Mark.IsEmpty)
            return;

        row.Mark = default;
        if (ReferenceEquals(row, lastPrompt))
            lastPrompt = null;
        if (ReferenceEquals(row, inputRow))
            inputRow = null;
        Version++;
    }

    /// <summary>
    /// Whether the shell is at its prompt waiting for a line, as the marks have it (B said and C not yet). A C that came and
    /// waits to be settled (late marks, see LateMarks) counts already. It waits for the output to go quiet, and a program
    /// that never does (a TUI with a spinner, every spinner there is) had the screen taken for the shell's prompt the whole time it
    /// ran, its arrows going to the history and not to its lists.
    /// </summary>
    public bool AtPrompt => inputRow is not null && !outputPending;

    /// <summary>The row of the whole and the column what is typed at the prompt starts at, null when the shell isn't at one.</summary>
    public (int Row, int Column)? Input
    {
        get
        {
            if (inputRow is null)
                return null;

            int row = IndexOf(inputRow);
            return row < 0 ? null : (row, inputColumn);
        }
    }

    /// <summary>
    /// Whether the marks come ahead of the text they are about, which they do through Windows' own ConPTY. It lets an OSC
    /// through the moment the shell writes it, in a write of its own, and paints the text later, so a mark lands where
    /// the cursor was before the prompt or the output was drawn. Set, the marks wait and are settled once the shell has
    /// gone quiet (<see cref="SettleIfQuiet"/>) or a key is typed (<see cref="SettleMarks"/>), when a shell at its prompt
    /// has drawn all of it and parked the cursor where the typing goes. The prompt is then the line the cursor is on
    /// (a prompt of two lines is marked on its last), the input where the cursor is, the output the row after the typed
    /// line. The view sets it for a ConPTY process, marks fed any other way are taken where they are.
    /// </summary>
    public bool LateMarks { get; set; }

    private bool promptPending, inputPending, outputPending;
    private int? exitPending;
    private long lastFed;

    // How many rows the shell's prompt takes, learned from its first, which nothing comes before (a prompt of two lines,
    // Git Bash's, or with a blank line over it), and the row the first one's mark arrived on
    private int promptHeight = 1;
    private int promptArrivedAt = -1;
    private bool promptLearned;

    // When the prompt mark that waits came, a prompt waits for its input mark (the end of the prompt) a second at most
    private long promptSince;

    /// <summary>
    /// Settles the marks that wait once nothing has come for a while, every tick from whoever feeds the screen. A prompt
    /// waits for the mark at its end as well, a shell can write its prompt in pieces with a quiet moment between them.
    /// </summary>
    public void SettleIfQuiet(int quietMilliseconds = 40)
    {
        if (!(promptPending || inputPending || outputPending || exitPending is not null))
            return;

        long now = Environment.TickCount64;
        if (now - lastFed < quietMilliseconds)
            return;

        if (promptPending && !inputPending && now - promptSince < 1000)
            return;

        SettleMarks();
    }

    /// <summary>A mark of FinalTerm's, A, B, C or D with what follows it.</summary>
    /// <summary>How many commands the shell has said were done (OSC 133 D), for whoever does something when one is.</summary>
    public int Finished { get; private set; }

    /// <summary>How the last command that was done ended, 0 for well.</summary>
    public int LastExit { get; private set; }

    /// <summary>How long the last command that was done ran, in seconds, from its output starting to it being done.</summary>
    public float LastTook { get; private set; }

    // When the running command's output started, by the tick count, 0 for none running
    private long commandSince;

    private void Mark(ReadOnlySpan<char> rest)
    {
        if (rest.IsEmpty)
            return;

        MarksSeen = true;

        // How commands went, the same however late the marks come
        if (rest[0] == 'C')
        {
            commandSince = Environment.TickCount64;
        }
        else if (rest[0] == 'D' && commandSince != 0)
        {
            int ended = 0;
            int after = rest.IndexOf(';');
            if (after >= 0)
            {
                // D;code, and maybe more after it (D;0;aid=1), the code being the first of what's said
                ReadOnlySpan<char> code = rest[(after + 1)..];
                int end = code.IndexOf(';');
                _ = int.TryParse(end >= 0 ? code[..end] : code, out ended);
            }

            LastExit = ended;
            LastTook = (Environment.TickCount64 - commandSince) / 1000.0f;
            commandSince = 0;
            Finished++;
        }

        TerminalRow row = Current[CursorY];
        if (LateMarks)
        {
            lastFed = Environment.TickCount64;
            switch (rest[0])
            {
                case 'A':
                    // A new prompt while one waits, the one before was drawn already, it settles first
                    if (promptPending)
                        SettleMarks();
                    promptPending = true;
                    promptSince = Environment.TickCount64;
                    promptArrivedAt = scrollback.Count + CursorY;
                    break;
                case 'B':
                    inputPending = true;
                    break;
                case 'C':
                    outputPending = true;
                    break;
                case 'D':
                    int said = 0;
                    int at = rest.IndexOf(';');
                    if (at >= 0)
                        _ = int.TryParse(rest[(at + 1)..], out said);
                    exitPending = said;
                    break;
            }

            return;
        }

        switch (rest[0])
        {
            case 'A':
                MarkUnsaidOutput(scrollback.Count + CursorY);
                // a command that wrote nothing had its output start where this prompt is, that was no output at all
                row.Mark.Output = false;
                row.Mark.Prompt = true;
                row.Mark.Guessed = false;
                // a prompt just drawn has no command run at it yet, whatever a row drawn over kept
                row.Mark.Exit = null;
                lastPrompt = row;
                inputRow = null;
                break;
            case 'B':
                row.Mark.InputAt = CursorX + 1;
                inputRow = row;
                inputColumn = CursorX;
                break;
            case 'C':
                row.Mark.Output = true;
                inputRow = null;
                break;
            case 'D':
                int code = 0;
                int semicolon = rest.IndexOf(';');
                if (semicolon >= 0)
                    _ = int.TryParse(rest[(semicolon + 1)..], out code);
                if (lastPrompt is not null)
                    lastPrompt.Mark.Exit = code;
                inputRow = null;
                break;
        }

        Version++;
    }

    /// <summary>The marks that came ahead of their text settled where the text has put the cursor, see <see cref="LateMarks"/>.</summary>
    public void SettleMarks()
    {
        if (!promptPending && !inputPending && !outputPending && exitPending is null)
            return;

        if (outputPending)
        {
            // The output starts on the row after what was typed, the typed line's wrapped rows and all
            int row = inputRow is not null ? IndexOf(inputRow) : -1;
            if (row < 0 && lastPrompt is not null)
                row = IndexOf(lastPrompt);
            if (row >= 0)
            {
                while (row < TotalRows - 1 && RowAt(row).Wrapped)
                    row++;
                if (row < TotalRows - 1)
                    row++;
                RowAt(row).Mark.Output = true;
            }

            outputPending = false;
            inputRow = null;
        }

        // How the last command ended goes on its prompt, before the next prompt takes its place
        if (exitPending is { } code)
        {
            if (lastPrompt is not null)
                lastPrompt.Mark.Exit = code;
            exitPending = null;
        }

        if (promptPending)
        {
            // The prompt starts at the start of the line the cursor is on once the prompt is drawn, so many rows up as the
            // shell's prompt is high. The first prompt says how high, nothing was written before it to be in the way
            int row = scrollback.Count + CursorY;
            while (row > 0 && RowAt(row - 1).Wrapped)
                row--;

            if (!promptLearned && lastPrompt is null && promptArrivedAt >= 0 && promptArrivedAt <= row)
            {
                // a banner the shell wrote first (cmd's) ends with a blank row, a prompt never has one inside it
                int from = promptArrivedAt;
                for (int above = row - 1; above >= promptArrivedAt; above--)
                {
                    if (RowAt(above).Used == 0)
                    {
                        from = above + 1;
                        break;
                    }
                }

                promptHeight = Math.Clamp(row - from + 1, 1, 4);
                promptLearned = true;
            }

            int floor = lastPrompt is not null ? IndexOf(lastPrompt) + 1 : 0;
            row = Math.Max(Math.Max(floor, 0), row - (promptHeight - 1));
            MarkUnsaidOutput(row);
            TerminalRow prompt = RowAt(row);
            // the same goes for a command that wrote nothing, its output was put where this prompt is
            prompt.Mark.Output = false;
            prompt.Mark.Prompt = true;
            prompt.Mark.Guessed = false;
            // a prompt just drawn has no command run at it yet, whatever a row drawn over kept (ConPTY draws a cleared
            // screen's prompt over the top row, which had a prompt of its own once)
            prompt.Mark.Exit = null;
            lastPrompt = prompt;
            promptPending = false;
        }

        if (inputPending)
        {
            TerminalRow row = Current[CursorY];
            row.Mark.InputAt = CursorX + 1;
            inputRow = row;
            inputColumn = CursorX;
            inputPending = false;
        }

        Version++;
    }

    /// <summary>
    /// The output of a command whose shell never said where it starts (cmd, which only has its prompt to say things
    /// with), taken to be the row after what was typed, when there is anything between that and the next prompt.
    /// </summary>
    private void MarkUnsaidOutput(int nextPrompt)
    {
        if (inputRow is null)
            return;

        int row = IndexOf(inputRow);
        inputRow = null;
        if (row < 0)
            return;
        while (row < TotalRows - 1 && RowAt(row).Wrapped)
            row++;
        if (++row < nextPrompt)
            RowAt(row).Mark.Output = true;
    }

    /// <summary>
    /// The row the cursor is on taken for a prompt, by somebody who saw Return go to a shell that never says its marks,
    /// so the blocks are there anyway, guessed. Nothing when the shell does say them.
    /// </summary>
    public void GuessPrompt()
    {
        if (MarksSeen || AlternateScreen)
            return;

        TerminalRow row = Current[CursorY];
        if (!row.Mark.Prompt)
        {
            row.Mark.Prompt = true;
            row.Mark.Guessed = true;
            Version++;
        }
    }

    /// <summary>Where a row is in the whole, -1 for one that isn't there any more.</summary>
    private int IndexOf(TerminalRow row)
    {
        int index = scrollback.IndexOf(row);
        if (index >= 0)
            return index;

        index = main.IndexOf(row);
        return index >= 0 ? scrollback.Count + index : -1;
    }

    /// <summary>Every block there is, the oldest first, out of the marks of the main screen and its scrollback.</summary>
    public List<TerminalBlock> Blocks()
    {
        var blocks = new List<TerminalBlock>();
        int total = scrollback.Count + main.Count;
        TerminalRow RowOfWhole(int index) => index < scrollback.Count ? scrollback[index] : main[index - scrollback.Count];

        int prompt = -1;
        int inputRowAt = -1, inputColumnAt = 0, output = -1;
        for (int i = 0; i <= total; i++)
        {
            bool end = i == total;
            TerminalRowMark mark = end ? default : RowOfWhole(i).Mark;

            // A clear ends the block above it where the screen was blanked, its bar doesn't go on down an empty screen
            if (!end && mark.Cleared && prompt >= 0)
            {
                TerminalRowMark at = RowOfWhole(prompt).Mark;
                blocks.Add(new TerminalBlock(prompt, inputRowAt < 0 ? prompt : inputRowAt, inputColumnAt, output, i - 1, at.Exit, at.Guessed));
                prompt = -1;
            }

            if (end || mark.Prompt)
            {
                if (prompt >= 0)
                {
                    TerminalRowMark at = RowOfWhole(prompt).Mark;
                    blocks.Add(new TerminalBlock(prompt, inputRowAt < 0 ? prompt : inputRowAt, inputColumnAt, output, i - 1, at.Exit, at.Guessed));
                }

                if (end)
                    break;

                prompt = i;
                inputRowAt = mark.InputAt != 0 ? i : -1;
                inputColumnAt = mark.InputAt != 0 ? mark.InputAt - 1 : 0;
                output = mark.Output ? i : -1;
                continue;
            }

            if (prompt >= 0 && inputRowAt < 0 && mark.InputAt != 0)
            {
                inputRowAt = i;
                inputColumnAt = mark.InputAt - 1;
            }

            if (prompt >= 0 && output < 0 && mark.Output)
                output = i;
        }

        return blocks;
    }

    /// <summary>The command typed in a block, what is on its input row from the input's column up to where its output starts.</summary>
    public string CommandOf(in TerminalBlock block)
    {
        var text = new StringBuilder();
        int last = block.OutputRow >= 0 ? block.OutputRow - 1 : Math.Min(block.EndRow, block.InputRow);
        for (int row = block.InputRow; row <= last && row < TotalRows; row++)
        {
            TerminalRow line = RowAt(row);
            text.Append(line.Text(row == block.InputRow ? block.InputColumn : 0));
            if (!line.Wrapped)
                break;
        }

        return text.ToString().Trim();
    }

    /// <summary>What a block's command wrote, every row of its output, the rows a line wrapped over joined.</summary>
    public string OutputOf(in TerminalBlock block)
    {
        if (block.OutputRow < 0)
            return string.Empty;

        var text = new StringBuilder();
        for (int row = block.OutputRow; row <= block.EndRow && row < TotalRows; row++)
        {
            TerminalRow line = RowAt(row);
            text.Append(line.Text());
            if (!line.Wrapped)
                text.Append('\n');
        }

        return text.ToString().TrimEnd();
    }
}
