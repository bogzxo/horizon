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
        _ => Name
    };
}
