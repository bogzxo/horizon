using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;

using Horizon.Core.Threading;
using Horizon.Engine;
using Horizon.Graphics;
using Horizon.Rendering;
using Horizon.Rendering.Spriting;
using Horizon.Rendering.Text;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Scripting;
using Horizon.UI.Skinning;

namespace Horizon.Tests.UI;

/// <summary>
/// Headless unit tests for Pass 7-C:
/// single-pass dual-source subpixel text emission, <see cref="UIRenderer"/> dual-source selection &amp; 2-pass fallback
/// on manual <see cref="SpriteItem.SubpixelEraseFlag"/> quads, <see cref="PerformanceSample.Read(GameEngine, bool)"/>
/// compact mode, zero-alloc <see cref="TreeView.WithoutIcons"/>, <see cref="CodeEditor"/> double-buffered recolour &amp;
/// foldable reuse, <see cref="CodeEditor.SemanticSpans"/> backing-array reuse, and <see cref="Label"/>/<see cref="Button"/>
/// leaf <c>Measure</c> caches.
/// </summary>
public sealed class Pass7UiDualSourceAndEditorTests
{
    private static readonly string CascadiaDir = Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", "cascadia_mono");
    private static readonly UIFont CascadiaFont = new(CascadiaDir, "cascadia_mono.ttf");
    private static readonly UISkin TestSkin = new(CascadiaFont);

    /* 1. Single-quad vs 2-quad UIDrawList subpixel emission */

    [Fact]
    public void UIDrawList_subpixel_dual_source_emits_single_ink_quad_and_legacy_emits_two_pass_erase_plus_ink()
    {
        FieldInfo? readyField = typeof(GlyphAtlas).GetField("ready", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(readyField);

        GlyphAtlas subpixelAtlas = GlyphAtlas.Subpixel;
        bool wasReady = (bool)readyField!.GetValue(subpixelAtlas)!;
        try
        {
            readyField.SetValue(subpixelAtlas, true);

            const string text = "Hello";
            var legacyList = new UIDrawList();
            legacyList.Begin(TestSkin, frame: 1, dt: 1.0f / 60.0f);
            legacyList.UseSmoothing(subpixels: true, bgr: false, hinted: true, dualSource: false);
            legacyList.Text(text, new Vector2(10.0f, 100.0f), 1.0f, Vector4.One, markup: false);
            legacyList.End();

            int legacyErase = 0, legacyInk = 0;
            foreach (ref readonly SpriteItem item in legacyList.Items)
            {
                if ((item.Flags & SpriteItem.SubpixelEraseFlag) != 0) legacyErase++;
                if ((item.Flags & SpriteItem.SubpixelInkFlag) != 0) legacyInk++;
            }

            Assert.Equal(text.Length, legacyErase);
            Assert.Equal(text.Length, legacyInk);
            Assert.Equal(text.Length * 2, legacyList.Items.Length);

            var dualList = new UIDrawList();
            dualList.Begin(TestSkin, frame: 1, dt: 1.0f / 60.0f);
            dualList.UseSmoothing(subpixels: true, bgr: false, hinted: true, dualSource: true);
            dualList.Text(text, new Vector2(10.0f, 100.0f), 1.0f, Vector4.One, markup: false);
            dualList.End();

            int dualErase = 0, dualInk = 0;
            foreach (ref readonly SpriteItem item in dualList.Items)
            {
                if ((item.Flags & SpriteItem.SubpixelEraseFlag) != 0) dualErase++;
                if ((item.Flags & SpriteItem.SubpixelInkFlag) != 0) dualInk++;
            }

            Assert.Equal(0, dualErase);
            Assert.Equal(text.Length, dualInk);
            Assert.Equal(text.Length, dualList.Items.Length);
        }
        finally
        {
            readyField.SetValue(subpixelAtlas, wasReady);
        }
    }

    /* 2. UIRenderer dual-source selection & fallback on manual SubpixelEraseFlag quads */

    [Fact]
    public void UIRenderer_selects_dual_source_when_available_and_falls_back_on_manual_SubpixelEraseFlag_quads()
    {
        using var renderer = new UIRenderer { DualSourceAvailableForTest = true };

        // Single-pass dual-source items (normal quad + SubpixelInkFlag quad, no SubpixelEraseFlag)
        SpriteItem[] dualItems =
        [
            SpriteItem.Rectangle(Vector2.Zero, new Vector2(40.0f, 20.0f), Vector2.Zero, Vector2.Zero, 0xFFFFFFFFu, SpriteItem.NoTexture),
            SpriteItem.Rectangle(new Vector2(4.0f, 4.0f), new Vector2(12.0f, 16.0f), Vector2.Zero, new Vector2(8.0f, 12.0f), 0xFFFFFFFFu, SpriteItem.SubpixelInkFlag | 3u),
        ];
        UIDrawList.Run[] dualRuns = [new UIDrawList.Run(0, dualItems.Length, null, null)];

        renderer.Upload(dualItems, dualRuns, TestSkin);
        Assert.True(renderer.UsingDualShader);
        Assert.Equal(0, renderer.BlendChangeCount);

        // Manual 2-pass SubpixelEraseFlag + SubpixelInkFlag quads must fall back to legacy 2-pass blending
        SpriteItem[] legacyItems =
        [
            SpriteItem.Rectangle(Vector2.Zero, new Vector2(40.0f, 20.0f), Vector2.Zero, Vector2.Zero, 0xFFFFFFFFu, SpriteItem.NoTexture),
            SpriteItem.Rectangle(new Vector2(4.0f, 4.0f), new Vector2(12.0f, 16.0f), Vector2.Zero, new Vector2(8.0f, 12.0f), 0xFFFFFFFFu, SpriteItem.SubpixelEraseFlag | 3u),
            SpriteItem.Rectangle(new Vector2(4.0f, 4.0f), new Vector2(12.0f, 16.0f), Vector2.Zero, new Vector2(8.0f, 12.0f), 0xFFFFFFFFu, SpriteItem.SubpixelInkFlag | 3u),
        ];
        UIDrawList.Run[] legacyRuns = [new UIDrawList.Run(0, legacyItems.Length, null, null)];

        renderer.Upload(legacyItems, legacyRuns, TestSkin);
        Assert.False(renderer.UsingDualShader);
        Assert.True(renderer.BlendChangeCount >= 2);
    }

    /* 3. PerformanceSample.Read(..., full: false) */

    [Fact]
    public void PerformanceSample_ReadTimelines_compact_skips_tick_timeline_and_worst_gap()
    {
        var sample = new PerformanceSample();
        var renderLoop = new LoopStatistics("Render", 60.0);
        var simLoop = new LoopStatistics("Simulation", 120.0);

        for (int i = 0; i < 120; i++)
        {
            renderLoop.Record(0.002, 0.0, 1.0 / 60.0);
            simLoop.Record(0.001, 0.0, 1.0 / 120.0);
        }

        FieldInfo? renderField = typeof(PerformanceSample).GetField("render", BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo? simField = typeof(PerformanceSample).GetField("simulation", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(renderField);
        Assert.NotNull(simField);

        renderField!.SetValue(sample, renderLoop);
        simField!.SetValue(sample, simLoop);

        // Compact timeline read populates FrameSlices & FrameUsualMs while skipping TickSlices, WorstGapMs, and TickUsualMs.
        sample.ReadTimelines(full: false);
        Assert.True(sample.FrameSlices > 0);
        Assert.True(sample.FrameUsualMs > 0.0f);
        Assert.Equal(0, sample.TickSlices);
        Assert.Equal(0.0f, sample.TickUsualMs);
        Assert.Equal(0.0, sample.WorstGapMs);

        // Full timeline read populates tick timeline and worst gap as well.
        sample.ReadTimelines(full: true);
        Assert.True(sample.TickSlices > 0);
        Assert.True(sample.TickUsualMs > 0.0f);
        Assert.True(sample.WorstGapMs > 0.0);
    }

    /* 4. TreeView.WithoutIcons */

    [Fact]
    public void TreeView_WithoutIcons_returns_same_instance_without_tags_and_strips_icon_markup()
    {
        const string plain = "Horizon.UI.Components (12 files)";
        string resultPlain = TreeView.WithoutIcons(plain);
        Assert.Same(plain, resultPlain);

        const string bracketsNoTag = "items[0] = $value";
        string resultNoTag = TreeView.WithoutIcons(bracketsNoTag);
        Assert.Same(bracketsNoTag, resultNoTag);

        string stripped = TreeView.WithoutIcons("$(git-branch) development [icon:check] clean");
        Assert.Equal(" development  clean", stripped);

        // Repeated calls reuse the ThreadStatic StringBuilder cleanly without leaking previous contents.
        string second = TreeView.WithoutIcons("alpha $(arrow-right) beta");
        Assert.Equal("alpha  beta", second);
    }

    /* 5. CodeEditor incremental single-line recolour buffer reuse + foldable reuse */

    [Fact]
    public void CodeEditor_single_line_recolour_reuses_buffers_and_foldable_when_brackets_unchanged()
    {
        ICodeHighlighter? cs = CodeLanguages.ForExtension(".cs");
        Assert.NotNull(cs);

        const int lineCount = 400;
        var lines = new string[lineCount];
        lines[0] = "namespace Demo;";
        lines[1] = "public sealed class Sample";
        lines[2] = "{";
        for (int i = 3; i < lineCount - 1; i++)
            lines[i] = $"    public int Prop_{i} => {i} + 1;";
        lines[lineCount - 1] = "}";

        var editor = new CodeEditor(string.Join('\n', lines))
        {
            Size = new Vector2(800.0f, 500.0f),
            Highlighter = cs,
            CheckSyntax = false,
        };

        // Initial synchronous Recolour + first background colouring pass (for > COLOUR_AT_ONCE lines)
        editor.UpdateTree(0.1f);
        WaitForColouring(editor);

        Assert.True(editor.CanFold(2));
        int initialFoldReuse = editor.FoldableReuseCount;

        // Edit #1: single-line edit with unchanged brackets -> populates spare buffers & reuses foldable
        editor.Document.MoveTo(new UITextPosition(200, editor.Document.LineAt(200).Length - 1));
        editor.Document.Type('9');
        editor.UpdateTree(0.1f);
        WaitForColouring(editor);

        Assert.True(editor.FoldableReuseCount > initialFoldReuse, "Expected foldable to be reused on single-line edit without bracket changes.");
        Assert.True(editor.CanFold(2));

        int bufferReuseBefore = editor.ColouringBufferReuseCount;
        int foldReuseBefore = editor.FoldableReuseCount;

        // Edit #2: another single-line edit at same LineCount -> must reuse the retired spare arrays AND foldable!
        editor.Document.MoveTo(new UITextPosition(201, editor.Document.LineAt(201).Length - 1));
        editor.Document.Type('7');
        editor.UpdateTree(0.1f);
        WaitForColouring(editor);

        Assert.True(editor.ColouringBufferReuseCount > bufferReuseBefore, "Expected double-buffered colouring arrays to be reused on second edit.");
        Assert.True(editor.FoldableReuseCount > foldReuseBefore, "Expected foldable to be reused on second single-line edit.");
        Assert.True(editor.CanFold(2));
    }

    /* 6. CodeEditor.SemanticSpans backing-array reuse */

    [Fact]
    public void CodeEditor_SemanticSpans_reuses_backing_arrays_when_capacity_fits()
    {
        var editor = new CodeEditor("int a = 1;\nint b = 2;\nint c = 3;")
        {
            Size = new Vector2(600.0f, 200.0f),
            CheckSyntax = false,
        };
        editor.UpdateTree(1.0f / 60.0f);

        editor.SemanticSpans =
        [
            new SemanticSpan(0, 4, 1, SyntaxKind.Local),
            new SemanticSpan(1, 4, 1, SyntaxKind.Local),
            new SemanticSpan(2, 4, 1, SyntaxKind.Local),
        ];

        FieldInfo spansField = typeof(CodeEditor).GetField("semanticSpans", BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo offsetsField = typeof(CodeEditor).GetField("semanticOffsets", BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo textsField = typeof(CodeEditor).GetField("semanticTexts", BindingFlags.Instance | BindingFlags.NonPublic)!;

        object? firstSpans = spansField.GetValue(editor);
        object? firstOffsets = offsetsField.GetValue(editor);
        object? firstTexts = textsField.GetValue(editor);

        // Refresh with <= same line count & span count: backing arrays must be reused in place.
        editor.SemanticSpans =
        [
            new SemanticSpan(0, 4, 1, SyntaxKind.Parameter),
            new SemanticSpan(2, 4, 1, SyntaxKind.Field),
        ];

        Assert.Same(firstSpans, spansField.GetValue(editor));
        Assert.Same(firstOffsets, offsetsField.GetValue(editor));
        Assert.Same(firstTexts, textsField.GetValue(editor));
        Assert.Equal(SyntaxKind.Parameter, editor.SemanticsOf(0, editor.Document.LineAt(0))[0].Kind);
        Assert.True(editor.SemanticsOf(1, editor.Document.LineAt(1)).IsEmpty);
        Assert.Equal(SyntaxKind.Field, editor.SemanticsOf(2, editor.Document.LineAt(2))[0].Kind);
    }

    /* 7. Leaf Measure cache on Label and Button */

    [Fact]
    public void Label_and_Button_Measure_cache_stays_accurate_and_invalidates_when_inputs_change()
    {
        var label = new Label("Short");
        Vector2 shortSize = label.MeasureTree(TestSkin);
        Vector2 shortAgain = label.MeasureTree(TestSkin);
        Assert.Equal(shortSize, shortAgain);

        label.Text = "A significantly longer label string";
        Vector2 longSize = label.MeasureTree(TestSkin);
        Assert.True(longSize.X > shortSize.X);

        var button = new Button("OK");
        Vector2 okSize = button.MeasureTree(TestSkin);
        Vector2 okAgain = button.MeasureTree(TestSkin);
        Assert.Equal(okSize, okAgain);

        button.Label = "Confirm and Continue";
        Vector2 confirmSize = button.MeasureTree(TestSkin);
        Assert.True(confirmSize.X > okSize.X);
    }

    private static void WaitForColouring(CodeEditor editor)
    {
        FieldInfo? colouringField = typeof(CodeEditor).GetField("colouring", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(colouringField);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            editor.UpdateTree(0.1f);
            if (colouringField!.GetValue(editor) is Task task)
                task.Wait(TimeSpan.FromSeconds(5));
            else
                return;
        }

        editor.UpdateTree(0.1f);
    }
}
