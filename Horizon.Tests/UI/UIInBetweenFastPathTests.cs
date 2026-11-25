using System.Numerics;
using System.Runtime.InteropServices;

using Horizon.Graphics;
using Horizon.Rendering.Spriting;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.Tests.UI;

/// <summary>
/// Verifies that the stationary-frame fast-copy path in <see cref="UIFrameList.Resolve"/> and
/// <see cref="UIInBetween.Frames"/> produces bit-for-bit identical output to full mathematical resolution
/// across stationary frames, moving frames, clipped frames, <see cref="UIDrawList.TryReuse"/>-rebased
/// components, and culled quads (<see cref="UIFrameQuad.Item"/> == -1).
/// </summary>
public class UIInBetweenFastPathTests
{
    private static readonly UISkin Skin = new(new UIFont(Path.GetTempPath(), "no-font-here.ttf"));

    private readonly record struct Snapshot(SpriteItem[] Items, UIFrameList Frames);

    private sealed class Swatch(Vector4 color, bool textured = false, bool rounded = false) : UIComponent
    {
        public float Height { get; init; } = 24.0f;

        protected override Vector2 Measure(UISkin skin) => new(120.0f, Height);

        protected override void Paint(UIDrawList list)
        {
            if (textured)
                list.Image(Texture.Invalid, Bounds, Vector2.Zero, new Vector2(16.0f), color);
            else if (rounded)
                list.RoundRect(Bounds, 6.0f, color);
            else
                list.Rect(Bounds, color);
        }
    }

    private sealed class VariablePrefix : UIComponent
    {
        public int ExtraQuads { get; set; }

        protected override Vector2 Measure(UISkin skin) => new(120.0f, 30.0f);

        protected override void Paint(UIDrawList list)
        {
            for (int i = 0; i < ExtraQuads; i++)
            {
                float x = Bounds.Min.X + i * 6.0f;
                list.Rect(new UIRect(new Vector2(x, Bounds.Min.Y), new Vector2(x + 4.0f, Bounds.Min.Y + 10.0f)), Colour(20 + i));
            }
        }
    }

    private sealed class CachedBlock : UIComponent
    {
        public long Version { get; set; } = 1;

        protected override long PaintVersion => Version;

        protected override Vector2 Measure(UISkin skin) => new(160.0f, 80.0f);

        protected override void Paint(UIDrawList list)
        {
            list.PushClip(Bounds);
            // Visible rect, rounded shape, textured quad, and a culled quad outside the clip.
            list.Rect(new UIRect(Bounds.Min + new Vector2(4.0f, 4.0f), Bounds.Min + new Vector2(60.0f, 30.0f)), Colour(1));
            list.RoundRect(new UIRect(Bounds.Min + new Vector2(68.0f, 4.0f), Bounds.Min + new Vector2(140.0f, 34.0f)), 5.0f, Colour(2));
            list.Image(Texture.Invalid, new UIRect(Bounds.Min + new Vector2(10.0f, 38.0f), Bounds.Min + new Vector2(90.0f, 70.0f)), Vector2.Zero, new Vector2(16.0f), Colour(3));
            // Fully clipped away so UIFrameQuad.Item == -1.
            list.Rect(new UIRect(Bounds.Max + new Vector2(50.0f, 50.0f), Bounds.Max + new Vector2(80.0f, 80.0f)), Colour(4));
            list.PopClip();
        }
    }

    private static Vector4 Colour(int i) => new((i + 1) / 64.0f, 0.45f, 0.7f, 1.0f);

    private static Snapshot Capture(UIDrawList list)
    {
        var frames = new UIFrameList();
        frames.CopyFrom(list.FrameList);
        return new Snapshot(list.Items.ToArray(), frames);
    }

    private static Snapshot PaintTree(UIComponent root, UIRect screen, int tick, UIDrawList? list = null, UIDrawList? previous = null)
    {
        list ??= new UIDrawList();
        root.MeasureTree(Skin);
        root.ArrangeTree(screen);
        list.Previous = previous;
        list.Begin(Skin, tick, 1.0f / 120.0f, tick / 120.0f);
        root.PaintTree(list);
        list.End();
        return Capture(list);
    }

    private static void AssertFastPathMatchesFull(UIFrameList older, Snapshot newer, float alpha, Vector2? pointer = null)
    {
        var full = new UIInBetween();
        var fast = new UIInBetween();

        full.Frames(older, newer.Frames, alpha, pointer);
        fast.Frames(older, newer.Frames, alpha, pointer, newer.Items);

        Assert.Equal(full.FramedQuads.Length, fast.FramedQuads.Length);
        Assert.True(
            MemoryMarshal.AsBytes(full.FramedQuads).SequenceEqual(MemoryMarshal.AsBytes(fast.FramedQuads)),
            $"Fast-copy quads diverged from full Resolve at alpha={alpha}.");

        Assert.Equal(full.FramedRuns.Length, fast.FramedRuns.Length);
        for (int i = 0; i < full.FramedRuns.Length; i++)
            Assert.Equal(full.FramedRuns[i], fast.FramedRuns[i]);

        Assert.Equal(full.FramedGlass.Length, fast.FramedGlass.Length);
        Assert.True(
            MemoryMarshal.AsBytes(full.FramedGlass).SequenceEqual(MemoryMarshal.AsBytes(fast.FramedGlass)),
            $"Fast-copy glass panes diverged from full Resolve at alpha={alpha}.");
    }

    [Fact]
    public void Stationary_and_moving_frames_match_full_resolve_bit_for_bit()
    {
        var screen = new UIRect(Vector2.Zero, new Vector2(400.0f, 200.0f));
        var root = new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8.0f };

        // Stationary sidebar with flat, rounded, textured, and glass elements.
        var sidebar = root.Add(new StackPanel { Direction = UIDirection.Vertical, Spacing = 4.0f });
        sidebar.Add(new Swatch(Colour(1)));
        sidebar.Add(new Swatch(Colour(2), rounded: true) { Glass = UIGlass.Frosted });
        sidebar.Add(new Swatch(Colour(3), textured: true));

        // Moving scroll panel alongside the stationary sidebar.
        var scroll = root.Add(new ScrollPanel { Fade = 0.0f, Size = new Vector2(200.0f, 120.0f) });
        var rows = scroll.Add(new StackPanel { Direction = UIDirection.Vertical, Spacing = 0.0f });
        for (int i = 0; i < 12; i++)
            rows.Add(new Swatch(Colour(10 + i), textured: i % 2 == 0, rounded: i % 3 == 0));

        // Animated badge changing visual offset and opacity.
        var badge = root.Add(new Swatch(Colour(30), rounded: true));

        Snapshot tick1 = PaintTree(root, screen, 1);

        scroll.Offset = 32.0f;
        badge.VisualOffset = new Vector2(12.0f, -6.0f);
        badge.Opacity = 0.45f;
        Snapshot tick2 = PaintTree(root, screen, 2);

        foreach (float alpha in new[] { 0.0f, 0.25f, 0.5f, 0.75f, 1.0f })
            AssertFastPathMatchesFull(tick1.Frames, tick2, alpha);
    }

    [Fact]
    public void Clipped_and_overscrolled_frames_match_full_resolve_bit_for_bit()
    {
        var viewport = new UIRect(Vector2.Zero, new Vector2(200.0f, 100.0f));
        var parentClip = new UIRect(new Vector2(10.0f, 5.0f), new Vector2(190.0f, 95.0f));

        Snapshot PaintScene(float overscrollStretch, float nestedScrollY)
        {
            var list = new UIDrawList();
            list.Begin(Skin, 1, 1.0f / 60.0f, 0.0f);

            // Stationary clipped region (partially trims a textured quad).
            list.PushClip(new UIRect(new Vector2(220.0f, 10.0f), new Vector2(300.0f, 80.0f)));
            list.Image(Texture.Invalid, new UIRect(new Vector2(200.0f, 0.0f), new Vector2(320.0f, 100.0f)), Vector2.Zero, new Vector2(32.0f), Colour(1));
            list.RoundRect(new UIRect(new Vector2(230.0f, 20.0f), new Vector2(290.0f, 60.0f)), 6.0f, Colour(2));
            list.PopClip();

            // Overscrolled region exercising StationaryParentFrame clips.
            list.PushClip(parentClip);
            var spring = new UIOverscroll { Stretch = overscrollStretch };
            spring.Begin(list, viewport);
            list.PushFrame(new Vector2(0.0f, nestedScrollY));
            list.Image(Texture.Invalid, new UIRect(new Vector2(-40.0f, -50.0f), new Vector2(240.0f, 150.0f)), Vector2.Zero, new Vector2(64.0f), Colour(3));
            list.PopFrame();
            UIOverscroll.End(list);
            list.PopClip();

            list.End();
            Assert.Null(list.FramesDiffer());
            return Capture(list);
        }

        Snapshot first = PaintScene(40.0f, 0.0f);
        Snapshot second = PaintScene(20.0f, 18.0f);

        foreach (float alpha in new[] { 0.0f, 0.25f, 0.5f, 0.75f, 1.0f })
            AssertFastPathMatchesFull(first.Frames, second, alpha);
    }

    [Fact]
    public void Culled_quads_record_negative_item_index_and_uncull_when_frame_moves()
    {
        var clipRect = new UIRect(Vector2.Zero, new Vector2(100.0f, 100.0f));

        Snapshot PaintAt(float shiftY)
        {
            var list = new UIDrawList();
            list.Begin(Skin, 1, 1.0f / 60.0f, 0.0f);

            // Stationary clip with one visible quad and one culled quad.
            list.PushClip(clipRect);
            list.Rect(new UIRect(new Vector2(10.0f, 10.0f), new Vector2(40.0f, 40.0f)), Colour(1));
            list.Rect(new UIRect(new Vector2(200.0f, 200.0f), new Vector2(230.0f, 230.0f)), Colour(2));
            list.RoundRect(new UIRect(new Vector2(250.0f, 250.0f), new Vector2(280.0f, 280.0f)), 5.0f, Colour(3));

            // Moving frame whose quad starts visible at shiftY=0 and is culled at shiftY=120.
            list.PushFrame(new Vector2(0.0f, shiftY));
            list.Rect(new UIRect(new Vector2(10.0f, 10.0f + shiftY), new Vector2(50.0f, 50.0f + shiftY)), Colour(4));
            list.PopFrame();

            list.PopClip();
            list.End();
            Assert.Null(list.FramesDiffer());
            return Capture(list);
        }

        Snapshot visibleTick = PaintAt(0.0f);
        Snapshot culledTick = PaintAt(120.0f);

        // Verify culled quads recorded Item == -1 while visible quads recorded valid indices.
        ReadOnlySpan<UIFrameQuad> quads = culledTick.Frames.Quads.AsSpan(0, culledTick.Frames.QuadCount);
        Assert.Equal(4, quads.Length);
        Assert.True(quads[0].Item >= 0);
        Assert.Equal(-1, quads[1].Item);
        Assert.Equal(-1, quads[2].Item);
        Assert.Equal(-1, quads[3].Item);

        // Interpolating from visibleTick -> culledTick brings the moving quad back into view even though
        // it was culled in culledTick, while the stationary culled quads remain skipped.
        foreach (float alpha in new[] { 0.0f, 0.25f, 0.5f, 0.75f, 1.0f })
            AssertFastPathMatchesFull(visibleTick.Frames, culledTick, alpha);
    }

    [Fact]
    public void Reused_components_rebase_frame_quad_item_indices_and_match_full_resolve()
    {
        var screen = new UIRect(Vector2.Zero, new Vector2(400.0f, 200.0f));
        var root = new StackPanel { Direction = UIDirection.Vertical, Spacing = 4.0f };
        var prefix = root.Add(new VariablePrefix { ExtraQuads = 1 });
        var cached = root.Add(new CachedBlock());
        var moving = root.Add(new Swatch(Colour(40)));

        var a = new UIDrawList();
        var b = new UIDrawList();

        // Tick 1: record cached block with 1 prefix quad.
        PaintTree(root, screen, 1, a, null);

        // Tick 2: add more prefix quads so Reuse must rebase quad.Item by a non-zero itemDelta.
        prefix.ExtraQuads = 7;
        Snapshot tick2 = PaintTree(root, screen, 2, b, a);
        Assert.True(b.Reused > 0, "CachedBlock should have been reused on tick 2.");
        Assert.Null(b.FramesDiffer());

        // Tick 3: keep prefix shifted differently and move sibling frame so tick2 -> tick3 blends.
        prefix.ExtraQuads = 3;
        moving.VisualOffset = new Vector2(16.0f, 4.0f);
        Snapshot tick3 = PaintTree(root, screen, 3, a, b);
        Assert.True(a.Reused > 0, "CachedBlock should have been reused on tick 3.");
        Assert.Null(a.FramesDiffer());

        // Verify all non-culled frame quads point to valid indices in tick3.Items.
        ReadOnlySpan<UIFrameQuad> quads = tick3.Frames.Quads.AsSpan(0, tick3.Frames.QuadCount);
        bool sawCulled = false;
        for (int i = 0; i < quads.Length; i++)
        {
            if (quads[i].Item < 0)
                sawCulled = true;
            else
                Assert.InRange(quads[i].Item, 0, tick3.Items.Length - 1);
        }
        Assert.True(sawCulled, "CachedBlock should preserve its culled quad (Item == -1) across Reuse.");

        foreach (float alpha in new[] { 0.0f, 0.25f, 0.5f, 0.75f, 1.0f })
            AssertFastPathMatchesFull(tick2.Frames, tick3, alpha);
    }

    [Fact]
    public void Stationary_quads_copy_directly_from_painted_items_while_moving_quads_recompute()
    {
        var list = new UIDrawList();
        list.Begin(Skin, 1, 1.0f / 60.0f, 0.0f);
        // Frame 0 (stationary)
        list.Rect(new UIRect(new Vector2(0.0f, 0.0f), new Vector2(50.0f, 50.0f)), Colour(1));
        // Frame 1 (will be moved)
        list.PushFrame(Vector2.Zero);
        list.Rect(new UIRect(new Vector2(60.0f, 0.0f), new Vector2(110.0f, 50.0f)), Colour(2));
        list.PopFrame();
        list.End();

        SpriteItem[] sentinelPainted = list.Items.ToArray();
        const uint sentinelColor = 0xDEADBEEF;
        sentinelPainted[0].Color = sentinelColor;
        sentinelPainted[1].Color = sentinelColor;

        // Move only frame 1, leaving frame 0 stationary.
        UIFrame[] movedFrames = list.FrameList.Span.ToArray();
        movedFrames[1] = movedFrames[1] with { Shift = new Vector2(10.0f, 0.0f) };

        SpriteItem[] output = [];
        var runs = new List<UIDrawList.Run>();
        var glass = new List<UIGlassPane>();
        int count = list.FrameList.Resolve(movedFrames, ref output, runs, glass, sentinelPainted);

        Assert.Equal(2, count);
        // Stationary quad copied directly from paintedItems.
        Assert.Equal(sentinelColor, output[0].Color);
        // Moving quad recomputed from UIFrameQuad instead of copying paintedItems.
        Assert.NotEqual(sentinelColor, output[1].Color);
        Assert.Equal(new Vector2(70.0f, 0.0f), output[1].Origin);
    }
}
