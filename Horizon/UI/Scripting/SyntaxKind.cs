namespace Horizon.UI.Scripting;

/// <summary>
/// What a piece of code is, as far as colouring it in goes. A skin has a colour for each of these
/// (<see cref="Skinning.UISyntaxColors"/>), and every language is read into them, HIDL by its own grammar and
/// the lexer behind it, everything else by the TextMate grammars VS Code ships with, their scopes mapped onto these
/// kinds (<see cref="SyntaxScopes"/>). So a theme that colours code colours every language at once.
/// </summary>
public enum SyntaxKind
{
    // A name that is nothing more than a name, a variable being used most of the time, and plain text
    Name,

    // let, const, func and the rest of the words a language keeps to itself
    Keyword,

    // The key of an object ("padding" in `padding: 8`) or something reached with a dot
    Property,

    // Anything that is called, which is whatever has a bracket after it, and a function being declared in most languages
    Call,

    Text,
    Number,
    Comment,

    // Commas, colons, semicolons and whatever else is left
    Punctuation,

    // A line that starts with a #, the #define part of #define layout, a C preprocessor line
    Directive,

    // A name being made, the one after let, const or func, a function's parameters, a markdown heading
    Declaration,

    // What the language comes with, print, include, math, vec, and the compositor a layout is made with
    Builtin,

    // The kind of a component, the button of compositor.button, and a class or a type in another language
    Type,

    // true, false, null, and the name a directive defines
    Constant,

    // A text that is a key of a game's language, "@main_menu.quit", and a link
    Reference,

    // An escape in a text, \n and \"
    Escape,

    // The markup in a text a label reads, [b], [color:red], $(save), [icon:pad_a], and a placeholder in another language's text
    Markup,

    // + - * / = == != ! ? ... and the rest
    Operator,

    // A bracket, coloured by how deep it is, three colours round and round
    Bracket1,
    Bracket2,
    Bracket3
}

/// <summary>
/// A stretch of a line of code that is all one kind of thing.
/// </summary>
/// <param name="Start">Where in the line it starts.</param>
public readonly record struct SyntaxSpan(int Start, int Length, SyntaxKind Kind);

/// <summary>
/// Where a line starts, as the last colouring of the whole found it, which is all a highlighter needs to colour that
/// line again on its own while it is typed into (<see cref="ICodeHighlighter.ReadLine"/>).
/// </summary>
/// <param name="Depth">How deep in brackets the line starts, for the colours of its brackets and the guides down the indentation.</param>
/// <param name="State">
/// What the highlighter was in the middle of at the end of the line before, a TextMate grammar's rule stack (inside a
/// comment over several lines, inside a template), null for the start of the file. Whatever the highlighter that made it
/// wants, nothing else reads it.
/// </param>
public readonly record struct CodeLineStart(int Depth, object? State)
{
    /// <summary>Where the first line of anything starts.</summary>
    public static readonly CodeLineStart First = new(0, null);
}
