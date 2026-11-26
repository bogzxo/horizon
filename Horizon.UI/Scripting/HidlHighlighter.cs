using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;

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

    // What the language has in it before anybody writes a line (mirrors HIDLRuntime.GlobalScope minus true/false/null, plus compositor)
    private static readonly HashSet<string> Builtins = new(StringComparer.Ordinal)
    {
        "compositor",
        "version", "reset", "to_text",
        "keys", "values", "has", "entries", "range", "print", "merge", "error", "include", "defined", "definition",
        "text", "number", "format", "typeof", "len",
        "math"
    };

    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> BuiltinsBySpan =
        Builtins.GetAlternateLookup<ReadOnlySpan<char>>();

    private sealed record CachedExtras(HidlSwatch[]? Swatches, HidlArt[]? Art);

    private readonly ConditionalWeakTable<List<SyntaxSpan>[], CachedExtras> cachedExtras = new();
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
        int count = Math.Max(1, lineCount);
        var spans = new List<SyntaxSpan>[count];
        var lineOffsets = new int[count];
        ScanState? state = null;
        int depth = 0, at = 0;

        for (int line = 0; line < count; line++)
        {
            lineOffsets[line] = at;
            int end = at < code.Length ? code.IndexOf('\n', at) : -1;
            if (end < 0)
                end = Math.Max(at, code.Length);

            if (starts is not null && line < starts.Length)
                starts[line] = new CodeLineStart(depth, state);

            var row = new List<SyntaxSpan>();
            if (end > at)
                ScanLine(code.AsSpan(at, end - at), row, ref state, ref depth);
            spans[line] = row;
            at = end < code.Length ? end + 1 : code.Length;
        }

        Overlay(spans, code, lineOffsets, swatches, art);
        if (swatches is not null || art is not null)
            cachedExtras.AddOrUpdate(spans, new CachedExtras(swatches is not null ? [.. swatches] : null, art is not null ? [.. art] : null));
        return spans;
    }

    /// <summary>
    /// Reads again from the first line that changed, sharing untouched prefix/suffix span lists (with copy-on-write
    /// if the overlay ever patches one) and incrementally lexing only the enclosing top-level window around the
    /// dirty range until lexer state converges.
    /// </summary>
    public List<SyntaxSpan>[] ReadAgain(string code, string[] lines, CodeLineStart[] starts, CodeColouring last, List<HidlSwatch>? swatches = null, List<HidlArt>? art = null)
    {
        int count = Math.Max(1, lines.Length), was = last.Lines.Length;
        var spans = new List<SyntaxSpan>[count];

        if (lines.Length == 0 || was == 0 || last.Spans.Length != was || last.Starts.Length != was)
        {
            ScanLinesFrom(lines, starts, spans, 0, CodeLineStart.First, last, int.MaxValue);
            Overlay(spans, code, BuildLineOffsets(code, count), swatches, art);
            if (swatches is not null || art is not null)
                cachedExtras.AddOrUpdate(spans, new CachedExtras(swatches is not null ? [.. swatches] : null, art is not null ? [.. art] : null));
            return spans;
        }

        int limit = Math.Min(lines.Length, was), first = 0;
        while (first < limit && string.Equals(lines[first], last.Lines[first], StringComparison.Ordinal))
            first++;
        int same = 0;
        while (same < limit - first && string.Equals(lines[lines.Length - 1 - same], last.Lines[was - 1 - same], StringComparison.Ordinal))
            same++;

        CachedExtras? prevExtras = null;
        bool canReuseExtras = (swatches is null && art is null)
            || (cachedExtras.TryGetValue(last.Spans, out prevExtras)
                && (swatches is null || prevExtras.Swatches is not null)
                && (art is null || prevExtras.Art is not null));

        // Pure prefix reuse (no change or tail deletion): share every remaining line directly
        if (first >= count)
        {
            for (int line = 0; line < count; line++)
            {
                spans[line] = last.Spans[line];
                if (line < starts.Length)
                    starts[line] = last.Starts[line];
            }

            if (canReuseExtras)
            {
                if (swatches is not null)
                {
                    swatches.Clear();
                    foreach (HidlSwatch s in prevExtras!.Swatches!)
                    {
                        if (s.Line < count)
                            swatches.Add(s);
                    }
                }
                if (art is not null)
                {
                    art.Clear();
                    foreach (HidlArt a in prevExtras!.Art!)
                    {
                        if (a.Line < count)
                            art.Add(a);
                    }
                }
                if (swatches is not null || art is not null)
                    cachedExtras.AddOrUpdate(spans, new CachedExtras(swatches is not null ? [.. swatches] : null, art is not null ? [.. art] : null));
                return spans;
            }

            Overlay(spans, code, BuildLineOffsets(code, count), swatches, art, sharedBefore: count, sharedFrom: count);
            if (swatches is not null || art is not null)
                cachedExtras.AddOrUpdate(spans, new CachedExtras(swatches is not null ? [.. swatches] : null, art is not null ? [.. art] : null));
            return spans;
        }

        int from = Math.Clamp(first, 0, Math.Min(was - 1, count - 1));
        for (int line = 0; line < from; line++)
        {
            spans[line] = last.Spans[line];
            if (line < starts.Length)
                starts[line] = last.Starts[line];
        }

        int dirtyEnd = ScanLinesFrom(lines, starts, spans, from, last.Starts[from], last, lines.Length - same);
        int windowStart = FindWindowStart(lines, starts, from);
        int windowEnd = FindWindowEnd(lines, starts, dirtyEnd, count);

        if ((windowStart > 0 || windowEnd < count) && canReuseExtras)
        {
            int shift = lines.Length - was;
            int oldWindowEnd = windowEnd - shift;
            List<HidlSwatch>? windowSwatches = swatches is not null ? [] : null;
            List<HidlArt>? windowArt = art is not null ? [] : null;

            OverlayWindow(spans, lines, windowStart, windowEnd, from, dirtyEnd, windowSwatches, windowArt);

            if (swatches is not null)
            {
                swatches.Clear();
                foreach (HidlSwatch s in prevExtras!.Swatches!)
                {
                    if (s.Line < windowStart)
                        swatches.Add(s);
                }
                foreach (HidlSwatch s in windowSwatches!)
                    swatches.Add(s with { Line = s.Line + windowStart });
                foreach (HidlSwatch s in prevExtras.Swatches!)
                {
                    if (s.Line >= oldWindowEnd)
                        swatches.Add(s with { Line = s.Line + shift });
                }
            }

            if (art is not null)
            {
                art.Clear();
                foreach (HidlArt a in prevExtras!.Art!)
                {
                    if (a.Line < windowStart)
                        art.Add(a);
                }
                foreach (HidlArt a in windowArt!)
                    art.Add(a with { Line = a.Line + windowStart });
                foreach (HidlArt a in prevExtras.Art!)
                {
                    if (a.Line >= oldWindowEnd)
                        art.Add(a with { Line = a.Line + shift });
                }
            }

            if (swatches is not null || art is not null)
                cachedExtras.AddOrUpdate(spans, new CachedExtras(swatches is not null ? [.. swatches] : null, art is not null ? [.. art] : null));
            return spans;
        }

        Overlay(spans, code, BuildLineOffsets(code, count), swatches, art, sharedBefore: from, sharedFrom: dirtyEnd);
        if (swatches is not null || art is not null)
            cachedExtras.AddOrUpdate(spans, new CachedExtras(swatches is not null ? [.. swatches] : null, art is not null ? [.. art] : null));
        return spans;
    }

    public bool TryReadLine(string line, in CodeLineStart start, out List<SyntaxSpan>? spans)
    {
        spans = ReadLine(line, start);
        return true;
    }

    public List<SyntaxSpan> ReadLine(string line, in CodeLineStart start)
    {
        ScanState? state = start.State as ScanState;
        int depth = start.Depth;
        var row = new List<SyntaxSpan>();
        if (line.Length > 0)
            ScanLine(line.AsSpan(), row, ref state, ref depth);

        List<SyntaxSpan>[] lines = [row];
        Overlay(lines, line, [0], null, null);
        return lines[0];
    }

    private static int ScanLinesFrom(string[] lines, CodeLineStart[] starts, List<SyntaxSpan>[] spans, int from, CodeLineStart start, CodeColouring last, int sameFrom)
    {
        ScanState? state = start.State as ScanState;
        int depth = start.Depth, shift = lines.Length - last.Lines.Length;

        for (int line = from; line < spans.Length; line++)
        {
            int old = line - shift;
            int remaining = spans.Length - line;
            if (line >= sameFrom && old >= 0 && old < last.Starts.Length
                && old + remaining <= last.Spans.Length && old + remaining <= last.Starts.Length
                && depth == last.Starts[old].Depth && Equals(state, last.Starts[old].State))
            {
                int convergedAt = line;
                for (; line < spans.Length; line++)
                {
                    int oldLine = line - shift;
                    spans[line] = last.Spans[oldLine];
                    if (line < starts.Length)
                        starts[line] = last.Starts[oldLine];
                }

                return convergedAt;
            }

            if (line < starts.Length)
                starts[line] = new CodeLineStart(depth, state);
            var row = new List<SyntaxSpan>();
            string text = line < lines.Length ? lines[line] : string.Empty;
            if (text.Length > 0)
                ScanLine(text.AsSpan(), row, ref state, ref depth);
            spans[line] = row;
        }

        return spans.Length;
    }

    private static bool IsSafeBoundary(string[] lines, CodeLineStart[] starts, int line) =>
        line <= 0 || (line < starts.Length && line - 1 < lines.Length
            && starts[line].Depth == 0 && starts[line].State is null && EndsStatement(lines[line - 1]));

    private static bool EndsStatement(string line)
    {
        ReadOnlySpan<char> trimmed = line.AsSpan().Trim();
        return trimmed.Length > 0 && (trimmed[^1] is ';' or '}' || trimmed[0] == '#');
    }

    private static int FindWindowStart(string[] lines, CodeLineStart[] starts, int from)
    {
        int line = Math.Clamp(from, 0, lines.Length);
        while (line > 0 && !IsSafeBoundary(lines, starts, line))
            line--;
        return line;
    }

    private static int FindWindowEnd(string[] lines, CodeLineStart[] starts, int dirtyEnd, int count)
    {
        int line = Math.Clamp(dirtyEnd, 0, count);
        while (line < count && !IsSafeBoundary(lines, starts, line))
            line++;
        return line;
    }

    private static void OverlayWindow(List<SyntaxSpan>[] spans, string[] lines, int windowStart, int windowEnd,
        int sharedBefore, int sharedFrom, List<HidlSwatch>? swatches, List<HidlArt>? art)
    {
        int windowLines = windowEnd - windowStart;
        if (windowLines <= 0)
            return;

        int totalChars = windowLines - 1;
        var offsets = new int[windowLines];
        for (int i = 0; i < windowLines; i++)
        {
            offsets[i] = i == 0 ? 0 : offsets[i - 1] + lines[windowStart + i - 1].Length + 1;
            totalChars += lines[windowStart + i].Length;
        }

        string windowCode = string.Create(totalChars, (lines, windowStart, windowLines), static (dest, state) =>
        {
            int pos = 0;
            for (int i = 0; i < state.windowLines; i++)
            {
                if (i > 0)
                    dest[pos++] = '\n';
                string l = state.lines[state.windowStart + i];
                l.AsSpan().CopyTo(dest[pos..]);
                pos += l.Length;
            }
        });

        Overlay(spans, windowCode, offsets, swatches, art, lineOffset: windowStart, sharedBefore: sharedBefore, sharedFrom: sharedFrom);
    }

    private static int[] BuildLineOffsets(string code, int lineCount)
    {
        var offsets = new int[lineCount];
        int line = 1;
        for (int i = 0; i < code.Length && line < lineCount; i++)
        {
            if (code[i] == '\n')
                offsets[line++] = i + 1;
        }

        while (line < lineCount)
            offsets[line++] = code.Length;

        return offsets;
    }

    // Immutable line-spanning state for block comments and template literals (null = top-level code).
    private sealed record ScanState(bool InBlockComment, bool InTemplate, int ExprBraceDepth, ScanState? Parent)
    {
        public static readonly ScanState BlockComment = new(InBlockComment: true, InTemplate: false, ExprBraceDepth: 0, Parent: null);
        public static readonly ScanState Template = new(InBlockComment: false, InTemplate: true, ExprBraceDepth: 0, Parent: null);
    }

    private static ScanState PushBlockComment(ScanState? current) =>
        current is null ? ScanState.BlockComment : new ScanState(InBlockComment: true, InTemplate: false, ExprBraceDepth: 0, Parent: current);

    private static ScanState PushTemplate(ScanState? current) =>
        current is null ? ScanState.Template : new ScanState(InBlockComment: false, InTemplate: true, ExprBraceDepth: 0, Parent: current);

    private enum PrevToken : byte
    {
        None,
        Dot,
        CompositorDot,
        DeclarationKeyword
    }

    private static void ScanLine(ReadOnlySpan<char> line, List<SyntaxSpan> spans, ref ScanState? state, ref int depth)
    {
        int i = 0;
        int len = line.Length;
        PrevToken prev = PrevToken.None;

        // Preprocessor directive: a '#' first on its line at top-level code
        if (state is null)
        {
            int ws = 0;
            while (ws < len && char.IsWhiteSpace(line[ws]))
                ws++;
            if (ws < len && line[ws] == '#')
            {
                ScanDirective(line, ws, spans, ref state, ref depth);
                return;
            }
            i = ws;
        }

        while (i < len)
        {
            if (state is { InBlockComment: true })
            {
                int close = line[i..].IndexOf("*/", StringComparison.Ordinal);
                if (close >= 0)
                {
                    int end = i + close + 2;
                    AddTrimmed(spans, line, i, end, SyntaxKind.Comment);
                    i = end;
                    state = state.Parent;
                }
                else
                {
                    AddTrimmed(spans, line, i, len, SyntaxKind.Comment);
                    return;
                }

                continue;
            }

            if (state is { InTemplate: true, ExprBraceDepth: 0 })
            {
                int runStart = i;
                while (i < len)
                {
                    char tc = line[i];
                    if (tc == '\\' && i + 1 < len)
                    {
                        AddTrimmed(spans, line, runStart, i, SyntaxKind.Text);
                        AddSpan(spans, i, i + 2, SyntaxKind.Escape);
                        i += 2;
                        runStart = i;
                    }
                    else if (tc == '{')
                    {
                        AddTrimmed(spans, line, runStart, i, SyntaxKind.Text);
                        AddSpan(spans, i, i + 1, SyntaxKind.Punctuation);
                        state = state with { ExprBraceDepth = 1 };
                        prev = PrevToken.None;
                        i++;
                        runStart = i;
                        break;
                    }
                    else if (tc == '`')
                    {
                        i++;
                        AddTrimmed(spans, line, runStart, i, SyntaxKind.Text);
                        state = state.Parent;
                        prev = PrevToken.None;
                        runStart = i;
                        break;
                    }
                    else
                    {
                        i++;
                    }
                }

                if (i == len && runStart < len)
                    AddTrimmed(spans, line, runStart, len, SyntaxKind.Text);

                continue;
            }

            if (char.IsWhiteSpace(line[i]))
            {
                i++;
                continue;
            }

            char c = line[i];
            char next = i + 1 < len ? line[i + 1] : '\0';

            // Comments
            if (c == '/' && next == '/')
            {
                AddTrimmed(spans, line, i, len, SyntaxKind.Comment);
                return;
            }

            if (c == '/' && next == '*')
            {
                int close = line[(i + 2)..].IndexOf("*/", StringComparison.Ordinal);
                if (close >= 0)
                {
                    int end = i + 2 + close + 2;
                    AddTrimmed(spans, line, i, end, SyntaxKind.Comment);
                    i = end;
                }
                else
                {
                    AddTrimmed(spans, line, i, len, SyntaxKind.Comment);
                    state = PushBlockComment(state);
                    return;
                }

                continue;
            }

            // Double-quoted strings
            if (c == '"')
            {
                i = ScanString(line, i, spans);
                prev = PrevToken.None;
                continue;
            }

            // Template literals
            if (c == '`')
            {
                AddSpan(spans, i, i + 1, SyntaxKind.Text);
                state = PushTemplate(state);
                prev = PrevToken.None;
                i++;
                continue;
            }

            // Closing '}' of a template embedded expression
            if (c == '}' && state is { InTemplate: true, ExprBraceDepth: 1 })
            {
                AddSpan(spans, i, i + 1, SyntaxKind.Punctuation);
                state = state with { ExprBraceDepth = 0 };
                prev = PrevToken.None;
                i++;
                continue;
            }

            // Numbers
            if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(next)))
            {
                int start = i;
                bool dotted = c == '.';
                i++;
                while (i < len && (char.IsAsciiDigit(line[i]) || (line[i] == '.' && !dotted && i + 1 < len && char.IsAsciiDigit(line[i + 1]))))
                {
                    if (line[i] == '.') dotted = true;
                    i++;
                }

                AddSpan(spans, start, i, SyntaxKind.Number);
                prev = PrevToken.None;
                continue;
            }

            // Identifiers, keywords, constants, builtins
            if (IsIdentStart(c))
            {
                int start = i;
                i++;
                while (i < len && IsIdentPart(line[i]))
                    i++;

                ReadOnlySpan<char> word = line.Slice(start, i - start);
                SyntaxKind kind = ClassifyWord(word, line, i, prev, out PrevToken nextPrev);
                AddSpan(spans, start, i, kind);
                prev = nextPrev;
                continue;
            }

            // Brackets (brackets inside template strings are Punctuation and do not affect bracket depth)
            if (c is '(' or '[' or '{' or ')' or ']' or '}')
            {
                bool inTemplateExpr = state is { InTemplate: true };
                if (inTemplateExpr)
                {
                    if (c == '{')
                        state = state! with { ExprBraceDepth = state.ExprBraceDepth + 1 };
                    else if (c == '}' && state!.ExprBraceDepth > 1)
                        state = state with { ExprBraceDepth = state.ExprBraceDepth - 1 };

                    AddSpan(spans, i, i + 1, SyntaxKind.Punctuation);
                }
                else if (c is '(' or '[' or '{')
                {
                    AddSpan(spans, i, i + 1, BracketAt(depth++));
                }
                else
                {
                    depth = Math.Max(0, depth - 1);
                    AddSpan(spans, i, i + 1, BracketAt(depth));
                }

                prev = PrevToken.None;
                i++;
                continue;
            }

            // Three-character operator
            if (c == '.' && next == '.' && i + 2 < len && line[i + 2] == '.')
            {
                AddSpan(spans, i, i + 3, SyntaxKind.Operator);
                prev = PrevToken.None;
                i += 3;
                continue;
            }

            // Two-character operators & arrow
            if (c == '=' && next == '>')
            {
                AddSpan(spans, i, i + 2, SyntaxKind.Keyword);
                prev = PrevToken.None;
                i += 2;
                continue;
            }

            if ((c == '?' && next == '.') && !(i + 2 < len && char.IsAsciiDigit(line[i + 2])))
            {
                AddSpan(spans, i, i + 2, SyntaxKind.Operator);
                prev = PrevToken.Dot;
                i += 2;
                continue;
            }

            if ((c == '=' && next == '=') || (c == '!' && next == '=') || (c == '<' && next == '=')
                || (c == '>' && next == '=') || (c == '&' && next == '&') || (c == '|' && next == '|')
                || (c == '?' && next == '?') || (next == '=' && c is '+' or '-' or '*' or '/' or '%'))
            {
                AddSpan(spans, i, i + 2, SyntaxKind.Operator);
                prev = PrevToken.None;
                i += 2;
                continue;
            }

            // Single-character operators
            if (c is '=' or '+' or '-' or '*' or '/' or '%' or '<' or '>' or '!' or '?' or '|' or '&')
            {
                AddSpan(spans, i, i + 1, SyntaxKind.Operator);
                prev = PrevToken.None;
                i++;
                continue;
            }

            // Punctuation
            if (c is ',' or ':' or ';' or '.')
            {
                AddSpan(spans, i, i + 1, SyntaxKind.Punctuation);
                if (c == '.')
                {
                    // Check if the token right before this dot was "compositor"
                    prev = spans.Count >= 2 && spans[^2].Kind == SyntaxKind.Builtin
                        && line.Slice(spans[^2].Start, spans[^2].Length).SequenceEqual("compositor")
                        ? PrevToken.CompositorDot
                        : PrevToken.Dot;
                }
                else
                {
                    prev = PrevToken.None;
                }

                i++;
                continue;
            }

            // Unknown character
            AddSpan(spans, i, i + 1, SyntaxKind.Name);
            prev = PrevToken.None;
            i++;
        }
    }

    private static void ScanDirective(ReadOnlySpan<char> line, int start, List<SyntaxSpan> spans, ref ScanState? state, ref int depth)
    {
        int len = line.Length;
        int i = start + 1;
        while (i < len && char.IsWhiteSpace(line[i]))
            i++;
        while (i < len && (char.IsAsciiLetter(line[i]) || line[i] == '_'))
            i++;

        AddTrimmed(spans, line, start, i, SyntaxKind.Directive);

        while (i < len)
        {
            if (char.IsWhiteSpace(line[i]))
            {
                i++;
                continue;
            }

            char c = line[i];
            char next = i + 1 < len ? line[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                AddTrimmed(spans, line, i, len, SyntaxKind.Comment);
                return;
            }

            if (c == '/' && next == '*')
            {
                int close = line[(i + 2)..].IndexOf("*/", StringComparison.Ordinal);
                if (close >= 0)
                {
                    int end = i + 2 + close + 2;
                    AddTrimmed(spans, line, i, end, SyntaxKind.Comment);
                    i = end;
                }
                else
                {
                    AddTrimmed(spans, line, i, len, SyntaxKind.Comment);
                    state = PushBlockComment(state);
                    return;
                }

                continue;
            }

            if (c == '"')
            {
                i = ScanString(line, i, spans);
                continue;
            }

            if ((c == '&' && next == '&') || (c == '|' && next == '|')
                || (c == '=' && next == '=') || (c == '!' && next == '=')
                || (c == '<' && next == '=') || (c == '>' && next == '='))
            {
                AddSpan(spans, i, i + 2, SyntaxKind.Operator);
                i += 2;
                continue;
            }

            if (c is '!' or '<' or '>')
            {
                AddSpan(spans, i, i + 1, SyntaxKind.Operator);
                i++;
                continue;
            }

            if (c is '(' or '[' or '{')
            {
                AddSpan(spans, i, i + 1, BracketAt(depth++));
                i++;
                continue;
            }

            if (c is ')' or ']' or '}')
            {
                depth = Math.Max(0, depth - 1);
                AddSpan(spans, i, i + 1, BracketAt(depth));
                i++;
                continue;
            }

            if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(next)))
            {
                int numStart = i;
                bool dotted = c == '.';
                i++;
                while (i < len && (char.IsAsciiDigit(line[i]) || (line[i] == '.' && !dotted && i + 1 < len && char.IsAsciiDigit(line[i + 1]))))
                {
                    if (line[i] == '.') dotted = true;
                    i++;
                }

                AddSpan(spans, numStart, i, SyntaxKind.Number);
                continue;
            }

            if (IsIdentStart(c))
            {
                int wordStart = i;
                i++;
                while (i < len && IsIdentPart(line[i]))
                    i++;

                ReadOnlySpan<char> word = line.Slice(wordStart, i - wordStart);
                SyntaxKind kind = word.SequenceEqual("defined") && NextNonSpace(line, i) == '('
                    ? SyntaxKind.Builtin
                    : SyntaxKind.Constant;
                AddSpan(spans, wordStart, i, kind);
                continue;
            }

            AddSpan(spans, i, i + 1, SyntaxKind.Directive);
            i++;
        }
    }

    private static int ScanString(ReadOnlySpan<char> line, int start, List<SyntaxSpan> spans)
    {
        int len = line.Length;
        bool isReference = start + 1 < len && line[start + 1] == '@';
        SyntaxKind baseKind = isReference ? SyntaxKind.Reference : SyntaxKind.Text;

        int i = start + 1;
        int runStart = start;

        while (i < len)
        {
            char c = line[i];
            if (c == '\\' && i + 1 < len)
            {
                AddTrimmed(spans, line, runStart, i, baseKind);
                AddSpan(spans, i, i + 2, SyntaxKind.Escape);
                i += 2;
                runStart = i;
                continue;
            }

            if (c == '"')
            {
                i++;
                AddTrimmed(spans, line, runStart, i, baseKind);
                return i;
            }

            if (!isReference)
            {
                if (c == '$' && i + 1 < len && line[i + 1] == '(')
                {
                    int j = i + 2;
                    while (j < len && !char.IsWhiteSpace(line[j]) && line[j] != ')' && line[j] != '"')
                        j++;
                    if (j > i + 2 && j < len && line[j] == ')')
                    {
                        AddTrimmed(spans, line, runStart, i, baseKind);
                        AddSpan(spans, i, j + 1, SyntaxKind.Markup);
                        i = j + 1;
                        runStart = i;
                        continue;
                    }
                }
                else if (c == '[')
                {
                    int j = i + 1;
                    while (j < len && !char.IsWhiteSpace(line[j]) && line[j] != ']' && line[j] != '"')
                        j++;
                    if (j > i + 1 && j < len && line[j] == ']')
                    {
                        AddTrimmed(spans, line, runStart, i, baseKind);
                        AddSpan(spans, i, j + 1, SyntaxKind.Markup);
                        i = j + 1;
                        runStart = i;
                        continue;
                    }
                }
            }

            i++;
        }

        AddTrimmed(spans, line, runStart, len, baseKind);
        return len;
    }

    private static SyntaxKind ClassifyWord(ReadOnlySpan<char> word, ReadOnlySpan<char> line, int afterIndex, PrevToken prev, out PrevToken nextPrev)
    {
        nextPrev = PrevToken.None;

        if (word is "true" or "false" or "null")
            return SyntaxKind.Constant;

        if (word is "let" or "const" or "func")
        {
            nextPrev = PrevToken.DeclarationKeyword;
            return SyntaxKind.Keyword;
        }

        if (word is "if" or "else" or "while" or "do" or "for" or "in" or "return" or "break" or "continue" or "delete")
            return SyntaxKind.Control;

        if (prev == PrevToken.DeclarationKeyword)
            return SyntaxKind.Declaration;

        if (prev == PrevToken.CompositorDot)
            return SyntaxKind.Type;

        int nextAt = SkipSpaces(line, afterIndex);
        char nextChar = nextAt < line.Length ? line[nextAt] : '\0';

        if (nextChar == '=' && nextAt + 1 < line.Length && line[nextAt + 1] == '>')
            return SyntaxKind.Declaration;

        if (nextChar == ':' || prev == PrevToken.Dot)
            return nextChar == '(' ? SyntaxKind.Call : SyntaxKind.Property;

        if (word.SequenceEqual("vec") || BuiltinsBySpan.Contains(word))
            return SyntaxKind.Builtin;

        return nextChar == '(' ? SyntaxKind.Call : SyntaxKind.Name;
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static int SkipSpaces(ReadOnlySpan<char> line, int index)
    {
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;
        return index;
    }

    private static char NextNonSpace(ReadOnlySpan<char> line, int index)
    {
        int at = SkipSpaces(line, index);
        return at < line.Length ? line[at] : '\0';
    }

    private static SyntaxKind BracketAt(int depth) => (depth % 3) switch
    {
        0 => SyntaxKind.Bracket1,
        1 => SyntaxKind.Bracket2,
        _ => SyntaxKind.Bracket3
    };

    private static void AddTrimmed(List<SyntaxSpan> spans, ReadOnlySpan<char> line, int from, int to, SyntaxKind kind)
    {
        while (from < to && char.IsWhiteSpace(line[from])) from++;
        while (to > from && char.IsWhiteSpace(line[to - 1])) to--;
        if (to <= from)
            return;

        AddSpan(spans, from, to, kind);
    }

    private static void AddSpan(List<SyntaxSpan> spans, int from, int to, SyntaxKind kind)
    {
        if (to <= from)
            return;

        if (spans.Count > 0 && spans[^1] is { } last && last.Kind == kind
            && kind is not (SyntaxKind.Bracket1 or SyntaxKind.Bracket2 or SyntaxKind.Bracket3))
        {
            spans[^1] = last with { Length = to - last.Start };
            return;
        }

        spans.Add(new SyntaxSpan(from, to - from, kind));
    }

    /// <summary>
    /// Helper method to lay what the lexer knows over what the line scanner said. Multi-line declarations
    /// (function/arrow parameters, destructuring let/const, for-loop names), swatches, and art.
    /// </summary>
    private static void Overlay(List<SyntaxSpan>[] spans, string code, int[] starts, List<HidlSwatch>? swatches, List<HidlArt>? art,
        int lineOffset = 0, int sharedBefore = 0, int sharedFrom = int.MaxValue)
    {
        swatches?.Clear();
        art?.Clear();

        Token[] tokens = Lexer.Tokenize(code, lenient: true);

        // The names being made that the token before doesn't give away. A function's parameters (and not what their
        // defaults use), the names a let takes out of an object or a list (let { a, b }) and the names of a for
        HashSet<int>? declared = null;
        int lineCursor = 0;

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
                    Place(spans, starts, ref lineCursor, token.Index, token.Length, KindOf(tokens, i, made), lineOffset, sharedBefore, sharedFrom);
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
    /// Helper method to write a name's kind over the span the scanner gave it, when that is a name of some sort (a
    /// name inside of a text or a comment is left alone). The span is cut round the name where it is wider.
    /// </summary>
    private static void Place(List<SyntaxSpan>[] spans, int[] starts, ref int lineCursor, int at, int length, SyntaxKind kind,
        int lineOffset = 0, int sharedBefore = 0, int sharedFrom = int.MaxValue)
    {
        while (lineCursor + 1 < starts.Length && starts[lineCursor + 1] <= at)
            lineCursor++;
        while (lineCursor > 0 && starts[lineCursor] > at)
            lineCursor--;

        int line = lineCursor;
        int targetLine = line + lineOffset;
        if (line < 0 || line >= starts.Length || targetLine < 0 || targetLine >= spans.Length)
            return;

        int column = at - starts[line], end = column + length;
        List<SyntaxSpan> row = spans[targetLine];

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

            // Copy-on-write when patching a span list shared with the previous colouring
            if (targetLine < sharedBefore || targetLine >= sharedFrom)
                row = spans[targetLine] = [.. row];

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
    private static void ReadArt(Token[] tokens, int index, string code, int[] starts, List<HidlArt> art)
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
    private static void AddArt(int[] starts, List<HidlArt> art, int at, int length, HidlArtKind kind, string name, string? set = null)
    {
        int line = Array.BinarySearch(starts, at);
        if (line < 0)
            line = ~line - 1;

        art.Add(new HidlArt(line, at - starts[line], length, kind, name, set));
    }

    /// <summary>
    /// Helper method to read the colour of a vec(r, g, b) or vec(r, g, b, a), for a swatch in front of it. Any vec of three
    /// or four parts that are all between 0 and 1 is taken for a colour, wherever it is (a theme's syntax: { keyword: vec(...) },
    /// an argument), and one given to something called a colour or a tint is one whatever its parts, clamped.
    /// </summary>
    private static void ReadSwatch(Token[] tokens, int open, List<HidlSwatch> swatches)
    {
        // What it is given to, key: vec( or name = vec(, if anything
        int vector = open - 1;
        if (vector < 0)
            return;
        bool named = vector >= 2 && tokens[vector - 2].Type == TokenType.Identifier && tokens[vector - 1].Type is TokenType.Colon or TokenType.Equals
            && IsColourName(tokens[vector - 2].Value);

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
                    if (!named)
                    {
                        for (int part = 0; part < count; part++)
                        {
                            if (numbers[part] is < 0.0f or > 1.0f) return;
                        }
                    }

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

    // Whether a name says it's a colour, or a set of them
    private static bool IsColourName(string name) =>
        name.Contains("color", StringComparison.OrdinalIgnoreCase) || name.Contains("colour", StringComparison.OrdinalIgnoreCase)
        || name.Contains("tint", StringComparison.OrdinalIgnoreCase);
}
