using System.Numerics;

using Horizon.UI.Scripting;

namespace Horizon.UI.Skinning;

/// <summary>
/// The colours a skin shows code in, one for every kind of thing the code is cut into (<see cref="SyntaxKind"/>), whatever the language.
/// A skin file sets them under syntax, and only the ones it wants different from these.
/// </summary>
public sealed class UISyntaxColors
{
    public Vector4 Name { get; set; } = new(0.84f, 0.87f, 0.93f, 1.0f);
    public Vector4 Keyword { get; set; } = new(0.78f, 0.57f, 0.95f, 1.0f);
    public Vector4 Property { get; set; } = new(0.49f, 0.78f, 0.98f, 1.0f);
    public Vector4 Call { get; set; } = new(0.98f, 0.82f, 0.48f, 1.0f);
    public Vector4 Text { get; set; } = new(0.62f, 0.86f, 0.55f, 1.0f);
    public Vector4 Number { get; set; } = new(0.98f, 0.63f, 0.47f, 1.0f);
    public Vector4 Comment { get; set; } = new(0.45f, 0.5f, 0.58f, 1.0f);
    public Vector4 Punctuation { get; set; } = new(0.6f, 0.65f, 0.74f, 1.0f);

    /// <summary>A directive, a line that starts with a #.</summary>
    public Vector4 Directive { get; set; } = new(0.95f, 0.55f, 0.75f, 1.0f);

    /// <summary>A name being made, after let, const or func, and a function's parameters.</summary>
    public Vector4 Declaration { get; set; } = new(0.55f, 0.88f, 1.0f, 1.0f);

    /// <summary>What the language comes with, print, include, math, vec, and the compositor.</summary>
    public Vector4 Builtin { get; set; } = new(0.95f, 0.72f, 0.42f, 1.0f);

    /// <summary>The kind of a component, the button of compositor.button.</summary>
    public Vector4 Type { get; set; } = new(0.38f, 0.85f, 0.72f, 1.0f);

    /// <summary>true, false, null, and the name a directive defines.</summary>
    public Vector4 Constant { get; set; } = new(0.45f, 0.65f, 1.0f, 1.0f);

    /// <summary>A text that is a key of a game's language, "@main_menu.quit".</summary>
    public Vector4 Reference { get; set; } = new(0.92f, 0.62f, 0.92f, 1.0f);

    /// <summary>An escape in a text.</summary>
    public Vector4 Escape { get; set; } = new(0.95f, 0.85f, 0.5f, 1.0f);

    /// <summary>The markup a label reads in a text, [b], $(save).</summary>
    public Vector4 Markup { get; set; } = new(0.47f, 0.68f, 0.47f, 1.0f);

    public Vector4 Operator { get; set; } = new(0.72f, 0.76f, 0.86f, 1.0f);

    /// <summary>The brackets by how deep they are, round and round, VS Code's gold, orchid and blue.</summary>
    public Vector4 Bracket1 { get; set; } = new(1.0f, 0.84f, 0.0f, 1.0f);
    public Vector4 Bracket2 { get; set; } = new(0.85f, 0.44f, 0.84f, 1.0f);
    public Vector4 Bracket3 { get; set; } = new(0.09f, 0.62f, 1.0f, 1.0f);

    /// <summary>The numbers down the side of a code view.</summary>
    public Vector4 LineNumber { get; set; } = new(0.34f, 0.38f, 0.46f, 1.0f);

    // Null until something sets it, and then the keywords' colour
    private Vector4? control;

    /// <summary>The keywords that send the code somewhere else, if, for, return. The other keywords' colour unless set.</summary>
    public Vector4 Control { get => control ?? Keyword; set => control = value; }

    /// <summary>
    /// The colours for code on a light field, deep where the defaults are bright, the flat skin's light theme is
    /// these. What a palette without colours of code of its own gets when its field is light.
    /// </summary>
    public static UISyntaxColors Light() => new()
    {
        Name = new(0.13f, 0.15f, 0.2f, 1.0f),
        Keyword = new(0.5f, 0.12f, 0.7f, 1.0f),
        Property = new(0.05f, 0.35f, 0.65f, 1.0f),
        Call = new(0.55f, 0.36f, 0.0f, 1.0f),
        Text = new(0.12f, 0.45f, 0.12f, 1.0f),
        Number = new(0.7f, 0.26f, 0.08f, 1.0f),
        Comment = new(0.45f, 0.48f, 0.53f, 1.0f),
        Punctuation = new(0.32f, 0.35f, 0.42f, 1.0f),
        LineNumber = new(0.55f, 0.58f, 0.63f, 1.0f),
        Directive = new(0.65f, 0.12f, 0.42f, 1.0f),
        Declaration = new(0.0f, 0.3f, 0.55f, 1.0f),
        Builtin = new(0.55f, 0.32f, 0.0f, 1.0f),
        Type = new(0.0f, 0.45f, 0.4f, 1.0f),
        Constant = new(0.0f, 0.28f, 0.75f, 1.0f),
        Reference = new(0.55f, 0.08f, 0.55f, 1.0f),
        Escape = new(0.55f, 0.4f, 0.0f, 1.0f),
        Markup = new(0.28f, 0.45f, 0.28f, 1.0f),
        Operator = new(0.28f, 0.31f, 0.38f, 1.0f),
        Bracket1 = new(0.62f, 0.48f, 0.0f, 1.0f),
        Bracket2 = new(0.55f, 0.15f, 0.55f, 1.0f),
        Bracket3 = new(0.0f, 0.36f, 0.75f, 1.0f)
    };

    /// <summary>
    /// Visual Studio's colours for code on its dark theme, the ones it has for C#. Visual Studio doesn't colour brackets
    /// by how deep they are, so all three are its punctuation.
    /// </summary>
    public static UISyntaxColors VisualStudioDark() => new()
    {
        Name = Rgb(0xDCDCDC),
        Keyword = Rgb(0x569CD6),
        Control = Rgb(0xD8A0DF),
        Property = Rgb(0xDCDCDC),
        Call = Rgb(0xDCDCAA),
        Text = Rgb(0xD69D85),
        Number = Rgb(0xB5CEA8),
        Comment = Rgb(0x57A64A),
        Punctuation = Rgb(0xB4B4B4),
        LineNumber = Rgb(0x2B91AF),
        Directive = Rgb(0x9B9B9B),
        Declaration = Rgb(0x9CDCFE),
        Builtin = Rgb(0x4EC9B0),
        Type = Rgb(0x4EC9B0),
        Constant = Rgb(0x569CD6),
        Reference = Rgb(0xD69D85),
        Escape = Rgb(0xFFD68F),
        Markup = Rgb(0xB4B4B4),
        Operator = Rgb(0xB4B4B4),
        Bracket1 = Rgb(0xB4B4B4),
        Bracket2 = Rgb(0xB4B4B4),
        Bracket3 = Rgb(0xB4B4B4)
    };

    /// <summary>Visual Studio's colours for code on its light theme, the ones it has for C#.</summary>
    public static UISyntaxColors VisualStudioLight() => new()
    {
        Name = Rgb(0x000000),
        Keyword = Rgb(0x0000FF),
        Control = Rgb(0x8F08C4),
        Property = Rgb(0x000000),
        Call = Rgb(0x74531F),
        Text = Rgb(0xA31515),
        Number = Rgb(0x000000),
        Comment = Rgb(0x008000),
        Punctuation = Rgb(0x000000),
        LineNumber = Rgb(0x2B91AF),
        Directive = Rgb(0x808080),
        Declaration = Rgb(0x1F377F),
        Builtin = Rgb(0x2B91AF),
        Type = Rgb(0x2B91AF),
        Constant = Rgb(0x0000FF),
        Reference = Rgb(0xA31515),
        Escape = Rgb(0xB776FB),
        Markup = Rgb(0x000000),
        Operator = Rgb(0x000000),
        Bracket1 = Rgb(0x000000),
        Bracket2 = Rgb(0x000000),
        Bracket3 = Rgb(0x000000)
    };

    // A colour written the way a theme file writes it, 0xRRGGBB
    private static Vector4 Rgb(uint rgb) => new(((rgb >> 16) & 0xFF) / 255.0f, ((rgb >> 8) & 0xFF) / 255.0f, (rgb & 0xFF) / 255.0f, 1.0f);

    /// <summary>Sets a colour by the key a skin file writes it under, false for a key that isn't one.</summary>
    public bool Set(string key, Vector4 color)
    {
        switch (key)
        {
            case "name": Name = color; break;
            case "keyword": Keyword = color; break;
            case "property": Property = color; break;
            case "call": Call = color; break;
            case "text": Text = color; break;
            case "number": Number = color; break;
            case "comment": Comment = color; break;
            case "punctuation": Punctuation = color; break;
            case "line_number": LineNumber = color; break;
            case "directive": Directive = color; break;
            case "declaration": Declaration = color; break;
            case "builtin": Builtin = color; break;
            case "type": Type = color; break;
            case "constant": Constant = color; break;
            case "reference": Reference = color; break;
            case "escape": Escape = color; break;
            case "markup": Markup = color; break;
            case "operator": Operator = color; break;
            case "bracket1": Bracket1 = color; break;
            case "bracket2": Bracket2 = color; break;
            case "bracket3": Bracket3 = color; break;
            case "control": Control = color; break;
            default: return false;
        }

        return true;
    }

    /// <summary>Writes every colour into another set.</summary>
    public void CopyTo(UISyntaxColors other)
    {
        other.Name = Name; other.Keyword = Keyword; other.Property = Property; other.Call = Call; other.Text = Text;
        other.Number = Number; other.Comment = Comment; other.Punctuation = Punctuation; other.LineNumber = LineNumber;
        other.Directive = Directive; other.Declaration = Declaration; other.Builtin = Builtin; other.Type = Type;
        other.Constant = Constant; other.Reference = Reference; other.Escape = Escape; other.Markup = Markup;
        other.Operator = Operator; other.Bracket1 = Bracket1; other.Bracket2 = Bracket2; other.Bracket3 = Bracket3;
        other.control = control;
    }

    public Vector4 Of(SyntaxKind kind) => kind switch
    {
        SyntaxKind.Keyword => Keyword,
        SyntaxKind.Control => Control,
        SyntaxKind.Property => Property,
        SyntaxKind.Call => Call,
        SyntaxKind.Text => Text,
        SyntaxKind.Number => Number,
        SyntaxKind.Comment => Comment,
        SyntaxKind.Punctuation => Punctuation,
        SyntaxKind.Directive => Directive,
        SyntaxKind.Declaration => Declaration,
        SyntaxKind.Builtin => Builtin,
        SyntaxKind.Type => Type,
        SyntaxKind.Constant => Constant,
        SyntaxKind.Reference => Reference,
        SyntaxKind.Escape => Escape,
        SyntaxKind.Markup => Markup,
        SyntaxKind.Operator => Operator,
        SyntaxKind.Bracket1 => Bracket1,
        SyntaxKind.Bracket2 => Bracket2,
        SyntaxKind.Bracket3 => Bracket3,
        _ => Name
    };
}
