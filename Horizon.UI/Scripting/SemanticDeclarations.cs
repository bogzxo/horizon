using System.Text.RegularExpressions;

namespace Horizon.UI.Scripting;

/// <summary>
/// The kinds a server's legend hasn't got, made out from how the name is declared. csharp-ls calls a delegate and a
/// record a class and an extension method a method, so a class declared after `delegate` and a return type is a delegate,
/// one after `record` a record, and a method whose first parameter starts with `this` an extension method, everywhere
/// that name is a class or a method in the file. A server that says them itself is left as it is.
/// </summary>
public static partial class SemanticDeclarations
{
    /// <summary>The spans with every delegate, record and extension method the text declares said so.</summary>
    public static List<SemanticSpan> Refine(List<SemanticSpan> spans, IReadOnlyList<string> lines)
    {
        Dictionary<string, SyntaxKind>? found = null;
        foreach (SemanticSpan span in spans)
        {
            if (span.Kind is not (SyntaxKind.Class or SyntaxKind.Method) || span.Line >= lines.Count || span.Start + span.Length > lines[span.Line].Length)
                continue;

            string line = lines[span.Line];
            ReadOnlySpan<char> before = line.AsSpan(0, span.Start), after = line.AsSpan(span.Start + span.Length);
            SyntaxKind? kind = span.Kind switch
            {
                SyntaxKind.Class when DelegateBefore().IsMatch(before) => SyntaxKind.Delegate,
                SyntaxKind.Class when RecordBefore().IsMatch(before) => SyntaxKind.Record,
                SyntaxKind.Method when ExtensionAfter().IsMatch(after) => SyntaxKind.ExtensionMethod,
                _ => null
            };

            if (kind is { } made)
                (found ??= new(StringComparer.Ordinal))[line.Substring(span.Start, span.Length)] = made;
        }

        if (found is null)
            return spans;

        var refined = new List<SemanticSpan>(spans.Count);
        foreach (SemanticSpan span in spans)
        {
            if (span.Kind is SyntaxKind.Class or SyntaxKind.Method && span.Line < lines.Count && span.Start + span.Length <= lines[span.Line].Length
                && found.TryGetValue(lines[span.Line].Substring(span.Start, span.Length), out SyntaxKind kind)
                && (kind == SyntaxKind.ExtensionMethod) == (span.Kind == SyntaxKind.Method))
                refined.Add(span with { Kind = kind });
            else
                refined.Add(span);
        }

        return refined;
    }

    // delegate and a return type before the name, delegate void Changed
    [GeneratedRegex(@"\bdelegate\s+[\w<>\[\],.?\s]+\s$")]
    private static partial Regex DelegateBefore();

    // record, record class or record struct before the name
    [GeneratedRegex(@"\brecord(\s+(class|struct))?\s+$")]
    private static partial Regex RecordBefore();

    // a method whose first parameter is this, Twice(this int value), its type parameters first if it has any
    [GeneratedRegex(@"^\s*(<[^>]*>)?\s*\(\s*this\s")]
    private static partial Regex ExtensionAfter();
}
