namespace Horizon.UI.Scripting;

/// <summary>
/// A language server's semantic tokens made into what the code editor colours names by. The protocol sends them as
/// five numbers a token, each place counted from the one before (the line from the last token's line, the start from
/// its start when they're on the same line), then the length, the token's type as an index into the legend the server
/// gave as it started, and its modifiers as bits of the legend's other list. The standard types are mapped, and the
/// ones Roslyn and csharp-ls send of their own (field, constant, extension method, record, delegate, static symbol, the
/// "class name" style of Roslyn's classifications), so C# comes out the way ReSharper colours it. What a grammar
/// colours fine already (keywords, texts, comments, numbers, operators) isn't a kind here, the grammar keeps it.
/// </summary>
public static class LanguageServerSemantics
{
    /// <summary>The token types the client says it knows, the protocol's own and the custom ones mapped below.</summary>
    public static readonly string[] TokenTypes =
    [
        "namespace", "type", "class", "enum", "interface", "struct", "typeParameter", "parameter", "variable", "property",
        "enumMember", "event", "function", "method", "macro", "keyword", "modifier", "comment", "string", "number", "regexp",
        "operator", "decorator", "label",
        "field", "constant", "extensionMethod", "record", "recordStruct", "delegate", "staticSymbol", "local", "template", "alias", "union"
    ];

    /// <summary>The token modifiers the client says it knows.</summary>
    public static readonly string[] TokenModifiers =
    [
        "declaration", "definition", "readonly", "static", "deprecated", "abstract", "async", "modification", "documentation",
        "defaultLibrary", "mutable", "immutable", "const"
    ];

    /// <summary>
    /// The spans a server's numbers come to, with its legend. A token of a type that isn't a name is left out, a static
    /// symbol (Roslyn sends one on top of the name it's about) goes onto that name as static. Numbers that run out half
    /// way through a token are the end of it.
    /// </summary>
    public static List<SemanticSpan> Decode(IReadOnlyList<int> data, IReadOnlyList<string> types, IReadOnlyList<string> modifiers)
    {
        var found = new List<SemanticSpan>(data.Count / 5);
        int line = 0, start = 0;

        // A modifier with no kind of its own waiting for the name at the same place
        (int Line, int Start, int Length, SyntaxModifiers Modifiers)? pending = null;

        for (int i = 0; i + 4 < data.Count; i += 5)
        {
            int deltaLine = data[i], deltaStart = data[i + 1], length = data[i + 2], type = data[i + 3], bits = data[i + 4];
            line += deltaLine;
            start = deltaLine == 0 ? start + deltaStart : deltaStart;
            if (length <= 0 || type < 0 || type >= types.Count)
                continue;

            SyntaxModifiers with = ModifiersOf(bits, modifiers);
            (SyntaxKind? kind, SyntaxModifiers extra) = KindOf(types[type]);
            with |= extra;

            if (kind is not { } named)
            {
                if (extra == SyntaxModifiers.None)
                    continue;

                // A static symbol after its name, or before it
                if (found.Count > 0 && found[^1] is var last && last.Line == line && last.Start == start && last.Length == length)
                    found[^1] = last with { Modifiers = last.Modifiers | extra };
                else
                    pending = (line, start, length, extra);
                continue;
            }

            // Only a local or a parameter is mutable the way ReSharper means it, a property written to is just written to
            if (named is not (SyntaxKind.Local or SyntaxKind.Parameter))
                with &= ~SyntaxModifiers.Mutable;

            if (pending is { } waiting && waiting.Line == line && waiting.Start == start && waiting.Length == length)
                with |= waiting.Modifiers;
            pending = null;

            // The same place said twice, the second is the more particular
            if (found.Count > 0 && found[^1] is var before && before.Line == line && before.Start == start && before.Length == length)
                found[^1] = new SemanticSpan(line, start, length, named, before.Modifiers | with);
            else
                found.Add(new SemanticSpan(line, start, length, named, with));
        }

        return found;
    }

    /// <summary>The modifiers a token's bits say, by the legend's names.</summary>
    public static SyntaxModifiers ModifiersOf(int bits, IReadOnlyList<string> modifiers)
    {
        SyntaxModifiers with = SyntaxModifiers.None;
        for (int bit = 0; bit < modifiers.Count && bit < 31 && bits >> bit != 0; bit++)
        {
            if ((bits & (1 << bit)) == 0)
                continue;

            with |= modifiers[bit] switch
            {
                "static" => SyntaxModifiers.Static,
                "readonly" or "immutable" or "const" => SyntaxModifiers.Readonly,
                "deprecated" => SyntaxModifiers.Deprecated,
                "declaration" or "definition" => SyntaxModifiers.Declaration,
                "mutable" or "modification" => SyntaxModifiers.Mutable,
                _ => SyntaxModifiers.None
            };
        }

        return with;
    }

    /// <summary>
    /// The kind a token type is and anything it says besides, null for a type that isn't a name (a keyword, a comment) or
    /// one that only says something about a name (a static symbol). Roslyn's names ("class name", "field name",
    /// "extension method name", "static symbol") are taken the same as the protocol's.
    /// </summary>
    public static (SyntaxKind? Kind, SyntaxModifiers Extra) KindOf(string type)
    {
        string name = Normalised(type);
        SyntaxKind? kind = name switch
        {
            "namespace" or "module" or "package" => SyntaxKind.Namespace,
            "class" or "type" or "template" or "alias" or "typealias" or "typedef" or "concept" => SyntaxKind.Class,
            "record" or "recordclass" or "recordstruct" => SyntaxKind.Record,
            "struct" or "union" => SyntaxKind.Struct,
            "interface" => SyntaxKind.Interface,
            "enum" => SyntaxKind.Enum,
            "enummember" => SyntaxKind.EnumMember,
            "delegate" => SyntaxKind.Delegate,
            "typeparameter" => SyntaxKind.TypeParameter,
            "method" or "function" or "member" => SyntaxKind.Method,
            "extensionmethod" => SyntaxKind.ExtensionMethod,
            "property" => SyntaxKind.Property,
            "field" => SyntaxKind.Field,
            "constant" => SyntaxKind.NamedConstant,
            "event" => SyntaxKind.Event,
            "variable" or "local" => SyntaxKind.Local,
            "parameter" => SyntaxKind.Parameter,
            "label" => SyntaxKind.Label,
            _ => null
        };

        if (name == "staticsymbol")
            return (null, SyntaxModifiers.Static);

        return (kind, SyntaxModifiers.None);
    }

    // A token type as one word in lower case without Roslyn's " name" on the end, "extension method name" is extensionmethod
    private static string Normalised(string type)
    {
        Span<char> word = stackalloc char[Math.Min(type.Length, 64)];
        int length = 0;
        foreach (char character in type)
        {
            if (char.IsLetter(character) && length < word.Length)
                word[length++] = char.ToLowerInvariant(character);
        }

        ReadOnlySpan<char> said = word[..length];
        if (said.Length > 4 && said.EndsWith("name"))
            said = said[..^4];
        return said.ToString();
    }
}
