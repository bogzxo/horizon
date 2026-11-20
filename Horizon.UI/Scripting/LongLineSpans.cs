namespace Horizon.UI.Scripting;

/// <summary>
/// The colours of a line too long for a grammar to read whole at once (an agent's chat saved as JSON, minified script),
/// read a piece of a couple of thousand letters at a time with how the grammar was at the start of every piece kept (see
/// <see cref="TextMateHighlighter"/>). So it can be read on from where it was left, a little more every paint until what's
/// in sight is coloured, or read again from just before an edit and taken over from the last read once the grammar is
/// back where it was after it. What's past <see cref="ReadTo"/> isn't read yet and is drawn plain.
/// </summary>
public sealed class LongLineSpans(int length) : List<SyntaxSpan>
{
    /// <summary>How long the line is.</summary>
    public int Length { get; } = length;

    /// <summary>How far into the line it's read, <see cref="Length"/> once all of it is.</summary>
    public int ReadTo { get; internal set; }

    /// <summary>Whether all of the line is read.</summary>
    public bool Whole => ReadTo >= Length;

    // Where every piece read starts and how the grammar was there, the first the line's own start, then how it was at
    // ReadTo, where the next line starts once all of it is read
    internal List<(int At, CodeLineStart Start)> Pieces { get; } = [];
    internal CodeLineStart End { get; set; }
}
