namespace Horizon.UI.Scripting;

/// <summary>
/// Which locals and parameters are written to again after they're made, for a server that doesn't say (csharp-ls never
/// sends a modification modifier), so they can be underlined the way ReSharper underlines them. Worked out from the
/// text round each name the server called a local or a parameter. A name followed by = (not == or =&gt;), a compound
/// assignment, ++ or --, or after ++, --, out or ref, is written there. One written more than once, or written anywhere
/// but where it first turns up (its declaration), is mutable, everywhere it's used. Names are told apart by the method
/// they're in, a method being from one method declaration to the next, near enough.
/// </summary>
public static class SemanticWrites
{
    /// <summary>
    /// The spans with every local or parameter that's written to again marked mutable. As they were when one of them is
    /// mutable already, the server said so itself.
    /// </summary>
    public static List<SemanticSpan> Mark(List<SemanticSpan> spans, IReadOnlyList<string> lines)
    {
        foreach (SemanticSpan span in spans)
        {
            if ((span.Modifiers & SyntaxModifiers.Mutable) != 0)
                return spans;
        }

        // Every local and parameter by the method it's in and its name, how often it's written and whether anywhere but first
        var groups = new Dictionary<(int Method, string Name), (int Writes, bool Again, bool Seen)>();
        var keys = new (int Method, string Name)?[spans.Count];
        int method = 0;
        for (int i = 0; i < spans.Count; i++)
        {
            SemanticSpan span = spans[i];
            if (span.Line < 0 || span.Line >= lines.Count || span.Start + span.Length > lines[span.Line].Length)
                continue;

            string line = lines[span.Line];
            if (span.Kind is SyntaxKind.Method or SyntaxKind.ExtensionMethod && Declares(line, span, (i > 0 ? spans[i - 1] : default)))
                method++;
            if (span.Kind is not (SyntaxKind.Local or SyntaxKind.Parameter))
                continue;

            var key = (method, line.Substring(span.Start, span.Length));
            keys[i] = key;
            groups.TryGetValue(key, out var group);
            if (Written(line, span.Start, span.Length))
                group = (group.Writes + 1, group.Again || group.Seen, true);
            else
                group.Seen = true;
            groups[key] = group;
        }

        var marked = new List<SemanticSpan>(spans.Count);
        for (int i = 0; i < spans.Count; i++)
        {
            SemanticSpan span = spans[i];
            if (keys[i] is { } key && groups[key] is var group && (group.Writes > 1 || group.Again))
                span = span with { Modifiers = span.Modifiers | SyntaxModifiers.Mutable };
            marked.Add(span);
        }

        return marked;
    }

    /// <summary>Whether the name at a place in a line is written to there.</summary>
    public static bool Written(string line, int start, int length)
    {
        ReadOnlySpan<char> after = line.AsSpan(start + length).TrimStart(), before = line.AsSpan(0, start).TrimEnd();
        foreach (string compound in Compounds)
        {
            if (after.StartsWith(compound, StringComparison.Ordinal))
                return true;
        }

        if (after.Length > 0 && after[0] == '=' && !(after.Length > 1 && after[1] is '=' or '>'))
            return true;

        return before.EndsWith("++", StringComparison.Ordinal) || before.EndsWith("--", StringComparison.Ordinal) || EndsWithWord(before, "out") || EndsWithWord(before, "ref");
    }

    private static readonly string[] Compounds = ["+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<=", ">>=", "??=", "~=", "^^=", "++", "--"];

    // Whether a method span is where it's declared rather than called, the first name on its line with nothing before it
    // that a call would have (a dot, an =, a bracket, return, new, await) and a bracket or a < after it
    private static bool Declares(string line, SemanticSpan span, SemanticSpan before)
    {
        if ((span.Modifiers & SyntaxModifiers.Declaration) != 0)
            return true;
        if (before.Length > 0 && before.Line == span.Line && before.Kind is SyntaxKind.Method or SyntaxKind.ExtensionMethod)
            return false;

        ReadOnlySpan<char> ahead = line.AsSpan(0, span.Start);
        if (ahead.IndexOfAny(".=(!") >= 0 || ahead.Trim().Length == 0)
            return false;
        foreach (string word in (string[])["return", "new", "await", "throw", "yield"])
        {
            if (ahead.Trim().StartsWith(word, StringComparison.Ordinal))
                return false;
        }

        ReadOnlySpan<char> after = line.AsSpan(span.Start + span.Length).TrimStart();
        return after.Length > 0 && after[0] is '(' or '<';
    }

    private static bool EndsWithWord(ReadOnlySpan<char> text, string word) =>
        text.EndsWith(word, StringComparison.Ordinal) && (text.Length == word.Length || !char.IsLetterOrDigit(text[^(word.Length + 1)]) && text[^(word.Length + 1)] != '_');
}
