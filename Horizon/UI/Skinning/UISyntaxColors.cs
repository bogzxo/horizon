using System.Numerics;

using Horizon.UI.Scripting;

namespace Horizon.UI.Skinning;

/// <summary>
/// The colours a skin shows code in, one for every kind of thing a <see cref="HidlHighlighter"/> tells apart.
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

    public Vector4 Of(HidlToken kind) => kind switch
    {
        HidlToken.Keyword => Keyword,
        HidlToken.Property => Property,
        HidlToken.Call => Call,
        HidlToken.Text => Text,
        HidlToken.Number => Number,
        HidlToken.Comment => Comment,
        HidlToken.Punctuation => Punctuation,
        HidlToken.Directive => Directive,
        HidlToken.Declaration => Declaration,
        HidlToken.Builtin => Builtin,
        HidlToken.Type => Type,
        HidlToken.Constant => Constant,
        HidlToken.Reference => Reference,
        HidlToken.Escape => Escape,
        HidlToken.Markup => Markup,
        HidlToken.Operator => Operator,
        HidlToken.Bracket1 => Bracket1,
        HidlToken.Bracket2 => Bracket2,
        HidlToken.Bracket3 => Bracket3,
        _ => Name
    };
}
