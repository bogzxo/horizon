using System.Numerics;

namespace Horizon.UI.Components;

/// <summary>
/// What a piece of a <see cref="UIChoice"/>'s row is, and so the colour it's written in. The colours come out of the skin's
/// colours for code, a theme colours a commit's hash the way it colours a number.
/// </summary>
public enum UIChoiceTone
{
    /// <summary>The text's own colour, what the row is about (a commit's subject).</summary>
    Primary,

    /// <summary>The text's colour a good deal quieter, what's only there to say where.</summary>
    Dim,

    /// <summary>The skin's accent, the colour the letters typed light up in.</summary>
    Accent,

    /// <summary>A commit's or a stash's name, in the colour of a number.</summary>
    Hash,

    /// <summary>Who did it, in the colour of a type.</summary>
    Author,

    /// <summary>When, in the colour of a comment.</summary>
    Date
}

/// <summary>
/// One row of a <see cref="UIChooser"/>, what it says and what picking it does. The menu a command is in goes quiet in
/// front (<see cref="Quiet"/>), a file's folder or who wrote a commit dim after it (<see cref="Detail"/>), its keys as
/// keycaps along the right, and a symbol in front of it all. A row of a few parts each its own colour (a commit's hash,
/// subject, author and date) says them in <see cref="Segments"/> instead.
/// <code>
/// new UIChoice("Save", save) { Quiet = "File", Keys = "Ctrl+S", Icon = "save" }
/// </code>
/// </summary>
public sealed record UIChoice(string Text, Action Picked)
{
    /// <summary>A quiet word in front, the menu a command is in. Empty for none.</summary>
    public string Quiet { get; init; } = string.Empty;

    /// <summary>Dim text after the row's own, a file's folder, a branch's remote. Empty for none.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>The keys it's on, written as keycaps along the right ("Ctrl+Shift+P", a chord split by a space). Empty for none.</summary>
    public string Keys { get; init; } = string.Empty;

    /// <summary>
    /// How many digits wide its text takes at least, so the detail after it starts in the one column down the list (Go to
    /// Line's numbers, whose lines drifted right as the numbers got longer). 0 for as wide as the text is.
    /// </summary>
    public int TextDigits { get; init; }

    /// <summary>One of <see cref="UISymbols"/>' names drawn in front of it, empty for none.</summary>
    public string Icon { get; init; } = string.Empty;

    /// <summary>The colour of the symbol, null for the text's own a little quieter.</summary>
    public Vector4? IconColor { get; init; }

    /// <summary>
    /// The row as a few pieces each in a tone of its own, written one after another in place of <see cref="Text"/>, null
    /// for the usual quiet, text and detail.
    /// </summary>
    public IReadOnlyList<(string Text, UIChoiceTone Tone)>? Segments { get; init; }

    private readonly string? id;

    /// <summary>
    /// What the row is known by, what's narrowed down by what's typed, ranked, and remembered as picked last. Two rows
    /// with the same one are the one row. Left alone it's the row written the old way, <c>"File: Save   (Ctrl+S)"</c>, so
    /// what was remembered before rows had parts is still found. Quick open's is the file's path from the workspace.
    /// </summary>
    public string Id
    {
        get => id ?? Written();
        init => id = value;
    }

    // The row as one line, the menu and a colon in front, the detail after a tab and the keys in brackets at the end
    private string Written()
    {
        string said = Segments is { Count: > 0 } pieces ? string.Join(" ", pieces.Select(piece => piece.Text)) : Text;
        string quiet = Quiet.Length > 0 ? Quiet + ": " : string.Empty;
        string detail = Detail.Length > 0 ? "\t" + Detail : string.Empty;
        string keys = Keys.Length > 0 ? "   (" + Keys + ")" : string.Empty;
        return quiet + said + detail + keys;
    }

    /// <summary>
    /// A row out of one written the old way, <c>"File: Save   (Ctrl+S)"</c> (see <see cref="UIChooser.Columns"/>), the menu
    /// quiet in front, the keys as keycaps and what's after a tab dim after the rest. It's known by the text it came as.
    /// </summary>
    public static UIChoice Of(string written, Action picked)
    {
        var (quiet, text, keys) = UIChooser.Columns(written);
        int tab = text.IndexOf('\t');
        string detail = tab < 0 ? string.Empty : text[(tab + 1)..];
        return new UIChoice(tab < 0 ? text : text[..tab], picked) { Quiet = quiet, Detail = detail, Keys = keys, Id = written };
    }
}
