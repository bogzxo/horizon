using System.Diagnostics;
using System.Numerics;

using Horizon.UI.Scripting;

namespace Horizon.Tests.Scripting;

/// <summary>
/// Verifies <see cref="HidlHighlighter"/> colours HIDL natively (without paying TextMateSharp/Oniguruma cold-start)
/// with exact span, swatch, and art output, and minimal allocations.
/// </summary>
public class HidlHighlighterPerfTests
{
    private static readonly HidlHighlighter Hidl = HidlHighlighter.Instance;

    private static SyntaxKind KindAt(List<SyntaxSpan>[] spans, int line, int column) =>
        spans[line].First(span => span.Start <= column && column < span.Start + span.Length).Kind;

    [Fact]
    public void Native_Read_produces_exact_spans_swatches_and_art()
    {
        const string code = """
            // Layout header
            #define layout
            #if defined(layout) && !debug
            let panel = compositor.panel({
                color: vec(0.2, 0.4, 0.9, 1),
                text: "[b]bold[/b] $(save) [icon:pad_a]\n",
                title: "@main_menu.quit",
                icon: "gear",
                sprite: "button",
                file: "art/hero.aseprite"
            });
            func greet(who, times = len(who)) { return `hi {who.name ?? "guest"}`; }
            #endif
            """;

        int lineCount = code.Count(c => c == '\n') + 1;
        var starts = new CodeLineStart[lineCount];
        var swatches = new List<HidlSwatch>();
        var art = new List<HidlArt>();

        List<SyntaxSpan>[] spans = Hidl.Read(code, lineCount, starts, swatches, art);

        Assert.Equal(SyntaxKind.Comment, KindAt(spans, 0, 3));
        Assert.Equal(SyntaxKind.Directive, KindAt(spans, 1, 0));
        Assert.Equal(SyntaxKind.Constant, KindAt(spans, 1, 8));
        Assert.Equal(SyntaxKind.Directive, KindAt(spans, 2, 0));
        Assert.Equal(SyntaxKind.Builtin, KindAt(spans, 2, 4));
        Assert.Equal(SyntaxKind.Constant, KindAt(spans, 2, 12));
        Assert.Equal(SyntaxKind.Operator, KindAt(spans, 2, 20));
        Assert.Equal(SyntaxKind.Keyword, KindAt(spans, 3, 0));
        Assert.Equal(SyntaxKind.Declaration, KindAt(spans, 3, 4));
        Assert.Equal(SyntaxKind.Builtin, KindAt(spans, 3, 12));
        Assert.Equal(SyntaxKind.Type, KindAt(spans, 3, 23));
        Assert.Equal(SyntaxKind.Property, KindAt(spans, 4, 4));
        Assert.Equal(SyntaxKind.Builtin, KindAt(spans, 4, 11));
        Assert.Equal(SyntaxKind.Number, KindAt(spans, 4, 15));
        Assert.Equal(SyntaxKind.Markup, KindAt(spans, 5, 11));
        Assert.Equal(SyntaxKind.Text, KindAt(spans, 5, 14));
        Assert.Equal(SyntaxKind.Markup, KindAt(spans, 5, 24));
        Assert.Equal(SyntaxKind.Markup, KindAt(spans, 5, 32));
        Assert.Equal(SyntaxKind.Reference, KindAt(spans, 6, 11));
        Assert.Equal(SyntaxKind.Declaration, KindAt(spans, 11, 5));
        Assert.Equal(SyntaxKind.Declaration, KindAt(spans, 11, 11));
        Assert.Equal(SyntaxKind.Declaration, KindAt(spans, 11, 16));
        Assert.Equal(SyntaxKind.Builtin, KindAt(spans, 11, 24));
        Assert.Equal(SyntaxKind.Name, KindAt(spans, 11, 28));
        Assert.Equal(SyntaxKind.Control, KindAt(spans, 11, 36));
        Assert.Equal(SyntaxKind.Text, KindAt(spans, 11, 43));
        Assert.Equal(SyntaxKind.Name, KindAt(spans, 11, 48));
        Assert.Equal(SyntaxKind.Property, KindAt(spans, 11, 52));
        Assert.Equal(SyntaxKind.Operator, KindAt(spans, 11, 57));
        Assert.Equal(SyntaxKind.Directive, KindAt(spans, 12, 0));

        HidlSwatch swatch = Assert.Single(swatches);
        Assert.Equal(4, swatch.Line);
        Assert.Equal(new Vector4(0.2f, 0.4f, 0.9f, 1.0f), swatch.Color);

        Assert.Equal(
            [
                new HidlArt(5, 23, 7, HidlArtKind.Symbol, "save"),
                new HidlArt(5, 31, 12, HidlArtKind.Icon, "pad_a"),
                new HidlArt(7, 11, 4, HidlArtKind.Symbol, "gear"),
                new HidlArt(8, 13, 6, HidlArtKind.Sprite, "button"),
                new HidlArt(9, 11, 17, HidlArtKind.Picture, "art/hero.aseprite")
            ],
            art);

        Assert.True(HidlHighlighter.IsBuiltin("print"));
        Assert.True(HidlHighlighter.IsBuiltin("compositor"));
        Assert.True(HidlHighlighter.IsBuiltin("math"));
        Assert.False(HidlHighlighter.IsBuiltin("true"));
        Assert.False(HidlHighlighter.IsBuiltin("false"));
        Assert.False(HidlHighlighter.IsBuiltin("null"));
        Assert.False(HidlHighlighter.IsBuiltin("not_a_builtin"));
    }

    [Fact]
    public void ReadLine_and_ReadAgain_match_full_Read_across_multiline_constructs()
    {
        const string before = "let a = {\n    /* block\n       comment */\n    msg: `hello\n{a.value}`\n};";
        string[] beforeLines = before.Split('\n');
        var beforeStarts = new CodeLineStart[beforeLines.Length];
        List<SyntaxSpan>[] beforeSpans = Hidl.Read(before, beforeLines.Length, beforeStarts);

        for (int i = 0; i < beforeLines.Length; i++)
        {
            List<SyntaxSpan> single = Hidl.ReadLine(beforeLines[i], beforeStarts[i]);
            Assert.Equal(beforeSpans[i], single);
        }

        const string after = "let a = {\n    /* block\n       edited comment */\n    msg: `hello\n{a.value}`\n};";
        string[] afterLines = after.Split('\n');
        var afterStarts = new CodeLineStart[afterLines.Length];
        var fullStarts = new CodeLineStart[afterLines.Length];

        var colouring = new CodeColouring(beforeLines, beforeSpans, beforeStarts);
        List<SyntaxSpan>[] incremental = Hidl.ReadAgain(after, afterLines, afterStarts, colouring);
        List<SyntaxSpan>[] full = Hidl.Read(after, afterLines.Length, fullStarts);

        Assert.Equal(fullStarts, afterStarts);
        for (int i = 0; i < afterLines.Length; i++)
            Assert.Equal(full[i], incremental[i]);
    }

    [Fact]
    public void Colouring_small_hidl_document_is_fast_and_low_allocation()
    {
        const string code = "let settings = {\n    theme: \"dark\",\n    color: vec(0.2, 0.4, 0.9, 1),\n};\n";
        var starts = new CodeLineStart[5];
        var swatches = new List<HidlSwatch>();
        var art = new List<HidlArt>();

        // Warm up JIT once
        Hidl.Read(code, 5, starts, swatches, art);

        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        long startStamp = Stopwatch.GetTimestamp();
        const int iterations = 20;
        for (int i = 0; i < iterations; i++)
            Hidl.Read(code, 5, starts, swatches, art);
        double elapsedMs = Stopwatch.GetElapsedTime(startStamp).TotalMilliseconds / iterations;
        long bytesPerCall = (GC.GetAllocatedBytesForCurrentThread() - allocBefore) / iterations;

        Assert.True(elapsedMs < 5.0, $"Expected < 5 ms per Read, got {elapsedMs:0.00} ms.");
        Assert.True(bytesPerCall < 32 * 1024, $"Expected < 32 KB per Read, got {bytesPerCall} bytes.");
    }
}
