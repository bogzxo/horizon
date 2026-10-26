using System.Numerics;

using Horizon.UI.Scripting;

namespace Horizon.UI.Skinning;

// The colours of what a name is (a class, a field, a parameter), the kinds from Class on. None of them is set unless a
// skin or a palette sets it, and one that isn't is the colour of the closest kind a grammar says, so a skin from before
// these were a thing looks the way it always did with a little more told apart. A class is a type, a method a call, a
// parameter a declaration (Visual Studio's colour for both) and a field a property.
public sealed partial class UISyntaxColors
{
    private const int SEMANTIC = SyntaxKind.Label - SyntaxKind.Class + 1;

    // What's been set of them, by the kind less Class, null for what goes to its fallback
    private readonly Vector4?[] semantic = new Vector4?[SEMANTIC];

    public Vector4 Class { get => OfSemantic(SyntaxKind.Class); set => semantic[SyntaxKind.Class - SyntaxKind.Class] = value; }
    public Vector4 Struct { get => OfSemantic(SyntaxKind.Struct); set => semantic[SyntaxKind.Struct - SyntaxKind.Class] = value; }
    public Vector4 Interface { get => OfSemantic(SyntaxKind.Interface); set => semantic[SyntaxKind.Interface - SyntaxKind.Class] = value; }
    public Vector4 Enum { get => OfSemantic(SyntaxKind.Enum); set => semantic[SyntaxKind.Enum - SyntaxKind.Class] = value; }
    public Vector4 EnumMember { get => OfSemantic(SyntaxKind.EnumMember); set => semantic[SyntaxKind.EnumMember - SyntaxKind.Class] = value; }
    public Vector4 Record { get => OfSemantic(SyntaxKind.Record); set => semantic[SyntaxKind.Record - SyntaxKind.Class] = value; }
    public Vector4 Delegate { get => OfSemantic(SyntaxKind.Delegate); set => semantic[SyntaxKind.Delegate - SyntaxKind.Class] = value; }
    public Vector4 TypeParameter { get => OfSemantic(SyntaxKind.TypeParameter); set => semantic[SyntaxKind.TypeParameter - SyntaxKind.Class] = value; }
    public Vector4 Namespace { get => OfSemantic(SyntaxKind.Namespace); set => semantic[SyntaxKind.Namespace - SyntaxKind.Class] = value; }
    public Vector4 Method { get => OfSemantic(SyntaxKind.Method); set => semantic[SyntaxKind.Method - SyntaxKind.Class] = value; }
    public Vector4 ExtensionMethod { get => OfSemantic(SyntaxKind.ExtensionMethod); set => semantic[SyntaxKind.ExtensionMethod - SyntaxKind.Class] = value; }
    public Vector4 Field { get => OfSemantic(SyntaxKind.Field); set => semantic[SyntaxKind.Field - SyntaxKind.Class] = value; }
    public Vector4 NamedConstant { get => OfSemantic(SyntaxKind.NamedConstant); set => semantic[SyntaxKind.NamedConstant - SyntaxKind.Class] = value; }
    public Vector4 Event { get => OfSemantic(SyntaxKind.Event); set => semantic[SyntaxKind.Event - SyntaxKind.Class] = value; }
    public Vector4 Local { get => OfSemantic(SyntaxKind.Local); set => semantic[SyntaxKind.Local - SyntaxKind.Class] = value; }
    public Vector4 Parameter { get => OfSemantic(SyntaxKind.Parameter); set => semantic[SyntaxKind.Parameter - SyntaxKind.Class] = value; }
    public Vector4 Label { get => OfSemantic(SyntaxKind.Label); set => semantic[SyntaxKind.Label - SyntaxKind.Class] = value; }

    /// <summary>The kind a name's colour comes from when nothing has set its own, one of the kinds a grammar says or one on the way to it.</summary>
    public static SyntaxKind FallbackOf(SyntaxKind kind) => kind switch
    {
        SyntaxKind.Record => SyntaxKind.Class,
        SyntaxKind.Class or SyntaxKind.Struct or SyntaxKind.Interface or SyntaxKind.Enum or SyntaxKind.Delegate or SyntaxKind.TypeParameter => SyntaxKind.Type,
        SyntaxKind.EnumMember or SyntaxKind.NamedConstant => SyntaxKind.Constant,
        SyntaxKind.ExtensionMethod => SyntaxKind.Method,
        SyntaxKind.Method => SyntaxKind.Call,
        SyntaxKind.Field or SyntaxKind.Event => SyntaxKind.Property,
        SyntaxKind.Parameter => SyntaxKind.Declaration,
        _ => SyntaxKind.Name
    };

    /// <summary>
    /// The colour of a span with what else is true of it. A variable of some sort being made (a local, a parameter, a
    /// field, a const) whose kind has no colour of its own is a declaration, which is what it was before names were told
    /// apart, so `let x` keeps its colour in every skin. A type or a method is its own colour declared or used, the way
    /// ReSharper has it.
    /// </summary>
    public Vector4 Of(SyntaxKind kind, SyntaxModifiers modifiers) =>
        (modifiers & SyntaxModifiers.Declaration) != 0 && IsVariable(kind) && !HasOwn(kind) ? Declaration : Of(kind);

    // Whether a kind is a name that holds a value rather than a type or a method
    private static bool IsVariable(SyntaxKind kind) => kind is SyntaxKind.Local or SyntaxKind.Parameter or SyntaxKind.Field or SyntaxKind.NamedConstant
        or SyntaxKind.Event or SyntaxKind.EnumMember or SyntaxKind.Label;

    /// <summary>Whether a name's kind has a colour of its own, or one on its way back to the grammar's kinds has.</summary>
    public bool HasOwn(SyntaxKind kind)
    {
        for (; kind >= SyntaxKind.Class; kind = FallbackOf(kind))
        {
            if (semantic[kind - SyntaxKind.Class] is not null)
                return true;
        }

        return false;
    }

    // A name's colour, its own or its fallback's
    private Vector4 OfSemantic(SyntaxKind kind) =>
        kind >= SyntaxKind.Class && kind <= SyntaxKind.Label && semantic[kind - SyntaxKind.Class] is { } own ? own : Of(FallbackOf(kind));

    // The keys a skin file writes them under, false for one that isn't one
    private bool SetSemantic(string key, Vector4 color)
    {
        SyntaxKind? kind = key switch
        {
            "class" => SyntaxKind.Class,
            "struct" => SyntaxKind.Struct,
            "interface" => SyntaxKind.Interface,
            "enum" => SyntaxKind.Enum,
            "enum_member" => SyntaxKind.EnumMember,
            "record" => SyntaxKind.Record,
            "delegate" => SyntaxKind.Delegate,
            "type_parameter" => SyntaxKind.TypeParameter,
            "namespace" => SyntaxKind.Namespace,
            "method" => SyntaxKind.Method,
            "extension_method" => SyntaxKind.ExtensionMethod,
            "field" => SyntaxKind.Field,
            "named_constant" => SyntaxKind.NamedConstant,
            "event" => SyntaxKind.Event,
            "local" => SyntaxKind.Local,
            "parameter" => SyntaxKind.Parameter,
            "label" => SyntaxKind.Label,
            _ => null
        };

        if (kind is not { } set)
            return false;

        semantic[set - SyntaxKind.Class] = color;
        return true;
    }

    private void CopySemanticTo(UISyntaxColors other) => Array.Copy(semantic, other.semantic, SEMANTIC);
}
