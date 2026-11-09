using Horizon.HIDL;

using TextMateSharp.Grammars;

namespace Horizon.UI.Scripting;

/// <summary>
/// How a language writes a comment, the mark a line comment starts with and the two a block comment goes between.
/// Empty when the language has no such thing (CSS has no line comment, Python no block one).
/// </summary>
public readonly record struct CodeComment(string Line, string BlockStart, string BlockEnd)
{
    /// <summary>Plain text, no comments at all.</summary>
    public static readonly CodeComment None = new(string.Empty, string.Empty, string.Empty);

    public bool HasLine => Line.Length > 0;

    public bool HasBlock => BlockStart.Length > 0 && BlockEnd.Length > 0;
}

/// <summary>
/// The comments of every language the code editor colours, by its grammar's scope (source.python) or its language id
/// (python), for Ctrl+/ and Shift+Alt+A. The languages people open every day are in a table here, which is instant.
/// Anything else is asked of the language-configuration.json VS Code ships next to its grammar, read once a language
/// and kept, and a language neither knows has none.
/// </summary>
public static class CodeComments
{
    private static readonly CodeComment CLike = new("//", "/*", "*/");
    private static readonly CodeComment Hash = new("#", string.Empty, string.Empty);
    private static readonly CodeComment Markup = new(string.Empty, "<!--", "-->");
    private static readonly CodeComment Semicolon = new(";", string.Empty, string.Empty);

    // By language id and by the words a grammar's scope is made of (source.cs is cs, text.html.basic is html)
    private static readonly Dictionary<string, CodeComment> known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["c"] = CLike, ["cpp"] = CLike, ["cs"] = CLike, ["csharp"] = CLike, ["java"] = CLike, ["js"] = CLike,
        ["javascript"] = CLike, ["jsx"] = CLike, ["javascriptreact"] = CLike, ["ts"] = CLike, ["typescript"] = CLike,
        ["tsx"] = CLike, ["typescriptreact"] = CLike, ["go"] = CLike, ["rust"] = CLike, ["swift"] = CLike,
        ["kotlin"] = CLike, ["scala"] = CLike, ["dart"] = CLike, ["php"] = CLike, ["groovy"] = CLike, ["d"] = CLike,
        ["hlsl"] = CLike, ["slang"] = CLike, ["glsl"] = CLike, ["shaderlab"] = CLike, ["objc"] = CLike,
        ["objcpp"] = CLike, ["objective-c"] = CLike, ["objective-cpp"] = CLike, ["json"] = CLike, ["jsonc"] = CLike,
        ["less"] = CLike, ["scss"] = CLike, ["hidl"] = CLike, ["cuda"] = CLike, ["proto"] = CLike,
        ["fsharp"] = new("//", "(*", "*)"),
        ["css"] = new(string.Empty, "/*", "*/"),

        ["python"] = Hash, ["py"] = Hash, ["shell"] = Hash, ["shellscript"] = Hash, ["sh"] = Hash, ["bash"] = Hash,
        ["yaml"] = Hash, ["toml"] = Hash, ["ruby"] = Hash, ["perl"] = Hash, ["r"] = Hash, ["dockerfile"] = Hash,
        ["makefile"] = Hash, ["make"] = Hash, ["cmake"] = Hash, ["ignore"] = Hash, ["properties"] = Hash,
        ["elixir"] = Hash, ["nim"] = Hash, ["coffee"] = new("#", "###", "###"),
        ["powershell"] = new("#", "<#", "#>"),

        ["sql"] = new("--", "/*", "*/"),
        ["lua"] = new("--", "--[[", "]]"),
        ["haskell"] = new("--", "{-", "-}"),

        ["html"] = Markup, ["xml"] = Markup, ["xsl"] = Markup, ["markdown"] = Markup, ["vue"] = Markup, ["svg"] = Markup,

        ["ini"] = Semicolon, ["asm"] = Semicolon, ["lisp"] = Semicolon, ["clojure"] = Semicolon, ["scheme"] = Semicolon,

        ["latex"] = new("%", string.Empty, string.Empty), ["tex"] = new("%", string.Empty, string.Empty),
        ["bibtex"] = new("%", string.Empty, string.Empty), ["erlang"] = new("%", string.Empty, string.Empty),
        ["matlab"] = new("%", "%{", "%}"),

        ["bat"] = new("REM", string.Empty, string.Empty), ["batchfile"] = new("REM", string.Empty, string.Empty),
        ["vb"] = new("'", string.Empty, string.Empty)
    };

    private static readonly Lock gate = new();

    // What VS Code's language configurations said, by scope, kept for the run
    private static readonly Dictionary<string, CodeComment> asked = new(StringComparer.Ordinal);
    private static RegistryOptions? bundled;

    /// <summary>The comments of the language a highlighter colours, none for plain text.</summary>
    public static CodeComment For(ICodeHighlighter? highlighter) => highlighter switch
    {
        null => CodeComment.None,
        HidlHighlighter => CLike,
        TextMateHighlighter textMate => For(textMate.ScopeName),
        _ => CodeComment.None
    };

    /// <summary>The comments of a language by its grammar's scope (source.python) or its id (python), none for one nobody knows.</summary>
    public static CodeComment For(string scopeOrId)
    {
        if (string.IsNullOrEmpty(scopeOrId))
            return CodeComment.None;
        if (scopeOrId == HIDLGrammar.ScopeName)
            return CLike;
        if (known.TryGetValue(scopeOrId, out CodeComment exact))
            return exact;
        if (FromScope(scopeOrId) is { } fromTable)
            return fromTable;

        lock (gate)
        {
            if (!asked.TryGetValue(scopeOrId, out CodeComment found))
                asked[scopeOrId] = found = FromConfiguration(scopeOrId);
            return found;
        }
    }

    // The words of a scope from the last back (text.html.markdown is markdown before html, source.json.comments is json)
    private static CodeComment? FromScope(string scope)
    {
        string[] words = scope.Split('.');
        for (int i = words.Length - 1; i >= 1; i--)
        {
            if (known.TryGetValue(words[i], out CodeComment comment))
                return comment;
        }

        return null;
    }

    // The comments the language's language-configuration.json gives, for a grammar's scope or a language's id
    private static CodeComment FromConfiguration(string scopeOrId)
    {
        try
        {
            bundled ??= new RegistryOptions(ThemeName.DarkPlus);
            foreach (Language language in bundled.GetAvailableLanguages())
            {
                if (language.Id != scopeOrId && bundled.GetScopeByLanguageId(language.Id) != scopeOrId)
                    continue;
                if (language.Configuration?.Comments is not { } comments)
                    continue;

                string line = comments.LineComment ?? string.Empty;
                bool block = comments.BlockComment is { Count: 2 } pair && pair[0].Length > 0 && pair[1].Length > 0;
                return new CodeComment(line, block ? comments.BlockComment![0] : string.Empty, block ? comments.BlockComment![1] : string.Empty);
            }
        }
        catch (Exception)
        {
            // A package that won't read is a language without comments, not a crash on Ctrl+/
        }

        return CodeComment.None;
    }
}
