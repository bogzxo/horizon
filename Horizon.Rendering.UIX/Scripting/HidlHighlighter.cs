using Horizon.HIDL.Lexing;

namespace Horizon.Rendering.UIX.Scripting;

/// <summary>
/// What a piece of HIDL code is, as far as colouring it in goes.
/// </summary>
public enum HidlToken
{
    // A name that is nothing more than a name, a variable most of the time
    Name,

    // let, const, func and the rest of the words the language keeps to itself, and true, false and null
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
/// Helper class that cuts HIDL code up into the bits a code view colours in differently. It is the language's own
/// lexer in its lenient mode, so what the editor colours is what the language sees, keywords and all, and code that
/// is half typed or plain wrong still gets coloured in. Every token knows where it was, so the spans come out per line,
/// with a comment that runs over several lines coloured on all of them.
/// </summary>
public static class HidlHighlighter
{
    // The words that are values rather than keywords to the lexer, but read as keywords
    private static readonly HashSet<string> Literals = ["true", "false", "null"];

    /// <summary>
    /// Cuts code into spans, one list per line. The code has to have its lines ended with '\n' alone.
    /// </summary>
    /// <param name="lineCount">How many lines the code has, which is how many lists come back.</param>
    public static List<HidlSpan>[] Read(string code, int lineCount)
    {
        var spans = new List<HidlSpan>[Math.Max(1, lineCount)];
        for (int i = 0; i < spans.Length; i++)
            spans[i] = [];

        Token[] tokens = Lexer.Tokenize(code, lenient: true);

        for (int i = 0; i < tokens.Length; i++)
        {
            Token token = tokens[i];
            if (token.Type == TokenType.EndOfFile || token.Length == 0)
                continue;

            HidlToken kind = KindOf(tokens, i);

            // Line by line, for the tokens that run over more than one (a block comment)
            int line = token.Line - 1;
            int column = token.Col - 1;
            int from = token.Index;
            int end = token.Index + token.Length;

            for (int at = from; at <= end && line < spans.Length; at++)
            {
                if (at < end && code[at] != '\n')
                    continue;

                if (at > from)
                    spans[line].Add(new HidlSpan(column, at - from, kind));

                line++;
                column = 0;
                from = at + 1;
            }
        }

        return spans;
    }

    /// <summary>
    /// Helper method to say what a token is to the colours, which for a name depends on what is next to it.
    /// </summary>
    private static HidlToken KindOf(Token[] tokens, int i)
    {
        Token token = tokens[i];

        switch (token.Type)
        {
            case TokenType.Comment:
                return HidlToken.Comment;

            case TokenType.TextLiteral:
                return HidlToken.Text;

            case TokenType.Number:
                return HidlToken.Number;

            case TokenType.Let or TokenType.Const or TokenType.Function or TokenType.If or TokenType.Else or TokenType.While
                or TokenType.Do or TokenType.For or TokenType.In or TokenType.Return or TokenType.Break or TokenType.Continue
                or TokenType.Delete or TokenType.Null:
                return HidlToken.Keyword;

            case TokenType.Vector:
                return HidlToken.Call;

            case TokenType.Identifier:
                if (Literals.Contains(token.Value))
                    return HidlToken.Keyword;

                // Called, a key, or reached through a dot
                TokenType next = i + 1 < tokens.Length ? tokens[i + 1].Type : TokenType.EndOfFile;
                TokenType before = i > 0 ? tokens[i - 1].Type : TokenType.EndOfFile;

                if (next == TokenType.OpenParenthesis)
                    return HidlToken.Call;
                if (next == TokenType.Colon || before == TokenType.Dot)
                    return HidlToken.Property;

                return HidlToken.Name;

            default:
                return HidlToken.Punctuation;
        }
    }
}
