using System.Globalization;
using System.Numerics;

using Horizon.HIDL;
using Horizon.HIDL.Lexing;
using Horizon.HIDL.Parsing;

namespace Horizon.UI.Scripting;

/// <summary>
/// A colour written into the code, <c>color: vec(0.2, 0.4, 0.9, 1)</c>, for an editor to show a swatch of in front of it.
/// </summary>
/// <param name="Line">The line its vec is on, from 0.</param>
/// <param name="Column">Where its vec starts.</param>
/// <param name="Length">How many characters it is, the vec to its closing bracket, when that is on the same line.</param>
public readonly record struct HidlSwatch(int Line, int Column, int Length, Vector4 Color);

/// <summary>What a piece of art named in the code is, which says where an editor finds the picture of it.</summary>
public enum HidlArtKind
{
    // One of the Codicons, $(name) in a text or the text given to an icon or a symbol
    Symbol,

    // An icon of the skin, [icon:name] in a text
    Icon,

    // A piece of the skin's art, the text given to a sprite
    Sprite,

    // A picture on disk, a text that is the path of a .png or an Aseprite file
    Picture
}

/// <summary>
/// Art named in the code, <c>"$(play)"</c>, <c>"[icon:pad_a]"</c>, <c>sprite: "button"</c> or <c>"art/hero.aseprite"</c>,
/// for an editor to draw a small picture of at the end of its line.
/// </summary>
/// <param name="Line">The line it is named on, from 0.</param>
/// <param name="Column">Where its name starts.</param>
/// <param name="Length">How many characters the name is.</param>
/// <param name="Name">The name, of the symbol, the icon or the sprite, or the path as it is written.</param>
/// <param name="Set">The set of icons an icon is written in, [icons:playstation] earlier in the text, null for none.</param>
public readonly record struct HidlArt(int Line, int Column, int Length, HidlArtKind Kind, string Name, string? Set = null);

/// <summary>
/// Colours HIDL. The language's TextMate grammar (<see cref="HIDLGrammar"/>, the same file VS Code colours a .hor
/// with) does what a grammar can, the keywords, texts and what a label reads in them, templates, comments, the
/// preprocessor's lines, numbers, operators and the brackets by their depth, through the same
/// <see cref="TextMateHighlighter"/> as every other language. Over that goes what only the language's own lexer knows,
/// the way VS Code lays semantic tokens over a grammar. What a name is from what is round it (made with let, const or
/// func or taken out of something with let { }, a parameter of a function or an arrow, called, the language's own, the
/// kind after compositor.), the colours written in the code for the editor's swatches, and the art it names for the
/// pictures at the ends of its lines. The lexer is run lenient, so code
/// that is half typed or plain wrong still gets coloured in, and it only ever writes over a span the grammar called a
/// name of some sort, never a text or a comment.
/// </summary>
public sealed class HidlHighlighter : ICodeHighlighter
{
    /// <summary>The one HIDL highlighter, the grammar is loaded the first time it is asked for anything.</summary>
    public static HidlHighlighter Instance { get; } = new();

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

    private TextMateHighlighter? grammar;

    public string Name => "HIDL";

    private HidlHighlighter()
    { }

    /// <summary>The grammar's own reading, with nothing laid over it, for a test that wants to see what the grammar says.</summary>
    public TextMateHighlighter Grammar => grammar ??= new TextMateHighlighter(
        CodeLanguages.LoadGrammar(HIDLGrammar.ScopeName) ?? throw new InvalidOperationException("The HIDL grammar didn't load."), Name);

    /// <summary>Whether a name is one the language comes with (print, include, math), or the compositor.</summary>
    public static bool IsBuiltin(string name) => Builtins.Contains(name);

    public List<SyntaxSpan>[] Read(string code, int lineCount, CodeLineStart[]? starts = null, List<HidlSwatch>? swatches = null, List<HidlArt>? art = null)
    {
        List<SyntaxSpan>[] spans = Grammar.Read(code, lineCount, starts);
        Overlay(spans, code, swatches, art);
        return spans;
    }

    /// <summary>
    /// The grammar's part read again from the first line that changed (see <see cref="TextMateHighlighter"/>), the spans it
    /// keeps copied, since what the lexer knows is laid over them in place, and that laid over all of it again as always.
    /// What was laid over a kept line last time is laid over again or left the colour it is, so it comes out the same.
    /// </summary>
    public List<SyntaxSpan>[] ReadAgain(string code, string[] lines, CodeLineStart[] starts, CodeColouring last, List<HidlSwatch>? swatches = null, List<HidlArt>? art = null)
    {
        List<SyntaxSpan>[] spans = Grammar.ReadAgain(lines, starts, last, copyKept: true);
        Overlay(spans, code, swatches, art);
        return spans;
    }

    public List<SyntaxSpan> ReadLine(string line, in CodeLineStart start)
    {
        List<SyntaxSpan>[] spans = [Grammar.ReadLine(line, start)];
        Overlay(spans, line, null, null);
        return spans[0];
    }

    /// <summary>
    /// Helper method to lay what the lexer knows over what the grammar said. Every name's kind from what is round it,
    /// the swatches and the art.
    /// </summary>
    private static void Overlay(List<SyntaxSpan>[] spans, string code, List<HidlSwatch>? swatches, List<HidlArt>? art)
    {
        swatches?.Clear();
        art?.Clear();

        // Where every line starts, to put any piece of the code on its line
        var starts = new List<int>(spans.Length + 1) { 0 };
        for (int i = 0; i < code.Length; i++)
        {
            if (code[i] == '\n')
                starts.Add(i + 1);
        }

        Token[] tokens = Lexer.Tokenize(code, lenient: true);

        // The names being made that the token before doesn't give away. A function's parameters (and not what their
        // defaults use), the names a let takes out of an object or a list (let { a, b }) and the names of a for
        HashSet<int>? declared = null;

        for (int i = 0; i < tokens.Length; i++)
        {
            Token token = tokens[i];
            switch (token.Type)
            {
                case TokenType.OpenParenthesis when HIDLDeclarations.OpensParameters(tokens, i):
                    foreach (Token name in HIDLDeclarations.Parameters(tokens, i))
                        (declared ??= []).Add(name.Index);
                    break;

                case TokenType.Let or TokenType.Const when i + 1 < tokens.Length && tokens[i + 1].Type is TokenType.OpenBracket or TokenType.OpenBrace:
                    foreach (Token name in HIDLDeclarations.Declared(tokens, i))
                        (declared ??= []).Add(name.Index);
                    break;

                case TokenType.For:
                    foreach (Token name in HIDLDeclarations.LoopNames(tokens, i))
                        (declared ??= []).Add(name.Index);
                    break;

                case TokenType.OpenParenthesis when swatches is not null && i > 0 && tokens[i - 1].Type == TokenType.Vector:
                    ReadSwatch(tokens, i, swatches);
                    break;

                case TokenType.TextLiteral when art is not null:
                    ReadArt(tokens, i, code, starts, art);
                    break;

                case TokenType.Identifier:
                    bool made = HIDLDeclarations.IsArrowParameter(tokens, i) || declared?.Contains(token.Index) == true;
                    Place(spans, starts, token.Index, token.Length, KindOf(tokens, i, made));
                    break;
            }
        }
    }

    private static TokenType Before(Token[] tokens, int i, int back) => i - back >= 0 ? tokens[i - back].Type : TokenType.EndOfFile;

    /// <summary>
    /// Helper method to say what a name is to the colours, which depends on what is next to it.
    /// </summary>
    private static SyntaxKind KindOf(Token[] tokens, int i, bool parameters)
    {
        Token token = tokens[i];
        TokenType next = i + 1 < tokens.Length ? tokens[i + 1].Type : TokenType.EndOfFile;
        TokenType before = Before(tokens, i, 1);

        // Being made, by let, const or func, or a parameter of the function being made
        if (before is TokenType.Let or TokenType.Const or TokenType.Function || parameters)
            return SyntaxKind.Declaration;

        // The kind of a component, compositor.button
        if (before == TokenType.Dot && i >= 2 && tokens[i - 2].Type == TokenType.Identifier && tokens[i - 2].Value == "compositor")
            return SyntaxKind.Type;

        if (next == TokenType.Colon || before is TokenType.Dot or TokenType.QuestionDot)
            return next == TokenType.OpenParenthesis ? SyntaxKind.Call : SyntaxKind.Property;

        if (Builtins.Contains(token.Value))
            return SyntaxKind.Builtin;

        return next == TokenType.OpenParenthesis ? SyntaxKind.Call : SyntaxKind.Name;
    }

    /// <summary>
    /// Helper method to write a name's kind over the span the grammar gave it, when that is a name of some sort (a
    /// name inside of a text or a comment is left to the grammar). The span is cut round the name where it is wider.
    /// </summary>
    private static void Place(List<SyntaxSpan>[] spans, List<int> starts, int at, int length, SyntaxKind kind)
    {
        int line = starts.BinarySearch(at);
        if (line < 0) line = ~line - 1;
        if (line < 0 || line >= spans.Length)
            return;

        int column = at - starts[line], end = column + length;
        List<SyntaxSpan> row = spans[line];

        for (int i = 0; i < row.Count; i++)
        {
            SyntaxSpan span = row[i];
            int spanEnd = span.Start + span.Length;
            if (spanEnd <= column)
                continue;
            if (span.Start >= end)
                return;

            if (!SyntaxScopes.IsName(span.Kind))
                return;

            if (span.Kind == kind && span.Start <= column && spanEnd >= end)
                return;

            // The piece of the span before the name, the name, and the piece after it
            row.RemoveAt(i);
            int put = i;
            if (span.Start < column)
                row.Insert(put++, new SyntaxSpan(span.Start, column - span.Start, span.Kind));
            row.Insert(put++, new SyntaxSpan(column, Math.Min(end, spanEnd) - column, kind));
            if (spanEnd > end)
                row.Insert(put, new SyntaxSpan(end, spanEnd - end, span.Kind));
            return;
        }
    }

    /// <summary>
    /// Helper method to find the art a text names. The icons written into it, the whole of it when it is the path of a
    /// picture, and the whole of it when it is given to something that takes the name of a symbol or a sprite.
    /// </summary>
    private static void ReadArt(Token[] tokens, int index, string code, List<int> starts, List<HidlArt> art)
    {
        Token token = tokens[index];
        int from = token.Index + 1, end = token.Index + token.Length - 1;
        if (end - from < 1 || code[token.Index] is not ('"' or '\''))
            return;

        ReadOnlySpan<char> text = code.AsSpan(from, end - from);
        if (text.ContainsAny('\n', '{'))
            return;

        // A picture is the whole of the text
        if (text.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || text.EndsWith(".aseprite", StringComparison.OrdinalIgnoreCase)
            || text.EndsWith(".ase", StringComparison.OrdinalIgnoreCase))
        {
            AddArt(starts, art, from, end - from, HidlArtKind.Picture, text.ToString());
            return;
        }

        // and so is the name of something given to an icon (a Codicon to a button) or a sprite (a piece of the skin)
        if (index >= 2 && tokens[index - 1].Type is TokenType.Colon or TokenType.Equals && !text.ContainsAny(' ', '[', '$'))
        {
            string key = tokens[index - 2].Value;
            if (key is "icon" or "symbol")
            {
                AddArt(starts, art, from, end - from, HidlArtKind.Symbol, text.ToString());
                return;
            }

            if (key is "sprite" or "region")
            {
                AddArt(starts, art, from, end - from, HidlArtKind.Sprite, text.ToString());
                return;
            }
        }

        // Otherwise the icons written into it, $(name) and [icon:name], an icon in whatever set [icons:name] said last
        string? set = null;
        for (int at = 0; at < text.Length - 2; at++)
        {
            if (text[at] == '$' && text[at + 1] == '(')
            {
                int close = text[at..].IndexOf(')');
                if (close > 2 && !text.Slice(at, close).Contains(' '))
                {
                    AddArt(starts, art, from + at, close + 1, HidlArtKind.Symbol, text.Slice(at + 2, close - 2).ToString());
                    at += close;
                }
            }
            else if (text[at..].StartsWith("[icon:", StringComparison.Ordinal))
            {
                int close = text[at..].IndexOf(']');
                if (close > 6 && !text.Slice(at, close).Contains(' '))
                {
                    AddArt(starts, art, from + at, close + 1, HidlArtKind.Icon, text.Slice(at + 6, close - 6).ToString(), set);
                    at += close;
                }
            }
            else if (text[at..].StartsWith("[icons:", StringComparison.Ordinal))
            {
                int close = text[at..].IndexOf(']');
                if (close > 7)
                {
                    set = text.Slice(at + 7, close - 7).ToString();
                    at += close;
                }
            }
        }
    }

    // Helper method to put art down on the line it was found on
    private static void AddArt(List<int> starts, List<HidlArt> art, int at, int length, HidlArtKind kind, string name, string? set = null)
    {
        int line = starts.BinarySearch(at);
        if (line < 0)
            line = ~line - 1;

        art.Add(new HidlArt(line, at - starts[line], length, kind, name, set));
    }

    /// <summary>
    /// Helper method to read the colour of a vec(r, g, b) or vec(r, g, b, a) that is given to something called a colour
    /// (or a tint), for a swatch in front of it.
    /// </summary>
    private static void ReadSwatch(Token[] tokens, int open, List<HidlSwatch> swatches)
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
}
