using System.Collections.Concurrent;

namespace Horizon.UI.Scripting;

/// <summary>
/// Turns the scopes a TextMate grammar puts on a token (comment.line.double-slash.cs, entity.name.function.python,
/// string.quoted.double.json) into the one kind the skin has a colour for. VS Code does this with a theme, a long list
/// of scope selectors with a colour each, and a skin here has twenty colours of code and no idea of a scope, so the
/// scopes are matched by prefix instead, the deepest scope of the token first and the longest prefix that fits it,
/// and every skin's themes colour every language without a TextMate theme to their name. A comment is a comment
/// whatever is inside of it, and whatever is in a text that isn't an escape or a placeholder is text.
/// </summary>
public static class SyntaxScopes
{
    // Longest prefixes first, so entity.name.function.preprocessor is found before entity.name.function. A scope
    // matches a prefix when it is the prefix or starts with it and a dot
    private static readonly (string Prefix, SyntaxKind Kind)[] Kinds = Sorted(
    [
        ("comment", SyntaxKind.Comment),

        // Texts, their escapes and what is written into them for something else to read
        ("string.other.link", SyntaxKind.Reference),
        ("string", SyntaxKind.Text),
        ("constant.character.escape", SyntaxKind.Escape),
        ("constant.other.placeholder", SyntaxKind.Markup),
        ("constant.character", SyntaxKind.Text),
        ("constant.numeric", SyntaxKind.Number),
        ("constant.language", SyntaxKind.Constant),
        ("constant", SyntaxKind.Constant),

        // The preprocessor, C's and HIDL's
        ("meta.preprocessor", SyntaxKind.Directive),
        ("keyword.control.directive", SyntaxKind.Directive),
        ("keyword.preprocessor", SyntaxKind.Directive),
        ("keyword.other.preprocessor", SyntaxKind.Directive),
        ("entity.name.function.preprocessor", SyntaxKind.Constant),
        ("entity.name.constant", SyntaxKind.Constant),

        // Keywords, and the operators that are words
        ("keyword.operator.expression", SyntaxKind.Keyword),
        ("keyword.operator.new", SyntaxKind.Keyword),
        ("keyword.operator.delete", SyntaxKind.Keyword),
        ("keyword.operator.logical.python", SyntaxKind.Keyword),
        ("keyword.operator.wordlike", SyntaxKind.Keyword),
        ("keyword.operator", SyntaxKind.Operator),
        ("keyword.other.unit", SyntaxKind.Number),
        ("keyword", SyntaxKind.Keyword),
        ("storage", SyntaxKind.Keyword),
        ("variable.language", SyntaxKind.Keyword),

        // Names, by what they are
        ("entity.name.function", SyntaxKind.Call),
        ("entity.name.type", SyntaxKind.Type),
        ("entity.name.class", SyntaxKind.Type),
        ("entity.name.struct", SyntaxKind.Type),
        ("entity.name.interface", SyntaxKind.Type),
        ("entity.name.enum", SyntaxKind.Type),
        ("entity.name.namespace", SyntaxKind.Type),
        ("entity.name.scope-resolution", SyntaxKind.Type),
        ("entity.other.inherited-class", SyntaxKind.Type),
        ("entity.name.tag", SyntaxKind.Keyword),
        ("entity.other.attribute-name", SyntaxKind.Property),
        ("entity.name.selector", SyntaxKind.Call),
        ("entity.name", SyntaxKind.Declaration),
        ("variable.parameter", SyntaxKind.Declaration),
        ("variable.other.constant", SyntaxKind.Constant),
        ("variable.other.enummember", SyntaxKind.Constant),
        ("variable.other.property", SyntaxKind.Property),
        ("variable.other.member", SyntaxKind.Property),
        ("variable.other.field", SyntaxKind.Property),
        ("variable.other.object.property", SyntaxKind.Property),
        ("variable.function", SyntaxKind.Call),
        ("variable", SyntaxKind.Name),
        ("support.function.builtin", SyntaxKind.Builtin),
        ("support.function", SyntaxKind.Call),
        ("support.type.property-name", SyntaxKind.Property),
        ("support.type", SyntaxKind.Type),
        ("support.class", SyntaxKind.Type),
        ("support.constant", SyntaxKind.Constant),
        ("support", SyntaxKind.Builtin),
        ("meta.function-call.generic", SyntaxKind.Call),
        ("invalid", SyntaxKind.Number),

        // Markdown and the like
        ("markup.heading", SyntaxKind.Declaration),
        ("markup.bold", SyntaxKind.Keyword),
        ("markup.italic", SyntaxKind.Property),
        ("markup.underline", SyntaxKind.Reference),
        ("markup.inline.raw", SyntaxKind.Text),
        ("markup.raw", SyntaxKind.Text),
        ("markup.fenced_code", SyntaxKind.Text),
        ("markup.quote", SyntaxKind.Comment),
        ("markup.strikethrough", SyntaxKind.Comment),
        ("markup.inserted", SyntaxKind.Text),
        ("markup.deleted", SyntaxKind.Number),
        ("markup.changed", SyntaxKind.Property),

        // Punctuation, which mostly takes the colour of what it belongs to
        ("punctuation.definition.comment", SyntaxKind.Comment),
        ("punctuation.definition.directive", SyntaxKind.Directive),
        ("punctuation.definition.string", SyntaxKind.Text),
        ("punctuation.definition.heading", SyntaxKind.Declaration),
        ("punctuation.definition.list", SyntaxKind.Operator),
        ("punctuation.definition.bold", SyntaxKind.Keyword),
        ("punctuation.definition.italic", SyntaxKind.Property),
        ("punctuation.support.type.property-name", SyntaxKind.Property),
        ("punctuation", SyntaxKind.Punctuation)
    ]);

    // What a scope came out as, kept, the grammars hand out the same strings over and over
    private static readonly ConcurrentDictionary<string, SyntaxKind> known = new(StringComparer.Ordinal);

    // The kinds that are a name of some sort, which the semantic layer of a language may know better
    private const SyntaxKind NOT_A_NAME = SyntaxKind.Text;

    private static (string, SyntaxKind)[] Sorted((string, SyntaxKind)[] kinds)
    {
        Array.Sort(kinds, static (a, b) => b.Item1.Length.CompareTo(a.Item1.Length));
        return kinds;
    }

    /// <summary>
    /// The kind of a token with these scopes, the outermost first the way a grammar lists them.
    /// </summary>
    public static SyntaxKind KindOf(IReadOnlyList<string> scopes)
    {
        // A comment is a comment all the way down
        for (int i = 1; i < scopes.Count; i++)
        {
            if (Matches(scopes[i], "comment"))
                return SyntaxKind.Comment;
        }

        // The deepest scope that says anything, the first scope is the language itself (source.cs) and says nothing
        for (int i = scopes.Count - 1; i >= 1; i--)
        {
            SyntaxKind kind = known.GetOrAdd(scopes[i], static scope => Lookup(scope));
            if (kind != NOT_FOUND)
                return kind;
        }

        return SyntaxKind.Name;
    }

    /// <summary>
    /// Whether a bracket in a token with these scopes is a bracket of the code, to be coloured by its depth, and not
    /// one written inside of a text, a comment or a markdown link.
    /// </summary>
    public static bool HoldsBrackets(IReadOnlyList<string> scopes)
    {
        for (int i = 1; i < scopes.Count; i++)
        {
            string scope = scopes[i];
            if (Matches(scope, "string") || Matches(scope, "comment") || scope.StartsWith("markup.", StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    /// <summary>Whether a kind is a name of some sort, which a language's own lexer may know more about than its grammar.</summary>
    public static bool IsName(SyntaxKind kind) => kind is SyntaxKind.Name or SyntaxKind.Property or SyntaxKind.Call or SyntaxKind.Declaration
        or SyntaxKind.Builtin or SyntaxKind.Type or SyntaxKind.Keyword or SyntaxKind.Constant;

    // Stands in for a scope no prefix fits, which is kept too so it isn't looked for again
    private const SyntaxKind NOT_FOUND = (SyntaxKind)(-1);

    private static SyntaxKind Lookup(string scope)
    {
        foreach (var (prefix, kind) in Kinds)
        {
            if (Matches(scope, prefix))
                return kind;
        }

        return NOT_FOUND;
    }

    private static bool Matches(string scope, string prefix) =>
        scope.StartsWith(prefix, StringComparison.Ordinal) && (scope.Length == prefix.Length || scope[prefix.Length] == '.');
}
