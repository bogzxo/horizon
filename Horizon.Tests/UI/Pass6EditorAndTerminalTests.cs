using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;

using Horizon.Engine;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Scripting;
using Horizon.UI.Skinning;

namespace Horizon.Tests.UI;

/// <summary>
/// Headless unit and regression tests for Pass 6-D:
/// flat CSR SemanticSpans, background folding &amp; incremental TextMateHighlighter, delta-based UITextDocument
/// multi-caret undo/redo &amp; external rebase, DiagnosticCard 8-slot LRU, zero-alloc TreeView truncation,
/// unmanaged TerminalCell + sparse combining marks + HasStyledBackground, TerminalPalette 256-colour LUT parity,
/// TerminalView blink stability &amp; static cell-grid reuse, and settled Lights PaintVersion stability.
/// </summary>
public sealed class Pass6EditorAndTerminalTests
{
    private static readonly UISkin Skin = new(new UIFont(Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", "cascadia_mono"), "cascadia_mono.ttf"));
    private static readonly UIRect Bounds = new(Vector2.Zero, new Vector2(820.0f, 520.0f));

    private static void Paint(UIDrawList list, UIDrawList? previous, UIComponent root, int tick)
    {
        root.MeasureTree(Skin);
        root.ArrangeTree(Bounds);
        list.Previous = previous;
        list.Begin(Skin, tick, 1.0f / 60.0f, tick / 60.0f);
        root.PaintTree(list);
        list.End();
    }

    /* 1. CSR SemanticSpans */

    [Fact]
    public void SemanticSpans_CSR_sorts_unsorted_inputs_handles_line_shifts_and_invalidates_edited_lines()
    {
        var editor = new CodeEditor("let alpha = beta;\nlet gamma = delta;\nlet eps = zeta;")
        {
            Size = new Vector2(600.0f, 300.0f),
            Highlight = true,
        };
        editor.UpdateTree(1.0f / 60.0f);

        // Provide spans out of column and line order, plus invalid/empty entries that must be skipped cleanly
        editor.SemanticSpans =
        [
            new SemanticSpan(2, 10, 4, SyntaxKind.Field),
            new SemanticSpan(0, 12, 4, SyntaxKind.Parameter),
            new SemanticSpan(0, 4, 5, SyntaxKind.Local, SyntaxModifiers.Mutable),
            new SemanticSpan(-1, 0, 3, SyntaxKind.Class),
            new SemanticSpan(99, 0, 3, SyntaxKind.Class),
            new SemanticSpan(1, 4, 0, SyntaxKind.Local),
            new SemanticSpan(1, 4, 5, SyntaxKind.Method),
        ];

        // Line 0 spans must be sorted by Start and overlaid cleanly
        ReadOnlySpan<SyntaxSpan> line0 = editor.SemanticsOf(0, editor.Document.LineAt(0));
        Assert.Equal(2, line0.Length);
        Assert.Equal(4, line0[0].Start);
        Assert.Equal(SyntaxKind.Local, line0[0].Kind);
        Assert.Equal(SyntaxModifiers.Mutable, line0[0].Modifiers);
        Assert.Equal(12, line0[1].Start);
        Assert.Equal(SyntaxKind.Parameter, line0[1].Kind);

        Assert.Equal((SyntaxKind.Local, SyntaxModifiers.Mutable), editor.DrawnAt(0, 5));
        Assert.Equal((SyntaxKind.Parameter, SyntaxModifiers.None), editor.DrawnAt(0, 13));
        Assert.Equal((SyntaxKind.Method, SyntaxModifiers.None), editor.DrawnAt(1, 5));
        Assert.Equal((SyntaxKind.Field, SyntaxModifiers.None), editor.DrawnAt(2, 11));

        // Inserting a line at the top shifts lines down: untouched shifted lines retain their semantic spans
        editor.Document.MoveTo(new UITextPosition(0, 0));
        editor.Document.Insert("let top = 0;\n");
        Assert.Equal((SyntaxKind.Field, SyntaxModifiers.None), editor.DrawnAt(3, 11));

        // Editing a line invalidates its semantic spans immediately (text mismatch)
        editor.Document.MoveTo(new UITextPosition(1, 0));
        editor.Document.Type(' ');
        Assert.True(editor.SemanticsOf(1, editor.Document.LineAt(1)).IsEmpty);
    }

    /* 2. Background Folding, NeedsFullText & Incremental TextMateHighlighter */

    [Fact]
    public void TextMateHighlighter_NeedsFullText_is_false_and_incremental_relex_stops_early_with_full_parity()
    {
        ICodeHighlighter? cs = CodeLanguages.ForExtension(".cs");
        Assert.NotNull(cs);
        var tm = Assert.IsType<TextMateHighlighter>(cs);
        Assert.False(tm.NeedsFullText);
        Assert.True(((ICodeHighlighter)HidlHighlighter.Instance).NeedsFullText);

        const int count = 1200;
        var lines = new string[count];
        lines[0] = "namespace Demo;";
        lines[1] = "public sealed class Sample";
        lines[2] = "{";
        for (int i = 3; i < count - 1; i++)
            lines[i] = $"    public int Prop_{i} => {i} + 1;";
        lines[count - 1] = "}";

        var startsFull = new CodeLineStart[count];
        List<SyntaxSpan>[] spansInitial = tm.ReadIncremental(lines, version: 1, dirtyLine: 0, startsFull);
        Assert.Equal(count, spansInitial.Length);
        Assert.Equal(count, tm.LastLexedLineCount);

        // Edit a single line in the middle: incremental re-lex should converge in ~1-5 lines
        string[] edited = [.. lines];
        edited[600] = "    public int Prop_600 => 600 + 42;";
        var startsInc = new CodeLineStart[count];
        List<SyntaxSpan>[] spansInc = tm.ReadIncremental(edited, version: 2, dirtyLine: 600, startsInc);

        Assert.True(tm.LastLexedLineCount <= 5, $"Expected incremental single-line edit to re-lex <= 5 lines, got {tm.LastLexedLineCount}.");
        Assert.Same(spansInitial[0], spansInc[0]);
        Assert.Same(spansInitial[599], spansInc[599]);
        Assert.Same(spansInitial[605], spansInc[605]);
        Assert.Same(spansInitial[^1], spansInc[^1]);

        // Verify exact parity with a full ReadLines()
        var startsRef = new CodeLineStart[count];
        List<SyntaxSpan>[] spansRef = tm.ReadLines(edited, startsRef);
        Assert.Equal(spansRef.Length, spansInc.Length);
        for (int i = 595; i <= 605; i++)
        {
            Assert.Equal(spansRef[i], spansInc[i]);
            Assert.Equal(startsRef[i].Depth, startsInc[i].Depth);
        }
    }

    [Fact]
    public void Background_colouring_precomputes_foldable_ranges_and_swaps_in_TakeColouring()
    {
        var lines = new List<string> { "namespace Demo;" };
        for (int i = 0; i < 350; i++)
        {
            lines.Add($"public class C{i}");
            lines.Add("{");
            lines.Add($"    public int V => {i};");
            lines.Add("}");
        }

        var editor = new CodeEditor(string.Join('\n', lines))
        {
            Size = new Vector2(820.0f, 520.0f),
            Highlighter = CodeLanguages.ForExtension(".cs"),
        };

        // Trigger initial Recolour + background StartColouring (file > COLOUR_AT_ONCE = 300)
        editor.UpdateTree(0.1f);

        // Pump UpdateTree until background colouring completes and TakeColouring swaps precomputed folds
        var deadline = Stopwatch.StartNew();
        while (!editor.CanFold(1202) && deadline.ElapsedMilliseconds < 5000)
        {
            editor.UpdateTree(0.05f);
            Thread.Sleep(5);
        }

        Assert.True(editor.CanFold(2));
        Assert.True(editor.CanFold(1202));

        // Fold line 2, edit a line further down, let background colouring finish again, and verify fold stays intact
        editor.ToggleFold(2);
        Assert.Contains(2, editor.FoldedLines);

        editor.Document.MoveTo(new UITextPosition(100, 0));
        editor.Document.Type(' ');
        deadline.Restart();
        while (deadline.ElapsedMilliseconds < 2000)
        {
            editor.UpdateTree(0.1f);
            Thread.Sleep(5);
            if (editor.CanFold(1202) && editor.FoldedLines.Contains(2))
                break;
        }

        Assert.Contains(2, editor.FoldedLines);
        Assert.True(editor.CanFold(1202));
    }

    /* 3. Delta-based UITextDocument Undo/Redo & External Rebase */

    [Fact]
    public void UITextDocument_delta_undo_redo_supports_coalescing_multicaret_line_ops_and_external_rebase()
    {
        var doc = new UITextDocument { Text = "alpha = 1;\nbeta = 2;\ngamma = 3;\ndelta = 4;" };

        // Coalesced typing on one line
        doc.MoveTo(new UITextPosition(1, 4));
        doc.Type('_');
        doc.Type('x');
        doc.Type('y');
        Assert.Equal("beta_xy = 2;", doc.LineAt(1));

        doc.Undo();
        Assert.Equal("beta = 2;", doc.LineAt(1));
        doc.Redo();
        Assert.Equal("beta_xy = 2;", doc.LineAt(1));

        // Multi-caret edit + Undo/Redo preserving ExtraCarets
        doc.MoveTo(new UITextPosition(0, 5));
        doc.AddCaret(new UITextPosition(2, 5));
        doc.Insert("_m");
        Assert.Equal("alpha_m = 1;", doc.LineAt(0));
        Assert.Equal("gamma_m = 3;", doc.LineAt(2));
        Assert.Equal(2, doc.CaretCount);

        doc.Undo();
        Assert.Equal("alpha = 1;", doc.LineAt(0));
        Assert.Equal("gamma = 3;", doc.LineAt(2));
        Assert.Equal(2, doc.CaretCount);

        doc.Redo();
        Assert.Equal("alpha_m = 1;", doc.LineAt(0));
        Assert.Equal("gamma_m = 3;", doc.LineAt(2));

        // MoveLines, Duplicate, CopyLines, IndentLines, Outdent, ToggleComment, Replace
        doc.MoveTo(new UITextPosition(1, 0));
        doc.MoveLines(1);
        Assert.Equal("gamma_m = 3;", doc.LineAt(1));
        Assert.Equal("beta_xy = 2;", doc.LineAt(2));
        doc.Undo();
        Assert.Equal("beta_xy = 2;", doc.LineAt(1));
        Assert.Equal("gamma_m = 3;", doc.LineAt(2));

        doc.Duplicate();
        Assert.Equal(5, doc.LineCount);
        doc.Undo();
        Assert.Equal(4, doc.LineCount);

        doc.ToggleComment();
        Assert.StartsWith("// ", doc.LineAt(1));
        doc.Undo();
        Assert.Equal("beta_xy = 2;", doc.LineAt(1));

        // External rebase on delta-based undo history
        var rebased = new UITextDocument { Text = "line0\nline1\nline2" };
        rebased.MoveTo(new UITextPosition(2, 5));
        rebased.Insert("!");
        Assert.Equal("line0\nline1\nline2!", rebased.Text);

        // External writer edits line0 while local edit was on line2
        rebased.ApplyExternal(
            "LINE0\nline1\nline2!",
            (newer, older, revised) =>
            {
                if (newer.StartsWith("line0\n") && older.StartsWith("line0\n") && revised.StartsWith("LINE0\n"))
                    return "LINE0\n" + older[6..];
                return null;
            });

        Assert.Equal("LINE0\nline1\nline2!", rebased.Text);
        Assert.True(rebased.CanUndo);
        rebased.Undo();
        Assert.Equal("LINE0\nline1\nline2", rebased.Text);
        rebased.Redo();
        Assert.Equal("LINE0\nline1\nline2!", rebased.Text);
    }

    [Fact]
    public void UITextDocument_single_line_edit_in_large_document_avoids_LOH_snapshot_copy()
    {
        var initial = new string[15_000];
        for (int i = 0; i < initial.Length; i++)
            initial[i] = $"let row_{i} = {i};";

        var doc = new UITextDocument { Text = string.Join('\n', initial) };
        doc.MoveTo(new UITextPosition(7_500, 12));

        // Warm up once
        doc.Type('a');
        doc.Undo();

        long before = GC.GetAllocatedBytesForCurrentThread();
        doc.Type('b');
        doc.Type('c');
        doc.Type('d');
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // A full string[15_000] copy alone would allocate >= 120,000 bytes; delta recording should be well under 8 KB.
        Assert.True(allocated < 8192, $"Expected compact delta undo allocation < 8 KB, got {allocated} bytes.");
        doc.Undo();
        Assert.Equal("let row_7500 = 7500;", doc.LineAt(7_500));
    }

    /* 4. DiagnosticCard 8-slot LRU */

    [Fact]
    public void DiagnosticCard_LRU_caches_multiple_diagnostics_with_zero_alloc_sweep()
    {
        var editor = new CodeEditor("a0\na1\na2\na3\na4\na5")
        {
            Size = new Vector2(600.0f, 300.0f),
            Diagnostics =
            [
                new UICodeDiagnostic(0, 0, 2, "Error zero (E000)"),
                new UICodeDiagnostic(1, 0, 2, "Warning one (W001)", Warning: true),
                new UICodeDiagnostic(2, 0, 2, "Hint two (H002)", Warning: true, Hint: true),
                new UICodeDiagnostic(3, 0, 2, "Error three (E003)"),
                new UICodeDiagnostic(4, 0, 2, "Warning four (W004)", Warning: true),
                new UICodeDiagnostic(5, 0, 2, "Error five (E005)"),
            ]
        };

        UITextPosition[] positions =
        [
            new(0, 0), new(1, 0), new(2, 0), new(3, 0), new(4, 0), new(5, 0)
        ];

        // Warm up all 6 cards into the 8-slot LRU cache
        string[] expected = new string[positions.Length];
        for (int i = 0; i < positions.Length; i++)
            expected[i] = editor.DiagnosticCard(positions[i], string.Empty);

        // Sweeping back and forth across all 6 diagnostics must hit the LRU cache with 0 managed allocations
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int round = 0; round < 3; round++)
        {
            for (int i = 0; i < positions.Length; i++)
            {
                string actual = editor.DiagnosticCard(positions[i], string.Empty);
                Assert.Same(expected[i], actual);
            }
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    /* 5. Unmanaged TerminalCell, Combining Marks & HasStyledBackground */

    [Fact]
    public void TerminalCell_is_unmanaged_and_preserves_combining_marks_and_HasStyledBackground()
    {
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<TerminalCell>());

        var screen = new TerminalScreen(10, 4);
        Assert.False(screen.Grid[0].HasStyledBackground);

        // Plain ASCII + combining marks keep HasStyledBackground == false
        screen.Feed("ab\u0301c");
        Assert.False(screen.Grid[0].HasStyledBackground);
        Assert.True(screen[1, 0].HasCombining);
        Assert.Equal("\u0301", screen.Grid[0].CombiningAt(1));
        Assert.Equal("\u0301", screen[1, 0].Combining);
        Assert.Equal("ab\u0301c", screen.Grid[0].Text());

        // Reflow via Resize preserves combining marks
        screen.Resize(20, 4);
        Assert.True(screen[1, 0].HasCombining);
        Assert.Equal("\u0301", screen.Grid[0].CombiningAt(1));
        Assert.Equal("ab\u0301c", screen.Grid[0].Text());

        // InsertCells (ICH) and DeleteCells (DCH) shift sparse combining marks with their base cell
        screen.Feed("\u001b[1;1H\u001b[2@"); // insert 2 blanks at col 0 -> 'b' moves from col 1 to col 3
        Assert.Null(screen.Grid[0].CombiningAt(1));
        Assert.Equal("\u0301", screen.Grid[0].CombiningAt(3));

        screen.Feed("\u001b[1;1H\u001b[1P"); // delete 1 cell at col 0 -> 'b' moves from col 3 to col 2
        Assert.Null(screen.Grid[0].CombiningAt(3));
        Assert.Equal("\u0301", screen.Grid[0].CombiningAt(2));

        // Writing styled background sets HasStyledBackground == true; full-row plain erase resets it
        screen.Feed("\r\n\u001b[41mred\u001b[0m");
        Assert.True(screen.Grid[1].HasStyledBackground);
        screen.Feed("\u001b[2K");
        Assert.False(screen.Grid[1].HasStyledBackground);

        // Inverse attribute also sets HasStyledBackground == true
        screen.Feed("\u001b[7minv\u001b[0m");
        Assert.True(screen.Grid[1].HasStyledBackground);
    }

    /* 6. TerminalPalette 256-colour LUT parity & TerminalSymbols threshold */

    [Fact]
    public void TerminalPalette_256_colour_LUT_matches_xterm_formula_for_all_indices()
    {
        var palette = new TerminalPalette();
        Assert.Equal(256, palette.Lut.Length);

        for (int i = 0; i < 16; i++)
            Assert.Equal(palette.Ansi[i], palette.Of(TerminalColor.Indexed(i), foreground: true));

        static float Level(int step) => step == 0 ? 0.0f : (55 + step * 40) / 255.0f;
        for (int i = 16; i < 232; i++)
        {
            int idx = i - 16;
            var expected = new Vector4(Level(idx / 36), Level(idx / 6 % 6), Level(idx % 6), 1.0f);
            Assert.Equal(expected, palette.Of(TerminalColor.Indexed(i), foreground: true));
            Assert.Equal(expected, palette.Lut[i]);
        }

        for (int i = 232; i < 256; i++)
        {
            float grey = (8 + (i - 232) * 10) / 255.0f;
            var expected = new Vector4(grey, grey, grey, 1.0f);
            Assert.Equal(expected, palette.Of(TerminalColor.Indexed(i), foreground: true));
            Assert.Equal(expected, palette.Lut[i]);
        }

        // Plain ASCII and everything below MIN_CODE_POINT must never map to a symbol
        for (int cp = 0; cp < TerminalSymbols.MIN_CODE_POINT; cp++)
            Assert.Null(TerminalSymbols.IconOf(cp));
        Assert.Equal("triangle-right", TerminalSymbols.IconOf(TerminalSymbols.MIN_CODE_POINT));
        Assert.Equal("triangle-right", TerminalSymbols.IconOf(TerminalSymbols.MAX_CODE_POINT));
    }

    /* 7. TerminalView blink stability & static cell-grid reuse */

    [Fact]
    public void TerminalView_cursor_quad_stays_structurally_stable_and_reuses_static_grid_across_blinks()
    {
        using var compositor = new UICompositor(new Camera2D(new Vector2(820.0f, 520.0f)));
        var module = compositor.CreateModule();
        var view = module.AddComponent(new TerminalView
        {
            Size = new Vector2(820.0f, 520.0f),
            CursorGlide = 0.0f,
        });
        view.Focus();
        view.Feed("hello terminal world\r\nsecond line of output");

        var a = new UIDrawList();
        var b = new UIDrawList();
        var fresh = new UIDrawList();

        bool previousCheck = UIDrawList.CheckReuse;
        try
        {
            UIDrawList.CheckReuse = false;

            // Settle initial resize and record lit frame (a holds -PaintId static grid record)
            Paint(a, null, view, 1);
            int litQuadCount = a.Items.Length;

            // Advance across blink boundary (0.53s) so cursor blinks off:
            // outer PaintVersion changes, but the static cell-grid record (-PaintId) from a must be reused!
            view.UpdateTree(0.55f);
            Paint(b, a, view, 2);
            Assert.True(b.Reused > 0, "Expected static terminal cell-grid slice to be reused across cursor blink.");
            Assert.Equal(litQuadCount, b.Items.Length);
            Assert.False(b.SameAs(a));

            // Reused blink-off frame must be byte-identical to a fresh paint
            Paint(fresh, null, view, 2);
            Assert.True(b.SameAs(fresh));

            // Blink back on -> reuses static grid again from b and matches the original lit frame
            view.UpdateTree(0.55f);
            Paint(a, b, view, 3);
            Assert.True(a.Reused > 0);
            Assert.Equal(litQuadCount, a.Items.Length);
            Paint(fresh, null, view, 3);
            Assert.True(a.SameAs(fresh));
        }
        finally
        {
            UIDrawList.CheckReuse = previousCheck;
        }
    }

    /* 8. Settled Lights PaintVersion stability */

    [Fact]
    public void Lights_settled_static_light_keeps_stable_PaintVersion_and_reuses_draw_list()
    {
        using var compositor = new UICompositor(new Camera2D(new Vector2(820.0f, 520.0f)));
        var module = compositor.CreateModule();
        var lights = module.AddComponent(new Lights { Follow = 20.0f, Fade = 10.0f });

        // Ramp light to settled Level == 1.0f
        for (int i = 0; i < 60; i++)
        {
            lights.Shine(1, new Vector2(120.0f, 140.0f), 80.0f, new Vector4(0.3f, 0.6f, 1.0f, 0.25f));
            lights.UpdateTree(0.05f);
        }

        var a = new UIDrawList();
        var b = new UIDrawList();
        bool previousCheck = UIDrawList.CheckReuse;
        try
        {
            UIDrawList.CheckReuse = false;
            lights.Shine(1, new Vector2(120.0f, 140.0f), 80.0f, new Vector4(0.3f, 0.6f, 1.0f, 0.25f));
            lights.UpdateTree(0.016f);
            Paint(a, null, lights, 1);

            lights.Shine(1, new Vector2(120.0f, 140.0f), 80.0f, new Vector4(0.3f, 0.6f, 1.0f, 0.25f));
            lights.UpdateTree(0.016f);
            Paint(b, a, lights, 2);

            Assert.True(b.Reused > 0, "Expected settled static Lights to reuse its draw list.");
        }
        finally
        {
            UIDrawList.CheckReuse = previousCheck;
        }
    }
}
