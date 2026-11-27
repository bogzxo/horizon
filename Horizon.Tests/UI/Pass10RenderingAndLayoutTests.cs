using System.Numerics;
using System.Reflection;

using Horizon.Engine;
using Horizon.Graphics;
using Horizon.Rendering;
using Horizon.Rendering.Spriting;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.Tests.UI;

/// <summary>
/// Regression tests for Pass 10 optimizations: unified <c>ui_dual</c> single-draw submission,
/// <c>glyphGammaCurve</c> accuracy (&lt; 0.5/255 vs <see cref="MathF.Pow(float, float)"/>),
/// <see cref="UIRenderer"/> common-prefix glass backdrop stability across foreground drags,
/// invisible subtree layout skip/resume, and <see cref="CodeEditor"/> minimap incremental scroll reuse.
/// </summary>
public sealed class Pass10RenderingAndLayoutTests
{
    private sealed class CountingControl(Vector2 size) : UIComponent
    {
        public int MeasureCount { get; private set; }
        public int ArrangeCount { get; private set; }

        protected override Vector2 Measure(UISkin skin)
        {
            MeasureCount++;
            return size;
        }

        protected override void Arrange(UIRect content)
        {
            ArrangeCount++;
            base.Arrange(content);
        }
    }

    private static UISkin CreateSkin() =>
        new(new UIFont(Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", "cascadia_mono"), "cascadia_mono.ttf"));

    [Fact]
    public void UIRenderer_unifies_dual_source_quads_and_tracks_common_prefix_for_glass_drags()
    {
        using var renderer = new UIRenderer { DualSourceAvailableForTest = true };
        var skin = CreateSkin();

        SpriteItem[] frame1 =
        [
            SpriteItem.Rectangle(new Vector2(0, 0), new Vector2(100, 100), Vector2.Zero, Vector2.Zero, 0xFFFFFFFFu, SpriteItem.NoTexture),
            SpriteItem.Rectangle(new Vector2(10, 10), new Vector2(30, 30), Vector2.Zero, Vector2.Zero, 0xFFFFFFFFu, SpriteItem.SubpixelInkFlag | 3u),
            SpriteItem.Rectangle(new Vector2(200, 200), new Vector2(280, 260), Vector2.Zero, Vector2.Zero, 0xFF8888FFu, SpriteItem.NoTexture),
        ];
        UIDrawList.Run[] runs = [new UIDrawList.Run(0, frame1.Length, null, null)];
        UIGlassPane[] glass = [new UIGlassPane(Start: 2, Min: new Vector2(200, 200), Max: new Vector2(280, 260), ClipMin: new Vector2(-1e6f), ClipMax: new Vector2(1e6f), Radius: 8, Blur: 12, Opacity: 1, Bend: 4, Under: 2)];

        renderer.Upload(frame1, runs, skin, default, glass);
        Assert.True(renderer.UsingDualShader);
        Assert.Equal(0, renderer.BlendChangeCount);

        ulong rev1 = renderer.Revision;
        renderer.AcknowledgeGlassBaseline(rev1);

        // Dragging foreground glass quad (>= Under=2) must keep prefix [0..2) unchanged relative to rev1.
        SpriteItem[] frame2 =
        [
            frame1[0],
            frame1[1],
            SpriteItem.Rectangle(new Vector2(240, 220), new Vector2(320, 280), Vector2.Zero, Vector2.Zero, 0xFF8888FFu, SpriteItem.NoTexture),
        ];
        renderer.Upload(frame2, runs, skin, default, glass);
        Assert.True(renderer.FirstChangedItemSince(rev1) >= 2);

        ulong rev2 = renderer.Revision;
        renderer.AcknowledgeGlassBaseline(rev2);

        // Mutating backdrop quad 0 (< Under=2) must drop FirstChangedItemSince(rev2) to 0.
        SpriteItem[] frame3 =
        [
            SpriteItem.Rectangle(new Vector2(0, 0), new Vector2(100, 100), Vector2.Zero, Vector2.Zero, 0xFF112233u, SpriteItem.NoTexture),
            frame2[1],
            frame2[2],
        ];
        renderer.Upload(frame3, runs, skin, default, glass);
        Assert.Equal(0, renderer.FirstChangedItemSince(rev2));
    }

    [Fact]
    public void GlyphGammaCurve_matches_pow_within_half_lsb_across_ui_gamma_range()
    {
        float maxError = 0.0f;
        for (int gi = 80; gi <= 115; gi++)
        {
            float gamma = gi / 100.0f;
            float d = gamma - 1.0f;
            for (int xi = 0; xi <= 1000; xi++)
            {
                float x = xi / 1000.0f;
                float q = MathF.Sqrt(MathF.Sqrt(x));
                float om = 1.0f - q;
                float f1 = om * (((-1.517853f * q - 3.101536f) * q + 0.685517f) * q - 0.070777f) * q;
                float f2 = om * (((-8.480083f * q + 8.826777f) * q - 0.337532f) * q - 0.016811f) * q;
                float f3 = om * (((-7.657008f * q + 17.694711f) * q - 11.206025f) * q + 1.248355f) * q;
                float approx = Math.Clamp(x + d * (f1 + d * (f2 + d * f3)), 0.0f, 1.0f);
                float exact = MathF.Pow(x, gamma);
                maxError = MathF.Max(maxError, MathF.Abs(exact - approx));
            }
        }

        Assert.True(maxError < 0.5f / 255.0f, $"Expected max gamma error < 0.5/255, got {maxError * 255.0f:F3}/255");
    }

    [Fact]
    public void Hidden_subtree_skips_measure_and_arrange_until_made_visible_and_ScrollPanel_skips_hidden_arrange()
    {
        var skin = CreateSkin();
        var scroll = new ScrollPanel { Size = new Vector2(400.0f, 300.0f) };
        var hiddenGroup = new Panel { Visible = false };
        var hiddenChild = hiddenGroup.Add(new CountingControl(new Vector2(120.0f, 40.0f)));
        var visibleChild = scroll.Add(new CountingControl(new Vector2(120.0f, 40.0f)));
        scroll.Add(hiddenGroup);

        var stack = new StackPanel { Size = new Vector2(200.0f, 200.0f) };
        var stackHidden = stack.Add(new CountingControl(new Vector2(80.0f, 20.0f)) { Visible = false });

        // Plain Panel root (like UIModule.Root) keeps measuring/arranging hidden overlay children (PerformanceFace.Card contract).
        var root = new Panel { Size = new Vector2(400.0f, 300.0f) };
        var overlayCard = root.Add(new CountingControl(new Vector2(150.0f, 60.0f)) { Visible = false });

        scroll.MeasureTree(skin);
        scroll.ArrangeTree(new UIRect(Vector2.Zero, new Vector2(400.0f, 300.0f)));
        stack.MeasureTree(skin);
        stack.ArrangeTree(new UIRect(Vector2.Zero, new Vector2(200.0f, 200.0f)));
        root.MeasureTree(skin);
        root.ArrangeTree(new UIRect(Vector2.Zero, new Vector2(400.0f, 300.0f)));

        Assert.Equal(1, visibleChild.MeasureCount);
        Assert.Equal(1, visibleChild.ArrangeCount);
        Assert.Equal(0, hiddenChild.MeasureCount);
        Assert.Equal(0, hiddenChild.ArrangeCount);
        Assert.Equal(0, stackHidden.MeasureCount);
        Assert.Equal(0, stackHidden.ArrangeCount);
        Assert.Equal(1, overlayCard.MeasureCount);
        Assert.Equal(1, overlayCard.ArrangeCount);

        hiddenGroup.Visible = true;
        stackHidden.Visible = true;
        scroll.MeasureTree(skin);
        scroll.ArrangeTree(new UIRect(Vector2.Zero, new Vector2(400.0f, 300.0f)));
        stack.MeasureTree(skin);
        stack.ArrangeTree(new UIRect(Vector2.Zero, new Vector2(200.0f, 200.0f)));

        Assert.Equal(1, hiddenChild.MeasureCount);
        Assert.Equal(1, hiddenChild.ArrangeCount);
        Assert.Equal(1, stackHidden.MeasureCount);
        Assert.Equal(1, stackHidden.ArrangeCount);
    }

    [Fact]
    public void CodeEditor_minimap_reuses_overlapping_rows_incrementally_on_scroll_and_invalidates_on_edit()
    {
        var skin = CreateSkin();
        string text = string.Join('\n', Enumerable.Range(0, 400).Select(i => $"let line_{i} = {i}; // item"));
        var editor = new CodeEditor(text)
        {
            Size = new Vector2(900.0f, 400.0f),
            Minimap = true,
            Highlight = false
        };
        var root = new Panel { Size = new Vector2(900.0f, 400.0f) };
        root.Add(editor);

        root.MeasureTree(skin);
        root.ArrangeTree(new UIRect(Vector2.Zero, new Vector2(900.0f, 400.0f)));
        editor.UpdateTree(1.0f / 60.0f);

        MethodInfo? paint = typeof(UIComponent).GetMethod("Paint", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(paint);

        var list = new UIDrawList();
        void DrawFrame()
        {
            list.Begin(skin, frame: 1, dt: 1.0f / 60.0f);
            paint!.Invoke(editor, [list]);
            list.End();
        }

        DrawFrame();
        int missesAfterFirst = editor.MinimapCacheMisses;
        Assert.True(missesAfterFirst >= 1);

        // Steady frame hits full cache cleanly.
        DrawFrame();
        Assert.True(editor.MinimapCacheHits >= 1);

        // Scrolling down a few rows shifts overlapping rows incrementally without a full rebuild.
        editor.ScrollPixels += 240.0f;
        editor.UpdateTree(1.0f / 60.0f);
        int incrementalBefore = editor.MinimapIncrementalHits;
        DrawFrame();
        Assert.True(editor.MinimapIncrementalHits > incrementalBefore);

        // Document edits still invalidate the minimap cache rather than hitting stale runs.
        int missesBeforeEdit = editor.MinimapCacheMisses;
        Assert.True(editor.Document.ApplyEdits([new UITextEdit(default, default, "// edited\n")]));
        editor.UpdateTree(1.0f / 60.0f);
        DrawFrame();
        Assert.True(editor.MinimapCacheMisses > missesBeforeEdit);
    }

    [Fact]
    public void TexelRegion_stores_region_and_offset()
    {
        var region = new TexelRegion(12, 34, 16, 20, 1024);
        Assert.Equal(12, region.X);
        Assert.Equal(34, region.Y);
        Assert.Equal(16u, region.Width);
        Assert.Equal(20u, region.Height);
        Assert.Equal(1024, region.DataOffset);
    }
}
