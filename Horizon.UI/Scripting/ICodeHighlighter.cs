namespace Horizon.UI.Scripting;

/// <summary>
/// What colours code for the code editor, a language at a time. The editor hands the whole text over on a thread of the
/// pool once it has been left alone a moment (<see cref="Read"/>) and, until that comes back, colours the line being
/// typed into on its own from where the last whole colouring said it starts (<see cref="ReadLine"/>). Nothing in here
/// runs on the render thread, so a highlighter may allocate as it reads, the spans it hands back are what is painted.
/// <see cref="TextMateHighlighter"/> is any language VS Code has a grammar for, <see cref="HidlHighlighter"/> is HIDL
/// through its own grammar with what the language's lexer knows laid on top, <see cref="CodeLanguages"/> picks one by
/// the file's extension. A language server's diagnostics, completions and hovers go through the editor's own hooks
/// (Diagnostics, CompletionSource, HoverSource) and not through this, this is colour and nothing else.
/// </summary>
public interface ICodeHighlighter
{
    /// <summary>What the language is called, C#, Python, HIDL, for a status bar.</summary>
    string Name { get; }

    /// <summary>
    /// Cuts code into spans, one list per line. The code has to have its lines ended with '\n' alone.
    /// </summary>
    /// <param name="lineCount">How many lines the code has, which is how many lists come back.</param>
    /// <param name="starts">Where every line starts, filled in when given (as many as there are lines), see <see cref="CodeLineStart"/>.</param>
    /// <param name="swatches">Where the colours written in the code go (a vec given to a colour), null to not look for them.</param>
    /// <param name="art">Where the art named in the code goes (see <see cref="HidlArt"/>), null to not look for it.</param>
    List<SyntaxSpan>[] Read(string code, int lineCount, CodeLineStart[]? starts = null, List<HidlSwatch>? swatches = null, List<HidlArt>? art = null);

    /// <summary>
    /// One line coloured on its own, from where it starts, for a line being typed into while the whole of the code is
    /// coloured again somewhere else. A name whose meaning hangs on the line before (let at the end of it) may come out
    /// a plain name until the whole is coloured.
    /// </summary>
    List<SyntaxSpan> ReadLine(string line, in CodeLineStart start);

    /// <summary>
    /// Colours code again after it was edited, given the last colouring of it. What comes out is what <see cref="Read"/>
    /// would make, one that can do better reads from the first line that changed and stops once a line after the edit
    /// starts the way it did last time, the rest taken over from then. Typing into a file of twenty thousand lines read
    /// every one of them again for every pause, near a second of a core and tens of megabytes of garbage a time.
    /// </summary>
    /// <param name="lines">The lines of <paramref name="code"/>, the same strings the document has.</param>
    /// <param name="starts">Where every line starts, filled in, as many as there are lines.</param>
    List<SyntaxSpan>[] ReadAgain(string code, string[] lines, CodeLineStart[] starts, CodeColouring last, List<HidlSwatch>? swatches = null, List<HidlArt>? art = null) =>
        Read(code, lines.Length, starts, swatches, art);
}

/// <summary>A colouring as it was made, the lines it was of, their spans and where every line started, for the next to go on from.</summary>
public sealed record CodeColouring(string[] Lines, List<SyntaxSpan>[] Spans, CodeLineStart[] Starts);
