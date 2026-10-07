namespace Horizon.Rendering.UIX.Scripting;

/// <summary>
/// What a piece of HIDL code is, as far as colouring it in goes.
/// </summary>
public enum HidlToken
{
    // A name that is nothing more than a name, a variable most of the time
    Name,

    // let, const, func and the rest of the words the language keeps to itself
    Keyword,

    // The key of an object ("padding" in padding: 8) or something reached with a dot
    Property,

    // Anything that is called, which is whatever has a bracket after it
    Call,

    Text,
    Number,
    Comment,

    // Brackets, commas, operators and whatever else is left
    Punctuation
}

/// <summary>
/// A stretch of a line of code that is all one kind of thing.
/// </summary>
/// <param name="Start">Where in the line it starts.</param>
public readonly record struct HidlSpan(int Start, int Length, HidlToken Kind);

/// <summary>
/// Helper class that cuts HIDL code up into the bits a code view colours in differently.
/// It only looks at one line at a time and never fails. Code that is half typed or plain wrong still gets coloured in, just not all that cleverly.
/// It is not the lexer of the language (see Horizon.HIDL for that one), which throws comments away and doesn't say where anything was.
/// </summary>
public static class HidlHighlighter
{
    private static readonly HashSet<string> Keywords =
    [
        "let", "const", "func", "if", "else", "while", "do", "for", "in", "delete", "break", "continue", "return", "true", "false", "null"
    ];

    /// <summary>
    /// Cuts one line of code into spans. Whitespace isn't part of any of them.
    /// </summary>
    /// <param name="spans">Where the spans go, after whatever is in there already.</param>
    public static void Read(ReadOnlySpan<char> line, List<HidlSpan> spans)
    {
        int at = 0;

        while (at < line.Length)
        {
            char character = line[at];

            if (char.IsWhiteSpace(character))
            {
                at++;
                continue;
            }

            int start = at;

            if (character == '/' && at + 1 < line.Length && line[at + 1] == '/')
            {
                // The rest of the line is the comment
                spans.Add(new HidlSpan(start, line.Length - start, HidlToken.Comment));
                return;
            }

            if (character == '"')
            {
                // Up to the quote that closes it, or the end of the line for one that was never closed
                at++;
                while (at < line.Length && line[at] != '"')
                    at++;
                at = Math.Min(at + 1, line.Length);

                spans.Add(new HidlSpan(start, at - start, HidlToken.Text));
                continue;
            }

            if (char.IsAsciiDigit(character) || (character == '-' && at + 1 < line.Length && char.IsAsciiDigit(line[at + 1]) && !EndsAValue(line, at)))
            {
                at++;
                while (at < line.Length && (char.IsAsciiDigit(line[at]) || line[at] == '.'))
                    at++;

                spans.Add(new HidlSpan(start, at - start, HidlToken.Number));
                continue;
            }

            if (char.IsLetter(character) || character == '_')
            {
                while (at < line.Length && (char.IsLetterOrDigit(line[at]) || line[at] == '_'))
                    at++;

                spans.Add(new HidlSpan(start, at - start, KindOfWord(line, start, at)));
                continue;
            }

            spans.Add(new HidlSpan(start, 1, HidlToken.Punctuation));
            at++;
        }
    }

    /// <summary>
    /// Helper method to say what a word is, going by the word itself and by what is either side of it.
    /// </summary>
    private static HidlToken KindOfWord(ReadOnlySpan<char> line, int start, int end)
    {
        if (Keywords.Contains(line[start..end].ToString()))
            return HidlToken.Keyword;

        char next = NextAfter(line, end);

        if (next == '(')
            return HidlToken.Call;

        // The key of an object, or something of an object that is reached into
        if (next == ':' || (start > 0 && line[start - 1] == '.'))
            return HidlToken.Property;

        return HidlToken.Name;
    }

    // The first thing after a place in the line that isn't whitespace, nothing if the line ends first
    private static char NextAfter(ReadOnlySpan<char> line, int at)
    {
        while (at < line.Length && char.IsWhiteSpace(line[at]))
            at++;

        return at < line.Length ? line[at] : '\0';
    }

    // Whether what comes before a minus is something a number can be taken away from, in which case the minus is not the sign of a number
    private static bool EndsAValue(ReadOnlySpan<char> line, int at)
    {
        while (at > 0 && char.IsWhiteSpace(line[at - 1]))
            at--;

        return at > 0 && (char.IsLetterOrDigit(line[at - 1]) || line[at - 1] is ')' or ']' or '_');
    }
}
