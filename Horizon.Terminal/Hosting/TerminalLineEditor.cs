using System.Text;

namespace Horizon.UI.Components;

/// <summary>
/// A line read in a terminal the way a shell reads one, for a program of our own (the HIDL REPL, any shell written in
/// HIDL), a <see cref="ITerminalProgram"/>. A prompt, a caret moved by the arrows, Home and End and by words with
/// control, Backspace and Delete, control with U to clear the line, control with C to give it up, control with L to
/// clear the screen, history on Up and Down (kept in a file when it is given one), and a completion the owner works
/// out (<see cref="Complete"/>) shown the way fish shows it, dim after the caret, Tab or Right at the end to take it.
/// Return hands the line to <see cref="OnLine"/>, which writes what it has to say with <see cref="Write"/>, and the
/// prompt comes again. The line is drawn by the editor into the screen with the sequences any terminal knows, a
/// carriage return, the prompt, the text, the completion, an erase to the end and the caret put back.
/// </summary>
public class TerminalLineEditor : ITerminalProgram
{
    private readonly StringBuilder line = new();
    private readonly TerminalHistory.Cursor cursor = new();
    private TerminalView? view;
    private int caret;
    private int columns = 80;
    private bool running = true;
    private string escape = string.Empty;

    /// <summary>What is written before the line, with any colours it wants (ESC [ 36 m and so on).</summary>
    public string Prompt { get; set; } = "> ";

    /// <summary>The lines given before, Up and Down step them.</summary>
    public TerminalHistory History { get; }

    /// <summary>A line was given, with what it was. Write what is to be said, the prompt comes after.</summary>
    public Action<string>? OnLine { get; set; }

    /// <summary>
    /// What would complete the text before the caret, the part to add after the caret (so "pr" might give "int("),
    /// null for nothing. Asked every time the line changes, keep it quick.
    /// </summary>
    public Func<string, string?>? Complete { get; set; }

    /// <summary>What the text it has been given is coloured with, the line handed in and the coloured text handed back, null for plain.</summary>
    public Func<string, string>? Colour { get; set; }

    /// <summary>The first thing written, before the first prompt.</summary>
    public string Greeting { get; set; } = string.Empty;

    public virtual string Name { get; set; } = "line editor";

    public bool IsRunning => running;

    /// <summary>The line as it is so far, and where the caret is in it.</summary>
    public string Line => line.ToString();

    public int Caret => caret;

    /// <param name="history">Where the lines are kept from one run to the next, null for only this one.</param>
    public TerminalLineEditor(string? history = null) => History = new TerminalHistory(history);

    public virtual void Attach(TerminalView attached)
    {
        view = attached;
        columns = Math.Max(2, attached.Columns);
        if (Greeting.Length > 0)
            Write(Greeting.EndsWith('\n') ? Greeting : Greeting + "\n");
        Redraw();
    }

    public virtual void Resize(int newColumns, int rows)
    {
        columns = Math.Max(2, newColumns);
        Redraw();
    }

    public virtual void Update(float dt)
    {
    }

    /// <summary>Writes text above the line, newlines and all, the line drawn again under it.</summary>
    public void Write(string text)
    {
        if (view is null)
            return;

        view.Feed("\r\u001b[K" + text.ReplaceLineEndings("\r\n"));

        // Said while somebody is typing (a program printing as it goes), the line they were typing comes back under it
        if (!submitting)
            Redraw();
    }

    private bool submitting;

    /// <summary>Ends the program with a code, which ends its terminal the way a shell ending does.</summary>
    public void Exit(int code = 0)
    {
        running = false;
        view?.End(code);
    }

    public void Input(string bytes)
    {
        foreach (char character in bytes)
            Take(character);
    }

    private void Take(char character)
    {
        // An escape sequence is gathered until its last byte, a letter or a tilde
        if (escape.Length > 0 || character == '\u001b')
        {
            escape += character;
            if (escape.Length == 1 || (escape.Length == 2 && character is '[' or 'O'))
                return;
            if (!(char.IsLetter(character) || character == '~') && escape.Length < 12)
                return;

            string sequence = escape;
            escape = string.Empty;
            Escape(sequence);
            return;
        }

        switch (character)
        {
            case '\r' or '\n':
                Submit();
                return;
            case '\u007f' or '\b':
                if (caret > 0)
                {
                    line.Remove(--caret, 1);
                    Changed();
                }
                return;
            case '\t':
                TakeCompletion();
                return;
            case '\u0001':
                caret = 0;
                Redraw();
                return;
            case '\u0005':
                caret = line.Length;
                Redraw();
                return;
            case '\u0003':
                Write(Prompt + line + "^C\n");
                line.Clear();
                caret = 0;
                cursor.Reset();
                Redraw();
                return;
            case '\u0015':
                line.Remove(0, caret);
                caret = 0;
                Changed();
                return;
            case '\u000c':
                view?.Feed("\u001b[H\u001b[2J");
                Redraw();
                return;
            case '\u0017':
                int from = WordLeft();
                line.Remove(from, caret - from);
                caret = from;
                Changed();
                return;
        }

        if (character < ' ')
            return;

        line.Insert(caret++, character);
        Changed();
    }

    private void Escape(string sequence)
    {
        switch (sequence)
        {
            case "\u001b[D" or "\u001bOD":
                caret = Math.Max(0, caret - 1);
                break;
            case "\u001b[C" or "\u001bOC":
                if (caret == line.Length)
                {
                    TakeCompletion();
                    return;
                }
                caret++;
                break;
            case "\u001b[1;5D" or "\u001b[1;3D" or "\u001bb":
                caret = WordLeft();
                break;
            case "\u001b[1;5C" or "\u001b[1;3C" or "\u001bf":
                caret = WordRight();
                break;
            case "\u001b[H" or "\u001bOH" or "\u001b[1~":
                caret = 0;
                break;
            case "\u001b[F" or "\u001bOF" or "\u001b[4~":
                caret = line.Length;
                break;
            case "\u001b[3~":
                if (caret < line.Length)
                    line.Remove(caret, 1);
                Changed();
                return;
            case "\u001b[A" or "\u001bOA":
                Step(-1);
                return;
            case "\u001b[B" or "\u001bOB":
                Step(1);
                return;
            default:
                return;
        }

        Redraw();
    }

    private int WordLeft()
    {
        int at = caret;
        while (at > 0 && char.IsWhiteSpace(line[at - 1])) at--;
        while (at > 0 && !IsBreak(line[at - 1])) at--;
        return at;
    }

    private int WordRight()
    {
        int at = caret;
        while (at < line.Length && char.IsWhiteSpace(line[at])) at++;
        while (at < line.Length && !IsBreak(line[at])) at++;
        return at;
    }

    private static bool IsBreak(char character) => char.IsWhiteSpace(character) || character is '.' or '(' or ')' or ',' or '[' or ']' or '{' or '}' or '"';

    private void Step(int direction)
    {
        if (History.Step(cursor, direction, line.ToString()) is not { } taken)
            return;

        line.Clear().Append(taken);
        caret = line.Length;
        Redraw();
    }

    private string? suggestion;

    private void TakeCompletion()
    {
        if (suggestion is not { Length: > 0 } taking || caret != line.Length)
            return;

        line.Append(taking);
        caret = line.Length;
        Changed();
    }

    private void Changed()
    {
        cursor.Reset();
        Redraw();
    }

    private void Submit()
    {
        string given = line.ToString();
        suggestion = null;

        // The line as it was given stays on screen, without its completion, and the answer goes under it
        view?.Feed("\r\u001b[K" + Prompt + (Colour?.Invoke(given) ?? given) + "\r\n");
        line.Clear();
        caret = 0;
        cursor.Reset();
        History.Add(given);

        submitting = true;
        try
        {
            OnLine?.Invoke(given);
        }
        catch (Exception e)
        {
            Write($"\u001b[31m{e.Message}\u001b[0m\n");
        }
        finally
        {
            submitting = false;
        }

        if (running)
            Redraw();
    }

    /// <summary>The prompt and the line drawn again where the cursor's row is, the caret put where it is in the line.</summary>
    public void Redraw()
    {
        if (view is null || !running)
            return;

        string text = line.ToString();
        suggestion = caret == text.Length && text.Length > 0 ? Complete?.Invoke(text) : null;

        // A line wider than the terminal shows its end, the part with the caret in it
        int promptWidth = VisibleLength(Prompt);
        int room = Math.Max(4, columns - promptWidth - 1);
        int start = text.Length > room ? Math.Max(0, Math.Min(caret, text.Length) - room + 1) : 0;
        string shown = text.Substring(start, Math.Min(room, text.Length - start));

        var drawn = new StringBuilder();
        drawn.Append('\r').Append(Prompt).Append(Colour?.Invoke(shown) ?? shown);
        if (suggestion is { Length: > 0 } && promptWidth + shown.Length + 1 < columns)
            drawn.Append("\u001b[2m").Append(suggestion.AsSpan(0, Math.Min(suggestion.Length, columns - promptWidth - shown.Length - 1))).Append("\u001b[0m");
        drawn.Append("\u001b[0m\u001b[K\r");
        int column = promptWidth + caret - start;
        if (column > 0)
            drawn.Append("\u001b[").Append(column).Append('C');
        view.Feed(drawn.ToString());
    }

    /// <summary>How many cells a text takes, its escape sequences taking none.</summary>
    public static int VisibleLength(string text)
    {
        int length = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\u001b')
            {
                i++;
                if (i < text.Length && text[i] == '[')
                {
                    while (i + 1 < text.Length && !char.IsLetter(text[i + 1]))
                        i++;
                    i++;
                }
                continue;
            }

            if (text[i] >= ' ')
                length++;
        }

        return length;
    }

    public virtual void Dispose()
    {
        running = false;
        GC.SuppressFinalize(this);
    }
}
