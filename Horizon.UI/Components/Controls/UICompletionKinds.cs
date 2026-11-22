using Horizon.UI.Scripting;

namespace Horizon.UI.Components;

/// <summary>
/// The icon and syntax colour of a completion, based on its kind (<see cref="UICompletion.Kind"/>).
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

    /// <summary>The syntax colour used for a completion's name and icon.</summary>
    public static SyntaxKind SyntaxOf(string kind) => kind switch
    {
        "method" or "function" => SyntaxKind.Method,
        "extension method" => SyntaxKind.ExtensionMethod,
        "constructor" => SyntaxKind.Type,
        "property" => SyntaxKind.Property,
        "field" => SyntaxKind.Field,
        "event" => SyntaxKind.Event,
        "variable" => SyntaxKind.Local,
        "parameter" => SyntaxKind.Parameter,
        "class" => SyntaxKind.Class,
        "struct" => SyntaxKind.Struct,
        "interface" => SyntaxKind.Interface,
        "enum" => SyntaxKind.Enum,
        "enum member" => SyntaxKind.EnumMember,
        "module" or "namespace" => SyntaxKind.Namespace,
        "type parameter" => SyntaxKind.TypeParameter,
        "constant" => SyntaxKind.NamedConstant,
        "keyword" => SyntaxKind.Keyword,
        "operator" => SyntaxKind.Operator,
        "string" => SyntaxKind.Text,
        "unit" or "value" or "colour" or "color" => SyntaxKind.Constant,
        "reference" => SyntaxKind.Reference,
        _ => SyntaxKind.Name
    };
}
