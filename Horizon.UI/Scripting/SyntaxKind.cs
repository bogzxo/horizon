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
    Bracket3,

    // The keywords that send the code somewhere else, if, else, for, return, break, await. A skin colours them as the
    // other keywords unless it says otherwise, Visual Studio has them apart
    Control,

    /* What a name is rather than what it looks like, the way ReSharper colours identifiers. A grammar never says these,
       they come from something that knows the code, a language server's semantic tokens or Hex's own reading of HIDL,
       laid over the grammar's colours (SemanticOverlay). A skin that doesn't colour one gets the closest of the kinds
       above, a class is a type and a method is a call, see UISyntaxColors */

    Class,
    Struct,
    Interface,
    Enum,

    // One of the names of an enum, Color.Red
    EnumMember,
    Record,
    Delegate,

    // The T of List<T>
    TypeParameter,
    Namespace,

    // A method or a function, declared or called
    Method,
    ExtensionMethod,
    Field,

    // A const, a name whose value never changes. Constant above is true, false and null
    NamedConstant,
    Event,

    // A variable of a block, a let inside a function
    Local,
    Parameter,

    // Where a goto goes
    Label,

    // The sixteen colours of a terminal, black to white and then the bright half, for text written with ANSI escapes (what
    // a notebook's code printed, a Python traceback). Drawn in an editor's AnsiColours, the terminal's own, and plain
    // text anywhere else
    Ansi0, Ansi1, Ansi2, Ansi3, Ansi4, Ansi5, Ansi6, Ansi7,
    Ansi8, Ansi9, Ansi10, Ansi11, Ansi12, Ansi13, Ansi14, Ansi15
}

/// <summary>
/// What else is true of a name besides what kind it is, carried with its span. ReSharper strikes out what's obsolete and
/// underlines a local that's written to again, the rest is for a skin that wants it.
/// </summary>
[Flags]
public enum SyntaxModifiers : byte
{
    None = 0,
    Static = 1,
    Readonly = 2,

    // A local or a parameter that's given a new value somewhere after it's made
    Mutable = 4,

    // Obsolete, deprecated, there's something newer to use
    Deprecated = 8,

    // The name being made here rather than used
    Declaration = 16
}

/// <summary>
/// A stretch of a line of code that is all one kind of thing.
/// </summary>
/// <param name="Start">Where in the line it starts.</param>
/// <param name="Modifiers">What else is true of it, nothing for everything a grammar says.</param>
public readonly record struct SyntaxSpan(int Start, int Length, SyntaxKind Kind, SyntaxModifiers Modifiers = SyntaxModifiers.None);

/// <summary>
/// A name somewhere in a file and what it is, a language server's semantic token or what Hex worked out of HIDL, for
/// the code editor to lay over the grammar's colours (<see cref="Components.CodeEditor.SemanticSpans"/>).
/// </summary>
/// <param name="Line">The line, from 0.</param>
/// <param name="Start">Where on it it starts.</param>
public readonly record struct SemanticSpan(int Line, int Start, int Length, SyntaxKind Kind, SyntaxModifiers Modifiers = SyntaxModifiers.None);

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
