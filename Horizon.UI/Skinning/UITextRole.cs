namespace Horizon.UI.Skinning;

/// <summary>What a piece of text is for, which a skin has a size for (UISkin.TextSize).</summary>
public enum UITextRole
{
    /// <summary>What is said quietly under something, a path, a count.</summary>
    Caption,

    /// <summary>A sentence, a row, a button, what most text is.</summary>
    Body,

    /// <summary>The name of a section.</summary>
    Heading,

    /// <summary>The name of a whole page.</summary>
    Title
}
