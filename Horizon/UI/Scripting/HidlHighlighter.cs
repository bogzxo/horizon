using System.Globalization;
using System.Numerics;

using Horizon.HIDL;
using Horizon.HIDL.Lexing;

namespace Horizon.UI.Scripting;

/// <summary>
/// What a piece of HIDL code is, as far as colouring it in goes.
/// </summary>
public enum HidlToken
{
    // A name that is nothing more than a name, a variable being used most of the time
    Name,

    // let, const, func and the rest of the words the language keeps to itself
    Keyword,

    // The key of an object ("padding" in `padding: 8`) or something reached with a dot
    Property,

    // Anything that is called, which is whatever has a bracket after it
    Call,

    Text,
    Number,
    Comment,

    // Commas, colons, semicolons and whatever else is left
    Punctuation,

    // A line that starts with a #, the #define part of #define layout
    Directive,

    // A name being made, the one after let, const or func, and a function's parameters
    Declaration,

    // What the language comes with, print, include, math, vec, and the compositor a layout is made with
    Builtin,

    // The kind of a component, the button of compositor.button
    Type,

    // true, false, null, and the name a directive defines
    Constant,

    // A text that is a key of a game's language, "@main_menu.quit"
    Reference,

    // An escape in a text, \n and \"
    Escape,

    // The markup in a text a label reads, [b], [color:red], $(save), [icon:pad_a]
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
public readonly record struct HidlSpan(int Start, int Length, HidlToken Kind);

/// <summary>
/// A colour written into the code, <c>color: vec(0.2, 0.4, 0.9, 1)</c>, for an editor to show a swatch of in front of it.
/// </summary>
/// <param name="Line">The line its vec is on, from 0.</param>
/// <param name="Column">Where its vec starts.</param>
/// <param name="Length">How many characters it is, the vec to its closing bracket, when that is on the same line.</param>
public readonly record struct HidlSwatch(int Line, int Column, int Length, Vector4 Color);

/// <summary>
/// Helper class that cuts HIDL code up into the bits a code view colours in differently. It is the language's own
/// lexer in its lenient mode, so what the editor colours is what the language sees, keywords and all, and code that
/// is half typed or plain wrong still gets coloured in. On top of what the lexer says it works out what a name is from
/// what is round it (made with let, a parameter, called, the language's own, the kind after compositor.), colours
/// brackets by how deep they are, and picks out what a text has in it (escapes, a label's markup, a key of a
/// language). Every token knows where it was, so the spans come out per line, with a comment that runs over several
/// lines coloured on all of them.
/// </summary>
public static class HidlHighlighter
{
    // What the language has in it before anybody writes a line, worked out once out of a runtime's global scope
    private static HashSet<string>? builtins;

    private static HashSet<string> Builtins
    {
        get
        {
            if (builtins is null)
            {
                var names = new HashSet<string>(new HIDLRuntime().GlobalScope.Variables.Keys, StringComparer.Ordinal) { "compositor" };
                names.Remove("true");
                names.Remove("false");
                names.Remove("null");
                builtins = names;
            }

            return builtins;
        }
    }

    /// <summary>Whether a name is one the language comes with (print, include, math), or the compositor.</summary>
    public static bool IsBuiltin(string name) => Builtins.Contains(name);

    /// <summary>
    /// Cuts code into spans, one list per line. The code has to have its lines ended with '\n' alone.
    /// </summary>
    /// <param name="lineCount">How many lines the code has, which is how many lists come back.</param>
    /// <param name="swatches">Where the colours written in the code go, null to not look for them.</param>
    public static List<HidlSpan>[] Read(string code, int lineCount, List<HidlSwatch>? swatches = null)
    {
        var spans = new List<HidlSpan>[Math.Max(1, lineCount)];
        for (int i = 0; i < spans.Length; i++)
            spans[i] = [];

        swatches?.Clear();

        // Where every line starts, to put any piece of the code on its line
        var starts = new List<int>(lineCount + 1) { 0 };
        for (int i = 0; i < code.Length; i++)
        {
            if (code[i] == '\n')
                starts.Add(i + 1);
        }

        Token[] tokens = Lexer.Tokenize(code, lenient: true);

        int depth = 0;

        // Whether the names being read are a function's parameters, from the bracket after func name to its pair
        bool parameters = false;

        for (int i = 0; i < tokens.Length; i++)
        {
            Token token = tokens[i];
            if (token.Type == TokenType.EndOfFile || token.Length == 0)
                continue;

            int from = token.Index, end = token.Index + token.Length;

            switch (token.Type)
            {
                case TokenType.TextLiteral:
                    ReadText(spans, starts, code, from, end);
                    continue;

                case TokenType.Directive:
                    ReadDirective(spans, starts, code, from, end);
                    continue;

                case TokenType.OpenParenthesis or TokenType.OpenBracket or TokenType.OpenBrace:
                    if (token.Type == TokenType.OpenParenthesis && Before(tokens, i, 1) == TokenType.Identifier && Before(tokens, i, 2) == TokenType.Function)
                        parameters = true;

                    Emit(spans, starts, code, from, end, BracketAt(depth++));

                    if (swatches is not null && token.Type == TokenType.OpenParenthesis && i > 0 && tokens[i - 1].Type == TokenType.Vector)
                        ReadSwatch(tokens, i, starts, swatches);
                    continue;

                case TokenType.CloseParenthesis or TokenType.CloseBracket or TokenType.CloseBrace:
                    depth = Math.Max(0, depth - 1);
                    if (token.Type == TokenType.CloseParenthesis)
                        parameters = false;

                    Emit(spans, starts, code, from, end, BracketAt(depth));
                    continue;
            }

            Emit(spans, starts, code, from, end, KindOf(tokens, i, parameters));
        }

        return spans;
    }

    private static HidlToken BracketAt(int depth) => (depth % 3) switch
    {
        0 => HidlToken.Bracket1,
        1 => HidlToken.Bracket2,
        _ => HidlToken.Bracket3
    };

    private static TokenType Before(Token[] tokens, int i, int back) => i - back >= 0 ? tokens[i - back].Type : TokenType.EndOfFile;

    /// <summary>
    /// Helper method to say what a token is to the colours, which for a name depends on what is next to it.
    /// </summary>
    private static HidlToken KindOf(Token[] tokens, int i, bool parameters)
    {
        Token token = tokens[i];

        switch (token.Type)
        {
            case TokenType.Comment:
                return HidlToken.Comment;

            case TokenType.Number:
                return HidlToken.Number;

            case TokenType.Null:
                return HidlToken.Constant;

            case TokenType.Let or TokenType.Const or TokenType.Function or TokenType.If or TokenType.Else or TokenType.While
                or TokenType.Do or TokenType.For or TokenType.In or TokenType.Return or TokenType.Break or TokenType.Continue
                or TokenType.Delete:
                return HidlToken.Keyword;

            case TokenType.Vector:
                return HidlToken.Builtin;

            case TokenType.BinaryOperation or TokenType.Equals or TokenType.Equality or TokenType.NotEquality or TokenType.Exclamation
                or TokenType.Question or TokenType.CompoundEquals or TokenType.Spread:
                return HidlToken.Operator;

            case TokenType.Identifier:
                if (token.Value is "true" or "false" or "null")
                    return HidlToken.Constant;

                TokenType next = i + 1 < tokens.Length ? tokens[i + 1].Type : TokenType.EndOfFile;
                TokenType before = Before(tokens, i, 1);

                // Being made, by let, const or func, or a parameter of the function being made
                if (before is TokenType.Let or TokenType.Const or TokenType.Function || parameters)
                    return HidlToken.Declaration;

                // The kind of a component, compositor.button
                if (before == TokenType.Dot && i >= 2 && tokens[i - 2].Type == TokenType.Identifier && tokens[i - 2].Value == "compositor")
                    return HidlToken.Type;

                if (next == TokenType.Colon || before == TokenType.Dot)
                    return next == TokenType.OpenParenthesis ? HidlToken.Call : HidlToken.Property;

                if (Builtins.Contains(token.Value))
                    return HidlToken.Builtin;

                return next == TokenType.OpenParenthesis ? HidlToken.Call : HidlToken.Name;

            default:
                return HidlToken.Punctuation;
        }
    }

    /// <summary>
    /// Helper method to colour a text, its quotes and words as a text (or a key of a language, "@main_menu.quit"), its
    /// escapes, and the markup a label reads in it.
    /// </summary>
    private static void ReadText(List<HidlSpan>[] spans, List<int> starts, string code, int from, int end)
    {
        // A key of a language is the whole of the text, the quotes and all
        if (end - from > 2 && code[from + 1] == '@')
        {
            Emit(spans, starts, code, from, end, HidlToken.Reference);
            return;
        }

        int plain = from;
        for (int at = from + 1; at < end - 1; at++)
        {
            int length = 0;
            HidlToken kind = HidlToken.Escape;

            if (code[at] == '\\')
            {
                length = 2;
            }
            else if (code[at] == '$' && at + 1 < end && code[at + 1] == '(')
            {
                int close = code.IndexOf(')', at);
                if (close > 0 && close < end && !code.AsSpan(at, close - at).ContainsAny(" \n"))
                {
                    length = close - at + 1;
                    kind = HidlToken.Markup;
                }
            }
            else if (code[at] == '[')
            {
                int close = code.IndexOf(']', at);
                if (close > at + 1 && close < end && !code.AsSpan(at, close - at).ContainsAny(" \n\""))
                {
                    length = close - at + 1;
                    kind = HidlToken.Markup;
                }
            }

            if (length == 0)
                continue;

            length = Math.Min(length, end - at);
            Emit(spans, starts, code, plain, at, HidlToken.Text);
            Emit(spans, starts, code, at, at + length, kind);
            at += length - 1;
            plain = at + 1;
        }

        Emit(spans, starts, code, plain, end, HidlToken.Text);
    }

    /// <summary>Helper method to colour a directive, its #word, the name it is about, and what is left.</summary>
    private static void ReadDirective(List<HidlSpan>[] spans, List<int> starts, string code, int from, int end)
    {
        int word = from + 1;
        while (word < end && char.IsLetter(code[word])) word++;
        Emit(spans, starts, code, from, word, HidlToken.Directive);

        int name = word;
        while (name < end && code[name] == ' ') name++;
        int nameEnd = name;
        while (nameEnd < end && (char.IsLetterOrDigit(code[nameEnd]) || code[nameEnd] == '_')) nameEnd++;

        Emit(spans, starts, code, name, nameEnd, HidlToken.Constant);
        Emit(spans, starts, code, nameEnd, end, HidlToken.Name);
    }

    /// <summary>
    /// Helper method to read the colour of a vec(r, g, b) or vec(r, g, b, a) that is given to something called a colour
    /// (or a tint), for a swatch in front of it.
    /// </summary>
    private static void ReadSwatch(Token[] tokens, int open, List<int> starts, List<HidlSwatch> swatches)
    {
        // What it is given to, key: vec( or name = vec(
        int vector = open - 1;
        if (vector < 2 || tokens[vector - 2].Type != TokenType.Identifier || tokens[vector - 1].Type is not (TokenType.Colon or TokenType.Equals))
            return;

        string target = tokens[vector - 2].Value;
        if (!target.Contains("color", StringComparison.OrdinalIgnoreCase) && !target.Contains("colour", StringComparison.OrdinalIgnoreCase)
            && !target.Contains("tint", StringComparison.OrdinalIgnoreCase))
            return;

        Span<float> numbers = stackalloc float[4];
        int count = 0;
        bool negative = false;

        for (int i = open + 1; i < tokens.Length && count <= 4; i++)
        {
            Token token = tokens[i];
            switch (token.Type)
            {
                case TokenType.Number when count < 4 && float.TryParse(token.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float value):
                    numbers[count++] = negative ? -value : value;
                    negative = false;
                    break;

                case TokenType.BinaryOperation when token.Value == "-":
                    negative = true;
                    break;

                case TokenType.Comma:
                    break;

                case TokenType.CloseParenthesis when count >= 3:
                    Token vec = tokens[vector];
                    int line = vec.Line - 1;
                    int length = token.Line == vec.Line ? token.Index + token.Length - vec.Index : vec.Length;
                    Vector4 color = new(numbers[0], numbers[1], numbers[2], count == 4 ? numbers[3] : 1.0f);
                    swatches.Add(new HidlSwatch(line, vec.Col - 1, length, Vector4.Clamp(color, Vector4.Zero, Vector4.One)));
                    return;

                default:
                    return;
            }
        }
    }

    /// <summary>
    /// Helper method to put a stretch of the code in as one kind of thing, cut at the ends of lines it runs over.
    /// </summary>
    private static void Emit(List<HidlSpan>[] spans, List<int> starts, string code, int from, int end, HidlToken kind)
    {
        if (end <= from)
            return;

        int line = starts.BinarySearch(from);
        if (line < 0) line = ~line - 1;

        while (from < end && line < spans.Length)
        {
            int lineEnd = line + 1 < starts.Count ? starts[line + 1] - 1 : code.Length;
            int stop = Math.Min(end, lineEnd);

            if (stop > from)
                spans[line].Add(new HidlSpan(from - starts[line], stop - from, kind));

            from = lineEnd + 1;
            line++;
        }
    }
}
