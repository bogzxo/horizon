namespace Horizon.UI.Components;

/// <summary>
/// The Codicon in front of a completion, by what kind of thing it is (<see cref="UICompletion.Kind"/>, a language
/// server's kind as a word), VS Code's symbol icons. The list was words alone, a keyword and a method looked the same.
/// </summary>
public static class UICompletionKinds
{
    /// <summary>The icon of a kind, empty for a completion that doesn't say what it is.</summary>
    public static string IconOf(string kind) => kind switch
    {
        "" => string.Empty,
        "text" => "symbol-text",
        "method" => "symbol-method",
        "function" => "symbol-function",
        "constructor" => "symbol-constructor",
        "field" => "symbol-field",
        "variable" => "symbol-variable",
        "class" => "symbol-class",
        "interface" => "symbol-interface",
        "module" => "symbol-namespace",
        "property" => "symbol-property",
        "unit" => "symbol-unit",
        "value" => "symbol-value",
        "enum" => "symbol-enum",
        "keyword" => "symbol-keyword",
        "snippet" => "symbol-snippet",
        "colour" or "color" => "symbol-color",
        "file" => "symbol-file",
        "reference" => "symbol-reference",
        "folder" => "symbol-folder",
        "enum member" => "symbol-enum-member",
        "constant" => "symbol-constant",
        "struct" => "symbol-structure",
        "event" => "symbol-event",
        "operator" => "symbol-operator",
        "type parameter" => "symbol-type-parameter",
        "string" => "symbol-string",
        _ => "symbol-misc"
    };
}
