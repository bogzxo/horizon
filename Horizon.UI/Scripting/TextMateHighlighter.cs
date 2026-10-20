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
/// A grammar isn't safe to use from two threads at once, so one of these takes a lock round every line it reads, and
/// an editor that colours on a thread of the pool while another does the same waits its turn, a few milliseconds.
/// </para>
/// </summary>
public sealed class TextMateHighlighter : ICodeHighlighter
{
    // How long one line may take before the grammar gives up on it, a pathological regex on a minified line
    private static readonly TimeSpan LINE_LIMIT = TimeSpan.FromMilliseconds(250);

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

        lock (grammar)
        {
            for (int line = 0; line < spans.Length; line++)
            {
                int end = at < code.Length ? code.IndexOf('\n', at) : -1;
                if (end < 0)
                    end = Math.Max(at, code.Length);

                if (starts is not null && line < starts.Length)
                    starts[line] = new CodeLineStart(depth, state);

                spans[line] = ReadLine(code.AsMemory(at, end - at), ref state, ref depth);
                at = end + 1;
            }
        }

        return spans;
    }

    public List<SyntaxSpan> ReadLine(string line, in CodeLineStart start)
    {
        IStateStack? state = start.State as IStateStack;
        int depth = start.Depth;

        lock (grammar)
            return ReadLine(line.AsMemory(), ref state, ref depth);
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
            state = grammar.TokenizeLine(new LineText(text), state!, LINE_LIMIT).RuleStack;
            return spans;
        }

        ITokenizeLineResult result = grammar.TokenizeLine(new LineText(text), state!, LINE_LIMIT);
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
                Add(spans, line, from, to, kind);
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

                Add(spans, line, runStart, i, kind);
                if (opens)
                {
                    Add(spans, line, i, i + 1, BracketAt(depth++));
                }
                else
                {
                    depth = Math.Max(0, depth - 1);
                    Add(spans, line, i, i + 1, BracketAt(depth));
                }

                runStart = i + 1;
            }

            Add(spans, line, runStart, to, kind);
        }

        return spans;
    }

    private static SyntaxKind BracketAt(int depth) => (depth % 3) switch
    {
        0 => SyntaxKind.Bracket1,
        1 => SyntaxKind.Bracket2,
        _ => SyntaxKind.Bracket3
    };

    // Helper method to put a stretch of a line in as one kind, less the whitespace at its ends, joined onto the span
    // before it when that is the same kind
    private static void Add(List<SyntaxSpan> spans, ReadOnlySpan<char> line, int from, int to, SyntaxKind kind)
    {
        while (from < to && char.IsWhiteSpace(line[from])) from++;
        while (to > from && char.IsWhiteSpace(line[to - 1])) to--;
        if (to <= from)
            return;

        if (spans.Count > 0 && spans[^1] is { } last && last.Kind == kind && kind is not (SyntaxKind.Bracket1 or SyntaxKind.Bracket2 or SyntaxKind.Bracket3))
        {
            spans[^1] = last with { Length = to - last.Start };
            return;
        }

        spans.Add(new SyntaxSpan(from, to - from, kind));
    }
}
