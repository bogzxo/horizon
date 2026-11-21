using TextMateSharp.Grammars;

namespace Horizon.UI.Scripting;

/// <summary>
/// Colours code with a TextMate grammar, the kind VS Code colours everything with, through TextMateSharp (a port of
/// vscode-textmate over Oniguruma). A grammar reads a line at a time and leaves off in a state (its rule stack, inside
/// a block comment, inside a string) that the next line starts from, so the whole is read line by line from the top
/// and every line's starting state is kept (<see cref="CodeLineStart.State"/>), which is what lets one line be coloured
/// again on its own as it is typed. The scopes a grammar puts on a token become the skin's kinds through
/// <see cref="SyntaxScopes"/>, and the brackets of the code, in whatever language, are coloured by how deep they are,
/// the way VS Code's bracket pairs are, which is also what the editor folds and indents by.
/// <para>
/// A grammar isn't safe to use from two threads at once, so background reads take a lock round every line.
/// Paint-time reads skip a busy grammar and retry later, keeping the editor responsive while a file is coloured.
/// </para>
/// </summary>
public sealed class TextMateHighlighter : ICodeHighlighter
{
    // How long one line may take before the grammar gives up on it, a pathological regex on a minified line. It's time
    // by the clock, a thread waiting for a core counts too, so the engine's tests (thousands at once on a busy machine)
    // give it longer, and what they compare isn't a line cut short by how busy the machine was
    internal static TimeSpan LineLimit { get; set; } = TimeSpan.FromMilliseconds(250);

    // A line longer than this is read a piece at a time (see LongLineSpans), each piece the grammar's line with the state
    // carried on from the one before, the lock taken a piece at a time. An agent's chat saved as JSON is one line of a
    // fifth of a megabyte, read whole it ran into LINE_LIMIT and was left plain from where it gave up, a quarter of a
    // second on the editor's thread when it opened and again at every letter typed into it
    internal const int LONG_LINE = 8 * 1024;

    // About how long a piece is, cut just after a comma, a bracket or a space near its end so a token is seldom cut in two
    internal const int PIECE = 2048;

    private readonly IGrammar grammar;

    public string Name { get; }

    /// <summary>The scope the grammar is under, source.cs, source.python, text.html.markdown.</summary>
    public string ScopeName => grammar.GetScopeName();

    public TextMateHighlighter(IGrammar grammar, string name)
    {
        this.grammar = grammar;
        Name = name;
    }

    public List<SyntaxSpan>[] Read(string code, int lineCount, CodeLineStart[]? starts = null, List<HidlSwatch>? swatches = null, List<HidlArt>? art = null)
    {
        swatches?.Clear();
        art?.Clear();

        var spans = new List<SyntaxSpan>[Math.Max(1, lineCount)];
        IStateStack? state = null;
        int depth = 0, at = 0;

        // The lock is taken a line at a time, a big file read on the pool held it for seconds and a line typed on the
        // editor's thread meanwhile waited for all of it
        for (int line = 0; line < spans.Length; line++)
        {
            int end = at < code.Length ? code.IndexOf('\n', at) : -1;
            if (end < 0)
                end = Math.Max(at, code.Length);

            if (starts is not null && line < starts.Length)
                starts[line] = new CodeLineStart(depth, state);

            spans[line] = ReadWhole(code.AsMemory(at, end - at), ref state, ref depth);
            at = end + 1;
        }

        return spans;
    }

    public List<SyntaxSpan>[] ReadAgain(string code, string[] lines, CodeLineStart[] starts, CodeColouring last, List<HidlSwatch>? swatches = null, List<HidlArt>? art = null)
    {
        swatches?.Clear();
        art?.Clear();
        return ReadAgain(lines, starts, last, copyKept: false);
    }

    /// <summary>
    /// Helper method for <see cref="ReadAgain(string, string[], CodeLineStart[], CodeColouring, List{HidlSwatch}?, List{HidlArt}?)"/>,
    /// the lines before the first that changed and after the last kept as they were, the ones between read again from
    /// the state the first of them started in. The reading stops at the first line past the edit that starts in the
    /// state and at the depth of brackets it started in last time, everything after it is coloured the same as it was.
    /// </summary>
    /// <param name="copyKept">Whether what's kept is copied, for a highlighter that writes over the spans after (HIDL's).</param>
    internal List<SyntaxSpan>[] ReadAgain(string[] lines, CodeLineStart[] starts, CodeColouring last, bool copyKept)
    {
        int count = Math.Max(1, lines.Length), was = last.Lines.Length;
        var spans = new List<SyntaxSpan>[count];
        if (lines.Length == 0 || was == 0 || last.Spans.Length != was || last.Starts.Length != was)
        {
            ReadFrom(lines, starts, spans, 0, CodeLineStart.First, last, int.MaxValue, copyKept);
            return spans;
        }

        // The first line that isn't what it was, and how many at the end are
        int limit = Math.Min(lines.Length, was), first = 0;
        while (first < limit && string.Equals(lines[first], last.Lines[first], StringComparison.Ordinal))
            first++;
        int same = 0;
        while (same < limit - first && string.Equals(lines[lines.Length - 1 - same], last.Lines[was - 1 - same], StringComparison.Ordinal))
            same++;

        // From where the first changed line started last time, or the last line again for lines added after the end
        int from = Math.Min(first, was - 1);
        for (int line = 0; line < from; line++)
        {
            spans[line] = Kept(last.Spans[line], copyKept);
            starts[line] = last.Starts[line];
        }

        ReadFrom(lines, starts, spans, from, last.Starts[from], last, lines.Length - same, copyKept);
        return spans;
    }

    // The lines from one on, read until one at or past where the lines are the same again starts as it did last time
    private void ReadFrom(string[] lines, CodeLineStart[] starts, List<SyntaxSpan>[] spans, int from, CodeLineStart start, CodeColouring last, int sameFrom, bool copyKept)
    {
        IStateStack? state = start.State as IStateStack;
        int depth = start.Depth, shift = lines.Length - last.Lines.Length;

        for (int line = from; line < spans.Length; line++)
        {
            int old = line - shift;
            if (line >= sameFrom && old >= 0 && old < last.Starts.Length && depth == last.Starts[old].Depth && Equals(state, last.Starts[old].State))
            {
                for (; line < spans.Length; line++)
                {
                    spans[line] = Kept(last.Spans[line - shift], copyKept);
                    starts[line] = last.Starts[line - shift];
                }

                return;
            }

            starts[line] = new CodeLineStart(depth, state);

            // The line as it was last time, a long one is read again from just before what changed in it
            int was = old >= 0 && old < last.Lines.Length ? old : Math.Min(line, last.Lines.Length - 1);
            bool known = was >= 0 && was < last.Spans.Length;
            spans[line] = ReadWhole((line < lines.Length ? lines[line] : string.Empty).AsMemory(), ref state, ref depth,
                known ? last.Spans[was] : null, known ? last.Lines[was] : null);
        }
    }

    private static List<SyntaxSpan> Kept(List<SyntaxSpan> spans, bool copy) => copy ? [.. spans] : spans;

    public bool TryReadLine(string line, in CodeLineStart start, out List<SyntaxSpan>? spans)
    {
        spans = null;
        if (line.Length > LONG_LINE)
        {
            IStateStack? from = start.State as IStateStack;
            int at = start.Depth;
            spans = ReadWhole(line.AsMemory(), ref from, ref at);
            return true;
        }

        if (!Monitor.TryEnter(grammar)) return false;
        try
        {
            IStateStack? state = start.State as IStateStack;
            int depth = start.Depth;
            spans = ReadLine(line.AsMemory(), ref state, ref depth);
            return true;
        }
        finally { Monitor.Exit(grammar); }
    }

    public List<SyntaxSpan> ReadLine(string line, in CodeLineStart start)
    {
        IStateStack? state = start.State as IStateStack;
        int depth = start.Depth;
        return ReadWhole(line.AsMemory(), ref state, ref depth);
    }

    public List<SyntaxSpan>? ReadUpTo(string line, in CodeLineStart start, int goal, int budget, List<SyntaxSpan>? last, string? lastLine) =>
        ReadLong(line.AsMemory(), start, goal, budget, last as LongLineSpans, lastLine.AsMemory());

    // Helper method for one line of a whole read from a state, leaving the state where the line left it. A long one is
    // read a piece at a time, each under the lock, going on from how it was read last time when that's given
    private List<SyntaxSpan> ReadWhole(ReadOnlyMemory<char> text, ref IStateStack? state, ref int depth, List<SyntaxSpan>? last = null, string? lastText = null)
    {
        if (text.Length > LONG_LINE)
        {
            LongLineSpans read = ReadLong(text, new CodeLineStart(depth, state), text.Length, int.MaxValue, last as LongLineSpans, lastText.AsMemory());
            state = read.End.State as IStateStack;
            depth = read.End.Depth;
            return read;
        }

        lock (grammar)
            return ReadLine(text, ref state, ref depth);
    }

    /// <summary>
    /// Helper method to read a long line a piece at a time (see <see cref="LongLineSpans"/>), as far as a place in it and
    /// no more than so many letters this time. Given how it was read before, what's the same at its start is taken over
    /// up to the last piece that started in it, and past what changed, a piece the last read started at a place where
    /// the grammar is in the state it was in then has everything after it taken over too, moved along by what the edit
    /// added or took out. A letter typed into the middle of a fifth of a megabyte reads a piece or two again.
    /// </summary>
    private LongLineSpans ReadLong(ReadOnlyMemory<char> text, CodeLineStart start, int goal, int budget, LongLineSpans? last, ReadOnlyMemory<char> lastText)
    {
        ReadOnlySpan<char> now = text.Span;
        var read = new LongLineSpans(now.Length);
        int pos = 0, delta = 0, tail = int.MaxValue, next = 0;
        CodeLineStart at = start;

        if (last is { Pieces.Count: > 0 } && last.Pieces[0].Start == start && lastText.Length == last.Length)
        {
            ReadOnlySpan<char> was = lastText.Span;
            int same = now.CommonPrefixLength(was);

            // The pieces that started in what's the same, and the spans before the last of them
            int kept = last.Pieces.Count - 1;
            while (kept > 0 && last.Pieces[kept].At > same)
                kept--;
            for (int i = 0; i <= kept; i++)
                read.Pieces.Add(last.Pieces[i]);
            (pos, at) = last.Pieces[kept];
            foreach (SyntaxSpan span in last)
            {
                if (span.Start >= pos) break;
                read.Add(span);
            }

            // How much is the same at the end, not reaching back into what's the same at the start
            int most = Math.Min(now.Length, was.Length) - same, suffix = 0;
            while (suffix < most && now[now.Length - 1 - suffix] == was[was.Length - 1 - suffix])
                suffix++;
            delta = now.Length - was.Length;
            tail = was.Length - suffix;
            next = kept + 1;
        }
        else
        {
            read.Pieces.Add((0, start));
        }

        int begun = pos;
        while (pos < now.Length && pos < goal && pos - begun < budget)
        {
            // The first piece of the last read that starts here or after, in the part that's the same at the end
            while (last is not null && next < last.Pieces.Count && last.Pieces[next].At + delta < pos)
                next++;
            bool aligned = last is not null && next < last.Pieces.Count && last.Pieces[next].At >= tail;

            // Started where one of the last read's did, in the state it was in, everything after it is as it was
            if (aligned && last!.Pieces[next].At + delta == pos && last.Pieces[next].Start == at)
            {
                int from = last.Pieces[next].At;
                for (int i = next; i < last.Pieces.Count; i++)
                {
                    int moved = last.Pieces[i].At + delta;
                    if (moved > read.Pieces[^1].At)
                        read.Pieces.Add((moved, last.Pieces[i].Start));
                }

                foreach (SyntaxSpan span in last)
                {
                    if (span.Start >= from)
                        read.Add(span with { Start = span.Start + delta });
                }

                read.ReadTo = last.ReadTo + delta;
                read.End = last.End;
                return read;
            }

            if (read.Pieces[^1].At != pos)
                read.Pieces.Add((pos, at));

            // Past the edit a piece ends where one of the last read's did, so the two can meet again, elsewhere near PIECE
            int end = Math.Min(now.Length, pos + PIECE);
            if (end < now.Length)
            {
                if (aligned && last!.Pieces[next].At + delta == pos)
                    aligned = ++next < last.Pieces.Count;
                int there = aligned ? last!.Pieces[next].At + delta : -1;
                end = there > pos && there <= pos + PIECE * 2 ? there : CutNear(now, pos, end);
            }

            IStateStack? state = at.State as IStateStack;
            int depth = at.Depth;
            lock (grammar)
                ReadInto(read, text[pos..end], pos, ref state, ref depth);
            at = new CodeLineStart(depth, state);
            pos = end;
        }

        read.ReadTo = pos;
        read.End = at;
        if (pos < now.Length && read.Pieces[^1].At != pos)
            read.Pieces.Add((pos, at));
        return read;
    }

    // Where a piece from a place is cut, just after the last comma, bracket or space in the second half of it, at the
    // most letters otherwise, never between the two halves of a letter outside the basic plane
    private static int CutNear(ReadOnlySpan<char> line, int from, int most)
    {
        for (int i = most - 1; i > from + PIECE / 2; i--)
        {
            if (line[i] is ',' or ';' or ' ' or '\t' or '}' or ']' or ')')
                return i + 1;
        }

        return char.IsHighSurrogate(line[most - 1]) ? most - 1 : most;
    }

    /// <summary>
    /// Helper method to read one line from a state, leaving the state and the depth of brackets where the line left
    /// them. The grammar's tokens become spans of the skin's kinds, whitespace at their ends left out and neighbours
    /// of one kind joined, so a line is as few draws as it can be.
    /// </summary>
    private List<SyntaxSpan> ReadLine(ReadOnlyMemory<char> text, ref IStateStack? state, ref int depth)
    {
        var spans = new List<SyntaxSpan>();
        if (text.Length == 0)
        {
            // An empty line still moves the state on, a block comment is still open after one
            state = grammar.TokenizeLine(new LineText(text), state!, LineLimit).RuleStack;
            return spans;
        }

        ReadInto(spans, text, 0, ref state, ref depth);
        return spans;
    }

    // Helper method for ReadLine, a line or a piece of one read into spans, its spans moved along by where it is in its
    // line, none of them joined onto one before it (a piece's first onto the last of the piece before)
    private void ReadInto(List<SyntaxSpan> spans, ReadOnlyMemory<char> text, int offset, ref IStateStack? state, ref int depth)
    {
        int first = spans.Count;
        ITokenizeLineResult result = grammar.TokenizeLine(new LineText(text), state!, LineLimit);
        state = result.RuleStack;
        ReadOnlySpan<char> line = text.Span;

        foreach (IToken token in result.Tokens)
        {
            int from = Math.Clamp(token.StartIndex, 0, line.Length), to = Math.Clamp(token.EndIndex, from, line.Length);
            if (to <= from)
                continue;

            SyntaxKind kind = SyntaxScopes.KindOf(token.Scopes);

            if (!SyntaxScopes.HoldsBrackets(token.Scopes))
            {
                Add(spans, first, offset, line, from, to, kind);
                continue;
            }

            // The brackets in it coloured by their depth, whatever is between them as the token is
            int runStart = from;
            for (int i = from; i < to; i++)
            {
                char c = line[i];
                bool opens = c is '(' or '[' or '{', closes = c is ')' or ']' or '}';
                if (!opens && !closes)
                    continue;

                Add(spans, first, offset, line, runStart, i, kind);
                if (opens)
                {
                    Add(spans, first, offset, line, i, i + 1, BracketAt(depth++));
                }
                else
                {
                    depth = Math.Max(0, depth - 1);
                    Add(spans, first, offset, line, i, i + 1, BracketAt(depth));
                }

                runStart = i + 1;
            }

            Add(spans, first, offset, line, runStart, to, kind);
        }

    }

    private static SyntaxKind BracketAt(int depth) => (depth % 3) switch
    {
        0 => SyntaxKind.Bracket1,
        1 => SyntaxKind.Bracket2,
        _ => SyntaxKind.Bracket3
    };

    // Helper method to put a stretch of a line in as one kind, less the whitespace at its ends, joined onto the span
    // before it when that is the same kind
    private static void Add(List<SyntaxSpan> spans, int first, int offset, ReadOnlySpan<char> line, int from, int to, SyntaxKind kind)
    {
        while (from < to && char.IsWhiteSpace(line[from])) from++;
        while (to > from && char.IsWhiteSpace(line[to - 1])) to--;
        if (to <= from)
            return;

        from += offset;
        to += offset;
        if (spans.Count > first && spans[^1] is { } last && last.Kind == kind && kind is not (SyntaxKind.Bracket1 or SyntaxKind.Bracket2 or SyntaxKind.Bracket3))
        {
            spans[^1] = last with { Length = to - last.Start };
            return;
        }

        spans.Add(new SyntaxSpan(from, to - from, kind));
    }
}
