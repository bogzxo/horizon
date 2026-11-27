using System.Numerics;
using Horizon.Engine;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.Tests.UI;

/// <summary>
/// Regression tests for <see cref="CodeEditor"/> draw-list reuse across pointer motion and caret-blink half-cycles,
/// plus minimap run caching and invalidation on document edits.
/// </summary>
[Collection("UI focus restoration")]
public class CodeEditorPerfTests
{
    private static readonly UISkin Skin = new(new UIFont(Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", "cascadia_mono"), "cascadia_mono.ttf"));
    private static readonly UIRect Screen = new(Vector2.Zero, new Vector2(900.0f, 600.0f));

    private static string SampleCode(int lines) =>
        string.Join('\n', Enumerable.Range(0, lines).Select(i =>
            i % 25 == 0 ? $"// TODO: review block {i}" : $"let value_{i} = ({i} + 1) * 2"));

    private static void Paint(UIDrawList list, UIDrawList? previous, UIComponent root, int tick)
    {
        root.MeasureTree(Skin);
        root.ArrangeTree(Screen);
        list.Previous = previous;
        list.Begin(Skin, tick, 1.0f / 120.0f, tick / 120.0f);
        root.PaintTree(list);
        list.End();
    }

    private static void SetPointer(UICompositor compositor, Vector2 position) =>
        typeof(UICompositor).GetProperty(nameof(UICompositor.Pointer))!.SetValue(compositor, new UIPointer(position, Down: false));

    [Fact]
    public void Unfocused_editor_reuses_draw_list_while_pointer_moves_outside_and_over_plain_code()
    {
        using var compositor = new UICompositor(new Camera2D(new Vector2(900.0f, 600.0f)));
        var module = compositor.CreateModule();
        var code = module.AddComponent(new CodeEditor(SampleCode(200))
        {
            Size = new Vector2(820.0f, 520.0f),
            Minimap = true,
        });

        var a = new UIDrawList();
        var b = new UIDrawList();
        var fresh = new UIDrawList();

        bool previousCheck = UIDrawList.CheckReuse;
        try
        {
            UIDrawList.CheckReuse = false;

            // Warm up and settle on ticks 1-2
            code.UpdateTree(1.0f / 60.0f);
            SetPointer(compositor, module.ToWorld(new Vector2(880.0f, 580.0f)));
            Paint(a, null, code, 1);
            Paint(b, a, code, 2);

            // Pointer moves outside CodeEditor (e.g. dragging another window over/beside it) -> 100% reuse
            SetPointer(compositor, module.ToWorld(new Vector2(895.0f, 10.0f)));
            code.IsHovered = false;
            Paint(a, b, code, 3);
            Assert.True(a.Reused > 0, "Unfocused CodeEditor must reuse its draw list when the pointer moves outside it.");
            Paint(fresh, null, code, 3);
            Assert.True(a.SameAs(fresh));
            Assert.Null(a.FramesDiffer());

            SetPointer(compositor, module.ToWorld(new Vector2(5.0f, 595.0f)));
            code.IsHovered = false;
            Paint(b, a, code, 4);
            Assert.True(b.Reused > 0, "Subsequent pointer moves outside CodeEditor must continue reusing.");
            Paint(fresh, null, code, 4);
            Assert.True(b.SameAs(fresh));

            // Pointer hovers and moves inside plain code area (not gutter, minimap, or scrollbar) -> still reuses!
            SetPointer(compositor, module.ToWorld(new Vector2(code.CodeBounds.Min.X + 80.0f, code.CodeBounds.Center.Y)));
            code.IsHovered = true;
            Paint(a, b, code, 5); // IsHovered transitioned false -> true
            Paint(b, a, code, 6); // Settled hovered state
            Assert.True(b.Reused > 0);

            SetPointer(compositor, module.ToWorld(new Vector2(code.CodeBounds.Min.X + 140.0f, code.CodeBounds.Center.Y + 24.0f)));
            code.IsHovered = true;
            Paint(a, b, code, 7);
            Assert.True(a.Reused > 0, "Pointer movement inside plain code should not invalidate PaintVersion.");
            Paint(fresh, null, code, 7);
            Assert.True(a.SameAs(fresh));

            // Verify with CheckReuse enabled that the recorded draw list matches fresh paint identically
            UIDrawList.CheckReuse = true;
            SetPointer(compositor, module.ToWorld(new Vector2(code.CodeBounds.Min.X + 180.0f, code.CodeBounds.Center.Y - 12.0f)));
            code.IsHovered = true;
            Paint(b, a, code, 8);
            Assert.True(b.SameAs(a));
            Assert.Null(b.FramesDiffer());

            // Moving pointer onto the minimap must invalidate reuse so the slider highlight updates
            UIDrawList.CheckReuse = false;
            Paint(a, b, code, 9);
            SetPointer(compositor, module.ToWorld(new Vector2(code.CodeBounds.Max.X + 20.0f, code.CodeBounds.Center.Y)));
            code.IsHovered = true;
            Paint(b, a, code, 10);
            Assert.Equal(0, b.Reused);
        }
        finally
        {
            UIDrawList.CheckReuse = previousCheck;
        }
    }

    [Fact]
    public void Focused_editor_reuses_within_blink_half_cycle_and_invalidates_on_blink_flip()
    {
        using var compositor = new UICompositor(new Camera2D(new Vector2(900.0f, 600.0f)));
        var module = compositor.CreateModule();
        var code = module.AddComponent(new CodeEditor(SampleCode(80))
        {
            Size = new Vector2(820.0f, 520.0f),
            Minimap = true,
        });
        code.Focus();
        Assert.True(code.IsFocused);

        var a = new UIDrawList();
        var b = new UIDrawList();
        var fresh = new UIDrawList();

        bool previousCheck = UIDrawList.CheckReuse;
        try
        {
            UIDrawList.CheckReuse = false;

            // Advance slightly into the lit half-cycle (blink = 0.05 < 0.53) and settle
            code.UpdateTree(0.05f);
            Paint(a, null, code, 1);
            Paint(b, a, code, 2);
            long litVersion = code.CurrentPaintVersion;
            Assert.True(litVersion >= 0);

            // Advance blink from 0.05 -> 0.15 (still in the same lit half-cycle)
            code.UpdateTree(0.10f);
            Assert.Equal(litVersion, code.CurrentPaintVersion);
            Paint(a, b, code, 3);
            Assert.True(a.Reused > 0, "Advancing blink within the same lit half-cycle must reuse the draw list.");
            Paint(fresh, null, code, 3);
            Assert.True(a.SameAs(fresh));
            Assert.Null(a.FramesDiffer());

            // Verify under CheckReuse = true within the same half-cycle
            UIDrawList.CheckReuse = true;
            code.UpdateTree(0.10f); // blink = 0.25 < 0.53
            Assert.Equal(litVersion, code.CurrentPaintVersion);
            Paint(b, a, code, 4);
            Assert.True(b.SameAs(a));
            Assert.Null(b.FramesDiffer());

            // Advance across the BLINK (0.53s) boundary so the caret turns off -> must invalidate!
            UIDrawList.CheckReuse = false;
            Paint(a, b, code, 5);
            code.UpdateTree(0.35f); // blink = 0.60 >= 0.53
            long unlitVersion = code.CurrentPaintVersion;
            Assert.NotEqual(litVersion, unlitVersion);
            Paint(b, a, code, 6);
            Assert.Equal(0, b.Reused);
            Paint(fresh, null, code, 6);
            Assert.True(b.SameAs(fresh));

            // Next tick inside the unlit half-cycle (0.60 -> 0.70 < 1.06) reuses again
            code.UpdateTree(0.10f);
            Assert.Equal(unlitVersion, code.CurrentPaintVersion);
            Paint(a, b, code, 7);
            Assert.True(a.Reused > 0, "Advancing blink within the same unlit half-cycle must reuse the draw list.");
        }
        finally
        {
            UIDrawList.CheckReuse = previousCheck;
        }
    }

    [Fact]
    public void Minimap_caches_runs_across_selection_and_subrow_scroll_and_invalidates_on_edit()
    {
        var code = new CodeEditor(SampleCode(280))
        {
            Size = new Vector2(820.0f, 520.0f),
            Minimap = true,
            HighlightTodos = true,
        };

        var a = new UIDrawList();
        var fresh = new UIDrawList();

        // Run initial Update so synchronous Recolour() completes before first paint
        code.UpdateTree(1.0f / 60.0f);

        // First paint builds the minimap run cache
        Paint(a, null, code, 1);
        Assert.Equal(1, code.MinimapCacheMisses);
        Assert.Equal(0, code.MinimapCacheHits);

        // Moving selection forces CodeEditor to repaint (no previous list), but minimap runs hit cache!
        code.Document.Select(new UITextPosition(2, 0), new UITextPosition(2, 8));
        Paint(a, null, code, 2);
        Assert.Equal(1, code.MinimapCacheMisses);
        Assert.Equal(1, code.MinimapCacheHits);

        // Small sub-row scroll (scrolledRows unchanged) also hits minimap run cache
        code.ScrollPixels = 2.0f;
        Paint(a, null, code, 3);
        Assert.Equal(1, code.MinimapCacheMisses);
        Assert.Equal(2, code.MinimapCacheHits);

        // Verify cached minimap output is byte-identical to an uncached fresh minimap rebuild
        code.ClearMinimapCache();
        Paint(fresh, null, code, 3);
        Assert.Equal(2, code.MinimapCacheMisses);
        Assert.True(a.SameAs(fresh), "Cached minimap paint must be pixel-identical to a fresh paint.");

        // Document.Insert invalidates the minimap run cache immediately
        code.Document.MoveTo(new UITextPosition(1, 0));
        code.Document.Insert("let added = 42\n");
        Paint(a, null, code, 4);
        Assert.Equal(3, code.MinimapCacheMisses);

        // Document.Delete invalidates the minimap run cache immediately
        code.Document.MoveTo(new UITextPosition(1, 0));
        code.Document.Delete();
        Paint(a, null, code, 5);
        Assert.Equal(4, code.MinimapCacheMisses);
    }

    [Fact]
    public void Pointer_sweep_and_repaint_allocate_zero_managed_bytes()
    {
        using var compositor = new UICompositor(new Camera2D(new Vector2(900.0f, 600.0f)));
        var module = compositor.CreateModule();
        int hoverCalls = 0;
        var code = module.AddComponent(new CodeEditor(SampleCode(240))
        {
            Size = new Vector2(820.0f, 520.0f),
            Minimap = true,
            HighlightTodos = true,
            StickyScroll = true,
            HoverSource = (_, pos) =>
            {
                hoverCalls++;
                return $"line {pos.Line}";
            },
        });

        var a = new UIDrawList();
        var b = new UIDrawList();

        // Warm up layout, colouring, hover cache, minimap caches, and draw-list internal buffers
        code.UpdateTree(1.0f / 60.0f);
        Paint(a, null, code, 1);
        Paint(b, a, code, 2);

        Vector2[] sweep =
        [
            module.ToWorld(code.PlaceOf(new UITextPosition(1, 5))),
            module.ToWorld(code.PlaceOf(new UITextPosition(2, 5))),
            module.ToWorld(new Vector2(code.Bounds.Min.X + 12.0f, code.CodeBounds.Max.Y - 24.0f)),
            module.ToWorld(new Vector2(code.CodeBounds.Max.X + 20.0f, code.CodeBounds.Center.Y)),
            module.ToWorld(code.PlaceOf(new UITextPosition(1, 5))),
        ];

        // Warm sweep populates per-version hover cache and draw-list capacities
        int tick = 3;
        code.IsHovered = true;
        foreach (Vector2 pt in sweep)
        {
            SetPointer(compositor, pt);
            code.UpdateTree(1.0f / 60.0f);
            Paint(a, null, code, tick++);
            Paint(b, a, code, tick++);
        }

        int hoverCallsAfterWarmup = hoverCalls;
        Assert.True(hoverCallsAfterWarmup >= 2, "Warm sweep must query HoverSource for at least two distinct words.");

        // Measured sweep: both reused and full repaints must allocate 0 managed bytes
        // (SetPointer uses reflection, so measure only CodeEditor UpdateTree + Paint work)
        long allocated = 0;
        for (int i = 0; i < sweep.Length; i++)
        {
            SetPointer(compositor, sweep[i]);
            long before = GC.GetAllocatedBytesForCurrentThread();
            code.UpdateTree(1.0f / 60.0f);
            Paint(a, null, code, tick++);
            Paint(b, a, code, tick++);
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.Equal(hoverCallsAfterWarmup, hoverCalls);
        Assert.Equal(0, allocated);

        // AskAgain clears the hover cache so dynamic hover sources (LSP / debugger) are re-queried
        code.AskAgain();
        SetPointer(compositor, sweep[0]);
        code.UpdateTree(1.0f / 60.0f);
        Assert.True(hoverCalls > hoverCallsAfterWarmup);
    }

    [Fact]
    public void Single_line_typing_does_not_rebuild_rows_without_word_wrap()
    {
        var code = new CodeEditor(SampleCode(500))
        {
            Size = new Vector2(820.0f, 520.0f),
            WordWrap = false,
            Minimap = true,
        };

        var a = new UIDrawList();
        var fresh = new UIDrawList();

        code.UpdateTree(1.0f / 60.0f);
        Paint(a, null, code, 1);
        Assert.Equal(1, code.RowsRebuildCount);

        code.GoTo(250, 0);
        code.UpdateTree(1.0f / 60.0f);
        Paint(a, null, code, 2);
        Assert.Equal(1, code.RowsRebuildCount);

        // Typing characters on the same line must not trigger full-document row rebuilds
        int tick = 3;
        foreach (char c in "let edited = 12345; ")
        {
            code.Document.Type(c);
            code.UpdateTree(1.0f / 60.0f);
            Paint(a, null, code, tick++);
        }
        Assert.Equal(1, code.RowsRebuildCount);

        // Selection and painting on the edited line remain exact
        string editedLine = code.Document.LineAt(250);
        code.Document.Select(new UITextPosition(250, 0), new UITextPosition(250, editedLine.Length));
        Paint(a, null, code, tick++);
        Paint(fresh, null, code, tick);
        Assert.True(a.SameAs(fresh));

        // Adding a line does rebuild rows
        code.Document.MoveTo(new UITextPosition(250, editedLine.Length));
        code.Document.NewLine();
        code.UpdateTree(1.0f / 60.0f);
        Paint(a, null, code, tick + 1);
        Assert.Equal(2, code.RowsRebuildCount);
    }

    [Fact]
    public void Sticky_lines_match_nested_blocks_while_scrolling()
    {
        // Pump background colouring past COLOUR_AT_ONCE (96) so the closing brace on the last line is foldable
        var lines = new List<string> { "outer: {" };
        for (int i = 0; i < 30; i++)
        {
            lines.Add($"    block_{i}: {{");
            for (int j = 0; j < 6; j++)
                lines.Add($"        item_{j}: {i * 10 + j}");
            lines.Add("    }");
        }
        lines.Add("}");

        var code = new CodeEditor(string.Join('\n', lines))
        {
            Size = new Vector2(820.0f, 520.0f),
            StickyScroll = true,
        };

        var list = new UIDrawList();
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (!code.CanFold(0) && deadline.ElapsedMilliseconds < 5000)
        {
            code.UpdateTree(0.05f);
            Thread.Sleep(5);
        }

        Assert.True(code.CanFold(0));
        Paint(list, null, code, 1);

        // Measure exact painted line height from PlaceOf so ScrollPixels lands cleanly inside the target blocks
        float lineHeight = code.PlaceOf(new UITextPosition(0, 0)).Y - code.PlaceOf(new UITextPosition(1, 0)).Y;
        Assert.True(lineHeight > 0.0f);

        // Scroll into block_15 (starts at line 1 + 15 * 8 = 121, body on lines 122..127)
        code.ScrollPixels = 124.0f * lineHeight;
        Paint(list, null, code, 2);
        Assert.Equal([0, 121], code.StickyShown);

        // Scroll into block_25 (starts at line 1 + 25 * 8 = 201, body on lines 202..207)
        code.ScrollPixels = 204.0f * lineHeight;
        Paint(list, null, code, 3);
        Assert.Equal([0, 201], code.StickyShown);
    }
}
