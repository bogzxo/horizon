using System.Numerics;

using Horizon.UI.Scripting;
using Horizon.UI.Skinning;

namespace Horizon.UI.Drawing;

/// <summary>
/// The blocks of code of markdown, between two fences. What comes after the first fence says what language the block is
/// in (<c>```cs</c>, <c>```hidl</c>, <c>```json</c>), and the block is coloured the way the code editor colours it, by
/// the same highlighter and in the skin's colours of code. <c>```diff</c> is a patch, its added lines green and the
/// taken out ones red. A block in a language nothing knows is code in one colour, as it always was.
/// </summary>
public sealed partial class UIRichText
{
    /// <summary>
    /// What a block of code is in when its fence says nothing, a language the way a fence would say it ("hidl", "cs"),
    /// null for code in one colour. HIDL's reference has every block in HIDL.
    /// </summary>
    public string? CodeLanguage { get; set; }

    // How a block of code is coloured, not at all, by its language's highlighter or as a patch
    private enum CodeMode : byte
    {
        Plain,
        Syntax,
        Diff
    }

    // A block of code as it was read, the highlighter that coloured it and what that made of every line of it
    private sealed class CodeBlock
    {
        public CodeMode Mode;
        public ICodeHighlighter? Highlighter;
        public List<SyntaxSpan>[]? Lines;
    }

    // The blocks of the layout going on, in order, a piece says which by where in here it is
    private readonly List<CodeBlock> blocks = [];

    // Every block of the text coloured once, by where its fence is, so a new width lays it out again without reading it again
    private readonly Dictionary<int, CodeBlock> codeRead = [];
    private string? builtCode;

    // The block going on (-1 outside of one) and which of its lines is next
    private int block = -1;
    private int blockLine;

    // What a fence's word turned out to be, kept for the run, the list of languages is made fresh every time it is asked
    private static readonly Dictionary<string, ICodeHighlighter?> languageOf = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock languageGate = new();

    /// <summary>How many blocks of code the text came out with.</summary>
    public int CodeBlockCount => blocks.Count;

    /// <summary>
    /// How many colours the letters of a block of code come out in on a palette, for a test to see a block was coloured.
    /// Spaces don't count, nor does a block that isn't there.
    /// </summary>
    /// <param name="seen">Where the colours go, for a test that wants to know which they were, null to only count them.</param>
    public int CodeColours(int index, UIPalette palette, ISet<Vector4>? seen = null)
    {
        seen ??= new HashSet<Vector4>();
        if (index < 0 || index >= blocks.Count)
            return 0;

        int syntaxBlock = -1;
        UISyntaxColors? syntax = null;

        foreach (Piece piece in pieces)
        {
            if (piece.Kind != Kind.Text || piece.Block != index || source.AsSpan(piece.Start, piece.Length).IsWhiteSpace())
                continue;

            if (piece.Shade == Shade.Syntax)
                syntax = SyntaxColours(piece.Block, palette, ref syntaxBlock, syntax);
            seen.Add(ShadeOf(piece, palette, palette.Text, syntax));
        }

        return seen.Count;
    }

    /// <summary>Helper method to open or close a block of code at a fence, the language after an opening one read with the block.</summary>
    private void Fence(int lineEnd, ReadOnlySpan<char> info, ref bool inCode, ref int codeFirstLine, ref float codeIndent, bool first)
    {
        if (inCode)
        {
            CloseCodeBlock(codeFirstLine, codeIndent);
            inCode = false;
            block = -1;
            pendingGap = font.LineHeight * baseScale * 0.3f;
            return;
        }

        inCode = true;
        codeFirstLine = lineCount;
        codeIndent = 0.0f;
        if (!first) pendingGap = MathF.Max(pendingGap, font.LineHeight * baseScale * 0.3f);

        // The block starts on the line after the fence
        int next = source.IndexOf('\n', lineEnd);
        int body = next < 0 ? source.Length : next + 1;

        if (!codeRead.TryGetValue(body, out CodeBlock? read))
            codeRead[body] = read = ReadBlock(info, body);

        blocks.Add(read);
        block = blocks.Count - 1;
        blockLine = 0;
    }

    private void CloseCodeBlock(int firstLine, float indent)
    {
        if (firstLine >= lines.Count)
            return;

        float top = lines[firstLine].Top;
        float bottom = lines[^1].Top + lines[^1].Height;

        fullWidth.Add(pieces.Count);
        pieces.Add(new Piece
        {
            Kind = Kind.Box,
            Line = -1,
            X = indent,
            Y = top,
            Height = bottom - top,
            Shade = Shade.Field,
            Alpha = 1.0f,
            Link = -1,
            Block = -1
        });
    }

    /// <summary>Helper method to lay out a line of a block of code, every character as it is written, in the colours of its language.</summary>
    private void LayOutCodeLine(int start, int length)
    {
        ResetState();
        state.Code = true;
        state.Block = block;

        float pad = font.LineHeight * baseScale * 0.4f;
        lineIndent = wrapIndent = penX = pad;

        CodeBlock? read = block >= 0 ? blocks[block] : null;
        int line = blockLine++;

        if (read is { Mode: CodeMode.Diff })
        {
            state.Shade = DiffShade(source.AsSpan(start, length));
            LayOutLiteral(start, length);
        }
        else if (read is { Mode: CodeMode.Syntax, Lines: { } spans } && line < spans.Length)
        {
            LayOutColoured(start, length, spans[line]);
        }
        else
        {
            LayOutLiteral(start, length);
        }

        FlushWord();
        EndLine();
    }

    /// <summary>
    /// Helper method to lay out a line of code a span at a time, every span a piece in the colour of what it is. The gaps
    /// between them are names, the way the editor has them. A span that runs into a word stays in that word, so a line is
    /// still only broken at a space.
    /// </summary>
    private void LayOutColoured(int start, int length, List<SyntaxSpan> spans)
    {
        int end = start + length;
        int at = start;
        int next = 0;

        while (at < end)
        {
            // Past the spans that end before here, the one after says what this run is and how far it goes
            while (next < spans.Count && start + spans[next].Start + spans[next].Length <= at)
                next++;

            SyntaxKind kind = SyntaxKind.Name;
            int runEnd = end;
            if (next < spans.Count)
            {
                int from = start + spans[next].Start;
                if (from <= at)
                {
                    kind = spans[next].Kind;
                    runEnd = Math.Min(end, from + spans[next].Length);
                }
                else
                {
                    runEnd = Math.Min(end, from);
                }
            }

            state.Shade = Shade.Syntax;
            state.Syntax = kind;
            LayOutLiteral(at, runEnd - at);
            at = runEnd;
        }
    }

    /// <summary>Helper method to lay out some of a line of a block of code as it is written, a word at a time between the spaces.</summary>
    private void LayOutLiteral(int start, int length)
    {
        int segment = start;
        for (int i = start; i < start + length; i++)
        {
            if (source[i] != ' ')
                continue;

            Take(segment, i - segment);
            FlushWord();
            Take(i, 1);
            FlushWord();
            segment = i + 1;
        }

        Take(segment, start + length - segment);
    }

    /// <summary>The colour of a line of a patch, added green, taken out red, where it is (the @@ line) the accent and the file names dim.</summary>
    private static Shade DiffShade(ReadOnlySpan<char> line)
    {
        if (line.StartsWith("+++") || line.StartsWith("---") || line.StartsWith("diff ") || line.StartsWith("index "))
            return Shade.Dim;
        if (line.StartsWith("+"))
            return Shade.Success;
        if (line.StartsWith("-"))
            return Shade.Error;
        if (line.StartsWith("@@"))
            return Shade.Accent;
        return Shade.Inherit;
    }

    /// <summary>
    /// Helper method to read a block of code, the lines from where it starts to its closing fence (or the end of the text)
    /// coloured in one go by the highlighter of the language its fence says.
    /// </summary>
    private CodeBlock ReadBlock(ReadOnlySpan<char> info, int body)
    {
        // The language is the first word after the fence, ```cs title="A.cs" is C#
        info = info.Trim();
        int space = info.IndexOfAny(' ', '\t');
        if (space >= 0) info = info[..space];
        string? tag = info.IsEmpty ? CodeLanguage : info.ToString();

        if (string.IsNullOrWhiteSpace(tag) || tag is "text" or "txt" or "plain" or "plaintext" or "console" or "output")
            return new CodeBlock();
        if (tag.Equals("diff", StringComparison.OrdinalIgnoreCase) || tag.Equals("patch", StringComparison.OrdinalIgnoreCase))
            return new CodeBlock { Mode = CodeMode.Diff };
        if (HighlighterFor(tag) is not { } highlighter)
            return new CodeBlock();

        // The lines of the block with \n alone at their ends, which is what a highlighter wants
        var code = new System.Text.StringBuilder();
        int count = 0;
        for (int at = body; at < source.Length;)
        {
            int end = source.IndexOf('\n', at);
            if (end < 0) end = source.Length;

            ReadOnlySpan<char> line = source.AsSpan(at, end - at);
            if (line.EndsWith("\r")) line = line[..^1];
            if (line.TrimStart().StartsWith("```"))
                break;

            if (count > 0) code.Append('\n');
            code.Append(line);
            count++;
            at = end + 1;
        }

        if (count == 0)
            return new CodeBlock();

        try
        {
            return new CodeBlock { Mode = CodeMode.Syntax, Highlighter = highlighter, Lines = highlighter.Read(code.ToString(), count) };
        }
        catch (Exception)
        {
            // A grammar that falls over on something is code in one colour, the text still reads
            return new CodeBlock();
        }
    }

    /// <summary>
    /// The highlighter a fence's word means, null for a word nothing knows. HIDL by its own names, then a language by its
    /// id or its name (csharp, python, C#), then the word as an extension (cs, ts, py, json, sh).
    /// </summary>
    private static ICodeHighlighter? HighlighterFor(string tag)
    {
        lock (languageGate)
        {
            if (languageOf.TryGetValue(tag, out ICodeHighlighter? known))
                return known;
        }

        ICodeHighlighter? found = null;
        if (tag.Equals("hidl", StringComparison.OrdinalIgnoreCase) || tag.Equals("hor", StringComparison.OrdinalIgnoreCase))
        {
            found = CodeLanguages.Hidl;
        }
        else
        {
            try
            {
                foreach (CodeLanguage language in CodeLanguages.Languages())
                {
                    if ((language.Id.Equals(tag, StringComparison.OrdinalIgnoreCase) || language.Name.Equals(tag, StringComparison.OrdinalIgnoreCase)) && language.Extensions.Count > 0)
                    {
                        found = CodeLanguages.ForExtension(language.Extensions[0]);
                        break;
                    }
                }

                found ??= tag.AsSpan().IndexOfAny("/\\") < 0 ? CodeLanguages.ForExtension("." + tag.TrimStart('.')) : null;
            }
            catch (Exception)
            {
                found = null;
            }
        }

        lock (languageGate)
            languageOf[tag] = found;
        return found;
    }

    /// <summary>Helper method to have the colours of code of the block a piece is in, the ones already had when it is the same block.</summary>
    private UISyntaxColors? SyntaxColours(int index, UIPalette palette, ref int known, UISyntaxColors? colours)
    {
        if (index == known && colours is not null)
            return colours;

        known = index;
        return CodeLanguages.ColorsFor(index >= 0 && index < blocks.Count ? blocks[index].Highlighter : null, palette);
    }
}
