using System.Diagnostics;
using System.Numerics;
using System.Reflection;

using Horizon.Engine;
using Horizon.Rendering.Spriting;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Scripting;
using Horizon.UI.Skinning;

namespace Horizon.Tests.UI;

/// <summary>
/// Pass 4 correctness and hot-path regression tests for <see cref="CodeEditor"/>:
/// fold interval tracking across edits, stale syntax span clamping, sticky header horizontal scroll,
/// wrapped <see cref="CodeEditor.PieceBounds"/> rects, bounded/async occurrence scanning,
/// O(N) <see cref="CodeEditor.PositionAt"/>, and IL zero-closure verification on modified hot paths.
/// </summary>
public class Pass4CodeEditorTests
{
    private const BindingFlags ALL = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private const byte NEWOBJ = 0x73;

    private static readonly UISkin Skin = new(new UIFont(Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", "cascadia_mono"), "cascadia_mono.ttf"));
    private static readonly UIRect Screen = new(Vector2.Zero, new Vector2(900.0f, 600.0f));

    private static void Paint(UIDrawList list, UIComponent root, int tick = 1)
    {
        root.MeasureTree(Skin);
        root.ArrangeTree(Screen);
        list.Previous = null;
        list.Begin(Skin, tick, 1.0f / 120.0f, tick / 120.0f);
        root.PaintTree(list);
        list.End();
    }

    private static void InvokeNonPublic(CodeEditor editor, string methodName, params object?[]? args) =>
        typeof(CodeEditor).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, args);

    [Fact]
    public void UpdateFolds_shifts_and_clamps_foldable_and_folded_intervals_across_line_inserts_and_deletes()
    {
        var editor = new CodeEditor("a\nb\nblock: {\n    x: 1\n    y: 2\n}\nafter\n")
        {
            Size = new Vector2(820.0f, 520.0f),
        };

        InvokeNonPublic(editor, "Recolour");
        InvokeNonPublic(editor, "UpdateFolds");
        Assert.True(editor.CanFold(2));
        editor.ToggleFold(2);
        Assert.Contains(2, editor.FoldedLines);

        // Insert two lines above the fold at line 0 -> fold moves from 2..5 to 4..7 and stays folded.
        editor.Document.MoveTo(new UITextPosition(0, 0));
        editor.Document.Insert("head1\nhead2\n");
        InvokeNonPublic(editor, "UpdateFolds");

        Assert.False(editor.CanFold(2));
        Assert.True(editor.CanFold(4));
        Assert.Contains(4, editor.FoldedLines);

        var isHidden = typeof(CodeEditor).GetMethod("IsHidden", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] args = [5, 0];
        Assert.True((bool)isHidden.Invoke(editor, args)!);
        Assert.Equal(4, (int)args[1]!);

        // Delete one line above the fold -> fold moves from 4..7 to 3..6 and stays folded.
        editor.Document.Select(new UITextPosition(0, 0), new UITextPosition(1, 0));
        editor.Document.Backspace();
        InvokeNonPublic(editor, "UpdateFolds");

        Assert.True(editor.CanFold(3));
        Assert.Contains(3, editor.FoldedLines);
        args = [4, 0];
        Assert.True((bool)isHidden.Invoke(editor, args)!);
        Assert.Equal(3, (int)args[1]!);

        // Moving caret inside the fold opens it immediately.
        editor.Document.MoveTo(new UITextPosition(4, 0));
        InvokeNonPublic(editor, "UpdateFolds");
        Assert.Empty(editor.FoldedLines);
    }

    [Fact]
    public void UpdateFolds_unfolds_nested_folds_and_drops_deleted_opening_lines()
    {
        var editor = new CodeEditor("outer\n  inner\n    body\n  end\nend\n")
        {
            Size = new Vector2(820.0f, 520.0f),
        };
        editor.SetNativeFoldingRanges([new(0, 4), new(1, 3)]);
        InvokeNonPublic(editor, "UpdateFolds");

        editor.ToggleFold(0);
        editor.ToggleFold(1);
        Assert.Contains(0, editor.FoldedLines);
        Assert.Contains(1, editor.FoldedLines);

        // Caret landing inside both nested folds opens both enclosing folds in a single update.
        editor.Document.MoveTo(new UITextPosition(2, 0));
        InvokeNonPublic(editor, "UpdateFolds");
        Assert.Empty(editor.FoldedLines);

        // Re-fold inner, then delete its opening line -> inner fold is dropped cleanly without corruption.
        editor.Document.MoveTo(new UITextPosition(0, 0));
        editor.ToggleFold(1);
        Assert.Contains(1, editor.FoldedLines);
        editor.Document.Select(new UITextPosition(0, 0), new UITextPosition(2, 0));
        editor.Document.Backspace();
        InvokeNonPublic(editor, "UpdateFolds");
        Assert.DoesNotContain(1, editor.FoldedLines);
    }

    [Fact]
    public void Stale_syntax_spans_and_rapid_edits_clamp_cleanly_in_paint_and_sticky()
    {
        var lines = new List<string> { "outer_block_header_with_long_name: {" };
        for (int i = 0; i < 40; i++)
            lines.Add($"    let value_{i} = {i} + 100");
        lines.Add("}");

        var editor = new CodeEditor(string.Join('\n', lines))
        {
            Size = new Vector2(820.0f, 520.0f),
            StickyScroll = true,
        };

        var list = new UIDrawList();
        editor.UpdateTree(1.0f / 60.0f);
        Paint(list, editor, 1);

        // Inject deliberately out-of-bounds and overlapping syntax spans on line 0 and line 10,
        // simulating stale colouring before background re-highlighting completes.
        var spansField = typeof(CodeEditor).GetField("spans", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var colouredLinesField = typeof(CodeEditor).GetField("colouredLines", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var spans = (List<SyntaxSpan>[])spansField.GetValue(editor)!;
        var colouredLines = (string[])colouredLinesField.GetValue(editor)!;

        // Shorten line 0 and line 10 while keeping colouredLines matching to force Paint/PaintSticky through the stale span list.
        editor.Document.Select(new UITextPosition(0, 5), new UITextPosition(0, editor.Document.LineAt(0).Length));
        editor.Document.Insert(" {");
        colouredLines[0] = editor.Document.LineAt(0);
        spans[0] =
        [
            new SyntaxSpan(-2, 5, SyntaxKind.Name),
            new SyntaxSpan(0, 4, SyntaxKind.Keyword),
            new SyntaxSpan(2, 10, SyntaxKind.Text),
            new SyntaxSpan(50, 20, SyntaxKind.Comment),
        ];

        editor.Document.Select(new UITextPosition(10, 0), new UITextPosition(10, editor.Document.LineAt(10).Length));
        editor.Document.Insert("x");
        colouredLines[10] = editor.Document.LineAt(10);
        spans[10] =
        [
            new SyntaxSpan(0, 100, SyntaxKind.Name),
            new SyntaxSpan(0, 5, SyntaxKind.Number),
        ];

        // Scroll into the block so PaintSticky draws line 0 while PaintCodeLaidOut draws line 10.
        float lineHeight = editor.PlaceOf(new UITextPosition(0, 0)).Y - editor.PlaceOf(new UITextPosition(1, 0)).Y;
        editor.ScrollPixels = 8.0f * lineHeight;
        Paint(list, editor, 2);
        Assert.True(list.Items.Length > 0);
    }

    [Fact]
    public void PaintSticky_shifts_header_text_by_horizontal_scrollX_while_keeping_gutter_anchored()
    {
        var lines = new List<string> { "outer_scope_identifier_with_extra_width: {" };
        for (int i = 0; i < 60; i++)
            lines.Add($"    let item_{i} = {i} * 2 + 1234567890");
        lines.Add("}");

        var editor = new CodeEditor(string.Join('\n', lines))
        {
            Size = new Vector2(460.0f, 320.0f),
            WordWrap = false,
            LineNumbers = true,
            StickyScroll = true,
        };

        var atZero = new UIDrawList();
        var atScrolled = new UIDrawList();
        editor.UpdateTree(1.0f / 60.0f);
        Paint(atZero, editor, 1);

        float lineHeight = editor.PlaceOf(new UITextPosition(0, 0)).Y - editor.PlaceOf(new UITextPosition(1, 0)).Y;
        editor.ScrollPixels = 10.0f * lineHeight;

        // Paint with scrollX = 0
        editor.ScrollXPixels = 0.0f;
        Paint(atZero, editor, 2);
        Assert.Equal([0], editor.StickyShown);

        // Paint with scrollX = 64
        const float shiftX = 64.0f;
        editor.ScrollXPixels = shiftX;
        Paint(atScrolled, editor, 3);
        Assert.Equal([0], editor.StickyShown);

        // Inspect text quads on the sticky strip (top row near CodeBounds.Max.Y).
        // Quads in the gutter (X < CodeBounds.Min.X) must stay anchored; quads in the code area must shift left.
        float codeMinX = editor.CodeBounds.Min.X;
        float stickyMinY = editor.CodeBounds.Max.Y - lineHeight - 1.0f;
        float stickyMaxY = editor.CodeBounds.Max.Y + 1.0f;

        // Gutter line-number column sits strictly left of the chevron/change-bar gap; code glyphs clipped at
        // CodeBounds.Min.X can begin a fraction of a character left of codeMinX while horizontally scrolled.
        float gutterMaxX = codeMinX - 10.0f;
        var gutterZero = CollectGlyphXs(atZero, 0.0f, gutterMaxX, stickyMinY, stickyMaxY);
        var gutterScrolled = CollectGlyphXs(atScrolled, 0.0f, gutterMaxX, stickyMinY, stickyMaxY);
        Assert.NotEmpty(gutterZero);
        Assert.Equal(gutterZero, gutterScrolled);

        var codeZero = CollectGlyphXs(atZero, codeMinX, editor.CodeBounds.Max.X, stickyMinY, stickyMaxY);
        var codeScrolled = CollectGlyphXs(atScrolled, codeMinX, editor.CodeBounds.Max.X, stickyMinY, stickyMaxY);
        Assert.NotEmpty(codeZero);
        Assert.NotEmpty(codeScrolled);

        // The rightmost sticky text glyph in the scrolled view must sit shifted left compared to the unshifted view.
        Assert.True(codeScrolled[^1] < codeZero[^1] - 10.0f,
            $"Expected sticky header text to shift left with ScrollXPixels={shiftX}, got max X {codeZero[^1]} -> {codeScrolled[^1]}.");
    }

    private static List<float> CollectGlyphXs(UIDrawList list, float minX, float maxX, float minY, float maxY)
    {
        var xs = new List<float>();
        foreach (ref readonly SpriteItem item in list.Items)
        {
            uint tex = SpriteItem.TextureOf(item.Flags);
            if (tex == SpriteItem.NoTexture)
                continue;
            float centerY = item.Origin.Y + item.AxisY.Y * 0.5f;
            if (centerY < minY || centerY > maxY)
                continue;
            if (item.Origin.X >= minX && item.Origin.X <= maxX)
                xs.Add(MathF.Round(item.Origin.X, 2));
        }
        xs.Sort();
        return xs;
    }

    [Fact]
    public void Piece_bounds_respect_wrapped_row_boundaries_without_overflowing()
    {
        string longLine = string.Join(' ', Enumerable.Range(0, 20).Select(i => $"token_{i:D2}"));
        var editor = new CodeEditor(longLine)
        {
            Size = new Vector2(260.0f, 300.0f),
            WordWrap = true,
            LineNumbers = true,
        };

        var list = new UIDrawList();
        editor.UpdateTree(1.0f / 60.0f);
        Paint(list, editor, 1);

        Assert.True(editor.RowCount >= 2, "Expected long line to wrap across multiple visual rows.");

        // Find the first wrap boundary by comparing PlaceOf Y coordinates across columns.
        float firstRowY = editor.PlaceOf(new UITextPosition(0, 0)).Y;
        int wrapCol = -1;
        for (int col = 1; col < longLine.Length; col++)
        {
            if (MathF.Abs(editor.PlaceOf(new UITextPosition(0, col)).Y - firstRowY) > 1.0f)
            {
                wrapCol = col;
                break;
            }
        }
        Assert.True(wrapCol > 2, "Expected a wrap boundary after the first few characters.");

        // A span ending exactly at wrapCol must stay on row 0 and end right after column (wrapCol - 1).
        UIRect exactEnd = editor.PieceBounds(0, 0, wrapCol);
        UIRect oneShort = editor.PieceBounds(0, 0, wrapCol - 1);
        Assert.Equal(oneShort.Max.Y, exactEnd.Max.Y);
        Assert.True(exactEnd.Max.X > oneShort.Max.X);
        Assert.True(exactEnd.Max.X <= editor.CodeBounds.Max.X + 2.0f,
            $"Wrapped Piece() ending at row boundary overflowed CodeBounds.Max.X ({exactEnd.Max.X} > {editor.CodeBounds.Max.X}).");

        // A span extending past wrapCol must clamp to row 0's end rather than extrapolating off the right edge.
        UIRect crossing = editor.PieceBounds(0, 0, Math.Min(longLine.Length, wrapCol + 30));
        Assert.Equal(exactEnd.Max.Y, crossing.Max.Y);
        Assert.Equal(exactEnd.Max.X, crossing.Max.X, precision: 2);
    }

    [Fact]
    public void FindOccurrences_bounds_synchronous_scan_on_large_document_and_completes_async()
    {
        const int lineCount = 20_000;
        var lines = new string[lineCount];
        for (int i = 0; i < lineCount; i++)
            lines[i] = i % 100 == 0 ? "let target_symbol = 1" : $"let filler_{i} = {i}";

        var editor = new CodeEditor(string.Join('\n', lines))
        {
            Size = new Vector2(820.0f, 520.0f),
            Highlight = true,
            LightOccurrences = true,
        };

        editor.Document.MoveTo(new UITextPosition(10_000, 6));

        var sw = Stopwatch.StartNew();
        InvokeNonPublic(editor, "FindOccurrences");
        sw.Stop();

        // Synchronous bounded window immediately highlights nearby occurrences without scanning all 20,000 lines.
        Assert.True(editor.Occurrences.Count >= 2, "Expected visible-window occurrences immediately after FindOccurrences.");
        Assert.True(editor.Occurrences.Count < 200, "Expected synchronous scan to be bounded to the window around the caret.");
        Assert.True(sw.ElapsedMilliseconds < 100, $"Bounded FindOccurrences took too long: {sw.ElapsedMilliseconds} ms.");

        // Pump Update until the background full-file occurrence scan completes and merges all 200 matches.
        var deadline = Stopwatch.StartNew();
        while (editor.Occurrences.Count < 200 && deadline.ElapsedMilliseconds < 5000)
        {
            editor.UpdateTree(1.0f / 60.0f);
            Thread.Sleep(5);
        }

        Assert.Equal(200, editor.Occurrences.Count);
    }

    [Fact]
    public void PositionAt_matches_PlaceOf_with_tabs_and_inlays_and_scales_linearly_on_long_lines()
    {
        string line = "\talpha\tbeta_gamma delta_epsilon";
        var editor = new CodeEditor(line)
        {
            Size = new Vector2(820.0f, 300.0f),
            WordWrap = false,
            InlayHints = [new UICodeInlay(0, 7, "param:", PadRight: true)],
        };

        var list = new UIDrawList();
        editor.UpdateTree(1.0f / 60.0f);
        Paint(list, editor, 1);

        // Every column round-trips cleanly between PlaceOf and PositionAt.
        for (int col = 0; col <= line.Length; col++)
        {
            Vector2 pt = editor.PlaceOf(new UITextPosition(0, col));
            UITextPosition hit = editor.PositionAt(pt);
            Assert.Equal(new UITextPosition(0, col), hit);
        }

        // Long 4,000-character line: O(N) PositionAt at the end of the line should run in a tiny fraction of a millisecond.
        string longLine = new string('a', 4000);
        var longEditor = new CodeEditor(longLine)
        {
            Size = new Vector2(820.0f, 300.0f),
            WordWrap = false,
        };
        longEditor.UpdateTree(1.0f / 60.0f);
        Paint(list, longEditor, 2);

        Vector2 endPoint = longEditor.PlaceOf(new UITextPosition(0, 3999));
        Assert.Equal(new UITextPosition(0, 3999), longEditor.PositionAt(endPoint));

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 50; i++)
            Assert.Equal(3999, longEditor.PositionAt(endPoint).Column);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 150, $"50 PositionAt calls on a 4,000-char line took {sw.ElapsedMilliseconds} ms (expected O(N)).");
    }

    [Fact]
    public void Gutter_uses_compact_default_width_and_expands_cleanly_for_hundreds_of_lines()
    {
        // Use typical code zoom scale (0.155f as in Hex) as well as proportional check at default skin scale.
        var compact = new CodeEditor("a\nb\nc") { Size = new Vector2(600.0f, 300.0f), LineNumbers = true, TextScale = 0.155f };
        var medium = new CodeEditor(string.Join('\n', Enumerable.Range(1, 150))) { Size = new Vector2(600.0f, 300.0f), LineNumbers = true, TextScale = 0.155f };
        var large = new CodeEditor(string.Join('\n', Enumerable.Range(1, 1200))) { Size = new Vector2(600.0f, 300.0f), LineNumbers = true, TextScale = 0.155f };

        var list = new UIDrawList();
        Paint(list, compact, 1);
        Paint(list, medium, 2);
        Paint(list, large, 3);

        float gutter2 = compact.CodeBounds.Min.X - compact.Bounds.Min.X;
        float gutter3 = medium.CodeBounds.Min.X - medium.Bounds.Min.X;
        float gutter4 = large.CodeBounds.Min.X - large.Bounds.Min.X;

        Assert.True(gutter2 > 20.0f && gutter2 < gutter3, $"Expected 2-digit compact gutter ({gutter2}) < 3-digit gutter ({gutter3}).");
        Assert.True(gutter3 < gutter4, $"Expected 3-digit gutter ({gutter3}) < 4-digit gutter ({gutter4}).");
        Assert.True(gutter2 < 65.0f, $"Expected compact code-scale default gutter width under 65px, got {gutter2}.");
    }

    [Fact]
    public void Discrete_wheel_zoom_snaps_immediately_without_delayed_rest_resize()
    {
        var editor = new CodeEditor("let x = 42;")
        {
            Size = new Vector2(600.0f, 300.0f),
            TextScale = 0.155f,
        };
        editor.OnZoom = steps => editor.TextScale *= Horizon.Input.PreciseScroll.ZoomFactor(steps, 1.1f);
        editor.OnPinch = factor => editor.TextScale *= factor;

        var scaleOf = typeof(CodeEditor).GetMethod("ScaleOf", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var zoomingProp = typeof(CodeEditor).GetProperty("Zooming", BindingFlags.Instance | BindingFlags.NonPublic)!;

        // Discrete Ctrl+Wheel notch (PreciseWheel == false) lands immediately on its final snapped scale.
        UIWheel.Precise = false;
        editor.OnZoom(1.0f);
        float discreteImmediate = (float)scaleOf.Invoke(editor, [Skin])!;
        Assert.False((bool)zoomingProp.GetValue(editor)!);
        Thread.Sleep(240);
        float discreteAfterRest = (float)scaleOf.Invoke(editor, [Skin])!;
        Assert.Equal(discreteImmediate, discreteAfterRest);

        // Smooth trackpad pinch enters Zooming while active and snaps only after the rest delay.
        editor.OnMagnify(0.15f);
        Assert.True((bool)zoomingProp.GetValue(editor)!);
    }

    public static TheoryData<string> ModifiedHotPaths => new()
    {
        "Paint",
        "PaintCode",
        "PaintCodeLaidOut",
        "PaintSticky",
        "PaintGutter",
        "PaintArt",
        "ArtWidthOn",
        "SwatchesOn",
        "PaintFolded",
        "PaintCompletions",
        "PaintRanges",
        "PaintSpelling",
        "PaintLenses",
        "PaintRemoteCarets",
        "PaintAgentHints",
        "Update",
        "UpdateFolds",
        "FindOccurrences",
        "IsHidden",
        "PositionAt",
        "Piece",
        "IsMarked",
        "WrongAt",
    };

    [Theory]
    [MemberData(nameof(ModifiedHotPaths))]
    public void Modified_CodeEditor_hot_paths_allocate_no_closure(string methodName)
    {
        MethodInfo[] methods = [.. typeof(CodeEditor).GetMethods(ALL).Where(m => m.Name == methodName)];
        Assert.True(methods.Length > 0, $"CodeEditor has no method named {methodName}.");

        foreach (MethodInfo method in methods)
        {
            string? closure = ClosureMadeIn(method);
            Assert.True(closure is null, $"CodeEditor.{methodName} allocates closure {closure}.");
        }
    }

    private static string? ClosureMadeIn(MethodBase method)
    {
        byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
            return null;

        Type[]? ofType = method.DeclaringType is { IsGenericType: true } owner ? owner.GetGenericArguments() : null;
        Type[]? ofMethod = method.IsGenericMethod ? method.GetGenericArguments() : null;

        for (int i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != NEWOBJ)
                continue;

            MethodBase? made;
            try
            {
                made = method.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1), ofType, ofMethod);
            }
            catch (Exception)
            {
                continue;
            }

            if (made?.DeclaringType is { } declared && declared.Name.StartsWith("<>c__DisplayClass", StringComparison.Ordinal))
                return declared.Name;
        }

        return null;
    }
}
