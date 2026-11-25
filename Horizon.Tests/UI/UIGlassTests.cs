using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Horizon.Engine;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.Tests;

// The glass a component sits on (UIGlass), the sums of it that need no GPU. Where a pane lands in pixels, the
// background that goes see through over it, and a pane between two ticks.
public class UIGlassTests
{
    [Fact]
    public void A_pane_lands_where_the_camera_shows_it_counted_from_the_top_left()
    {
        var camera = new Camera2D(new Vector2(800, 600));
        Vector2 size = new(800, 600);

        var pane = new UIGlassPane(0, new Vector2(0, 0), new Vector2(100, 50),
            new Vector2(float.NegativeInfinity), new Vector2(float.PositiveInfinity), 8.0f, 18.0f, 1.0f);

        var (rect, clip) = UIGlassPass.Pixels(pane, camera, size);

        var seen = camera.Bounds;
        Vector2 offset = new(seen.X + seen.Width * 0.5f, seen.Y + seen.Height * 0.5f);
        Assert.Equal(400.0f - offset.X, rect.X, 0.5f);
        Assert.Equal(500.0f - offset.X, rect.Z, 0.5f);
        Assert.Equal(250.0f + offset.Y, rect.Y, 0.5f);
        Assert.Equal(300.0f + offset.Y, rect.W, 0.5f);
        Assert.True(float.IsNegativeInfinity(clip.X) && float.IsPositiveInfinity(clip.Z));
        Assert.Equal(1.0f, UIGlassPass.PixelsPerUnit(camera, size), 0.001f);
    }

    [Fact]
    public void The_background_over_it_keeps_what_the_glass_does_not_take()
    {
        var glass = new UIGlass(Blur: 20.0f, Clarity: 0.25f);
        Vector4 bg = new(0.2f, 0.3f, 0.4f, 1.0f);
        Vector4 over = glass.Over(bg);

        Assert.Equal(0.2f, over.X, 0.001f);
        Assert.Equal(0.3f, over.Y, 0.001f);
        Assert.Equal(0.4f, over.Z, 0.001f);
        Assert.Equal(0.75f, over.W, 0.001f);
        Assert.Equal(0.0f, new UIGlass(Clarity: 1.0f).Over(Vector4.One).W, 0.001f);

        Assert.Equal(0.8f, glass.Dye, 0.001f);
        Assert.Equal(0.578f, UIGlass.Clear.Dye, 0.001f);
        Vector4 dye = UIGlass.Clear.DyeColor(bg);
        Assert.Equal(0.2f, dye.X, 0.001f);
        Assert.Equal(0.3f, dye.Y, 0.001f);
        Assert.Equal(0.4f, dye.Z, 0.001f);
        Assert.Equal(UIGlass.Clear.Dye, dye.W, 0.001f);
    }

    [Fact]
    public void Between_two_ticks_a_pane_slides_and_fades_with_its_component()
    {
        Vector2 none = new(float.NegativeInfinity), all = new(float.PositiveInfinity);
        var a = new UIGlassPane(4, new Vector2(0, 0), new Vector2(100, 100), none, all, 6.0f, 18.0f, 0.0f);
        var b = new UIGlassPane(9, new Vector2(100, 0), new Vector2(200, 100), none, all, 6.0f, 18.0f, 1.0f);

        UIGlassPane half = UIGlassPane.Blend(a, b, 0.5f);

        Assert.Equal(new Vector2(50, 0), half.Min);
        Assert.Equal(0.5f, half.Opacity, 0.001f);
        Assert.Equal(9, half.Start);
        Assert.True(float.IsNegativeInfinity(half.ClipMin.X) && float.IsPositiveInfinity(half.ClipMax.Y));
    }

    [Fact]
    public void Frosted_glass_blurs_and_lets_some_of_it_through()
    {
        UIGlass frosted = UIGlass.Frosted;

        Assert.True(frosted.Blur > 0.0f);
        Assert.InRange(frosted.Clarity, 0.01f, 0.99f);
        Assert.True(frosted.Over(Vector4.One).W < 1.0f);
    }

    [Fact]
    public void Blending_a_pane_interpolates_bend_along_with_bounds_and_opacity()
    {
        Vector2 none = new(float.NegativeInfinity), all = new(float.PositiveInfinity);
        var a = new UIGlassPane(2, new Vector2(0, 0), new Vector2(100, 60), none, all, 8.0f, 12.0f, 0.2f, Bend: 4.0f, Lens: 0.2f);
        var b = new UIGlassPane(5, new Vector2(20, 10), new Vector2(120, 70), none, all, 8.0f, 12.0f, 1.0f, Bend: 16.0f, Lens: 0.8f);

        UIGlassPane mid = UIGlassPane.Blend(a, b, 0.25f);

        Assert.Equal(new Vector2(5, 2.5f), mid.Min);
        Assert.Equal(new Vector2(105, 62.5f), mid.Max);
        Assert.Equal(0.4f, mid.Opacity, 0.001f);
        Assert.Equal(7.0f, mid.Bend, 0.001f);
        Assert.Equal(0.35f, mid.Lens, 0.001f);
        Assert.Equal(5, mid.Start);
    }

    [Fact]
    public void Glass_panes_are_tightly_packed_and_compare_deterministically_in_SameAs()
    {
        // 2 ints + 4 Vector2 + 5 float + 1 uint = 16 * 4 = 64 bytes with no padding holes for MemoryMarshal.AsBytes
        Assert.Equal(64, Unsafe.SizeOf<UIGlassPane>());

        var font = new UIFont(Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", "cascadia_mono"), "cascadia_mono.ttf");
        var skin = new UISkin(font);
        var rect = new UIRect(new Vector2(0, 0), new Vector2(180, 90));

        var a = new UIDrawList();
        var b = new UIDrawList();
        a.Begin(skin);
        a.Glass(rect, 12.0f, 8.0f, bend: 16.0f);
        a.End();

        b.Begin(skin);
        b.Glass(rect, 12.0f, 8.0f, bend: 16.0f);
        b.End();
        Assert.True(a.SameAs(b));
        Assert.True(MemoryMarshal.AsBytes(a.GlassPanes).SequenceEqual(MemoryMarshal.AsBytes(b.GlassPanes)));
        Assert.Null(a.FramesDiffer());

        b.Begin(skin);
        b.Glass(rect, 12.0f, 8.0f, bend: 8.0f);
        b.End();
        Assert.False(a.SameAs(b));
    }

    [Fact]
    public void Zero_blur_with_bend_emits_a_pane_while_zero_blur_and_zero_bend_emits_none()
    {
        var font = new UIFont(Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", "cascadia_mono"), "cascadia_mono.ttf");
        var skin = new UISkin(font);
        var rect = new UIRect(new Vector2(0, 0), new Vector2(180, 90));
        var list = new UIDrawList();

        list.Begin(skin);
        list.Glass(rect, 12.0f, 0.0f, bend: 16.0f);
        list.End();

        Assert.Equal(1, list.GlassPanes.Length);
        Assert.Equal(0.0f, list.GlassPanes[0].Blur, 0.001f);
        Assert.Equal(16.0f, list.GlassPanes[0].Bend, 0.001f);
        Assert.Equal(16.0f / UIGlass.MAX_BEND, list.GlassPanes[0].Lens, 0.001f);
        Assert.Null(list.FramesDiffer());

        list.Begin(skin);
        list.Glass(rect, 12.0f, 0.0f, bend: 0.0f);
        list.End();

        Assert.Equal(0, list.GlassPanes.Length);
    }

    [Fact]
    public void Glass_optics_scale_with_pane_size_and_pad_for_refraction_and_aberration()
    {
        float lens = 16.0f / UIGlass.MAX_BEND;
        UIGlassOptics large = UIGlassOptics.Compute(16.0f, lens, 320.0f);
        Assert.Equal(48.0f, large.BevelWidth, 0.001f);
        Assert.Equal(35.2f, large.Refraction, 0.001f);
        Assert.Equal(72.0f, large.BevelDepth, 0.001f);
        Assert.Equal(1.0f + 0.12f * lens, large.Magnify, 0.001f);
        Assert.Equal(0.25f * lens, large.Aberration, 0.001f);
        Assert.Equal(MathF.Ceiling((large.Refraction + large.BevelDepth) * (1.0f + large.Aberration)) + 2.0f + UIGlassOptics.RIM_SPREAD, large.PaddingPixels, 0.001f);

        UIGlassOptics small = UIGlassOptics.Compute(16.0f, lens, 28.0f);
        Assert.Equal(0.32f * 28.0f, small.BevelWidth, 0.001f);
        Assert.Equal(0.11f * 28.0f, small.Refraction, 0.001f);
        Assert.Equal(0.24f * 28.0f, small.BevelDepth, 0.001f);
        Assert.Equal(MathF.Ceiling((small.Refraction + small.BevelDepth) * (1.0f + small.Aberration)) + 2.0f + UIGlassOptics.RIM_SPREAD, small.PaddingPixels, 0.001f);

        UIGlassOptics flat = UIGlassOptics.Compute(0.0f, 0.0f, 320.0f);
        Assert.Equal(0.0f, flat.BevelWidth, 0.001f);
        Assert.Equal(0.0f, flat.Refraction, 0.001f);
        Assert.Equal(0.0f, flat.BevelDepth, 0.001f);
        Assert.Equal(1.0f, flat.Magnify, 0.001f);
        Assert.Equal(0.0f, flat.Aberration, 0.001f);
        Assert.Equal(2.0f, flat.PaddingPixels, 0.001f);
    }

    [Fact]
    public void Popup_excludes_lifted_shadow_from_glass_under_mark_and_resolves_cleanly()
    {
        var font = new UIFont(Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", "cascadia_mono"), "cascadia_mono.ttf");
        var skin = new UISkin(font);
        var list = new UIDrawList();

        list.Begin(skin);
        list.Rect(new UIRect(new Vector2(0, 0), new Vector2(400, 300)), Vector4.One);
        list.Popup(new UIRect(new Vector2(40, 40), new Vector2(220, 160)), UIGlass.Clear);
        list.End();

        Assert.Equal(1, list.GlassPanes.Length);
        UIGlassPane pane = list.GlassPanes[0];
        Assert.Equal(1, pane.Under);
        Assert.True(pane.Under < pane.Start, $"Expected Under ({pane.Under}) < Start ({pane.Start}) so Lifted shadow is excluded from glass capture.");
        Assert.Null(list.FramesDiffer());
    }

    private sealed class CachedGlassComponent : UIComponent
    {
        public long Version { get; set; } = 1;
        public bool DrawPopup { get; set; }

        protected override long PaintVersion => Version;

        protected override void Paint(UIDrawList list)
        {
            list.Rect(Bounds, new Vector4(0.15f, 0.2f, 0.25f, 0.8f));
            if (DrawPopup)
                list.Popup(new UIRect(Bounds.Min + new Vector2(12, 12), Bounds.Max - new Vector2(12, 12)), UIGlass.Clear);
        }
    }

    [Fact]
    public void Component_with_glass_and_popup_reuses_draw_list_and_rebases_glass_indices()
    {
        var font = new UIFont(Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", "cascadia_mono"), "cascadia_mono.ttf");
        var skin = new UISkin(font);
        var component = new CachedGlassComponent
        {
            Lift = 20.0f,
            Glass = UIGlass.Frosted,
            DrawPopup = true
        };
        component.ArrangeTree(new UIRect(new Vector2(20, 20), new Vector2(260, 180)));

        var listA = new UIDrawList();
        listA.Begin(skin);
        listA.Rect(new UIRect(new Vector2(0, 0), new Vector2(10, 10)), Vector4.One);
        component.PaintTree(listA);
        listA.End();

        Assert.Equal(0, listA.Reused);
        Assert.Equal(2, listA.GlassPanes.Length);
        Assert.True(listA.GlassPanes[0].Under < listA.GlassPanes[0].Start);
        Assert.True(listA.GlassPanes[1].Under < listA.GlassPanes[1].Start);
        Assert.Null(listA.FramesDiffer());

        var listB = new UIDrawList { Previous = listA };
        listB.Begin(skin);
        // Paint 3 leading quads instead of 1 (+2 shift) to verify Under and Start rebasing
        for (int i = 0; i < 3; i++)
            listB.Rect(new UIRect(new Vector2(i * 12, 0), new Vector2(i * 12 + 10, 10)), Vector4.One);
        component.PaintTree(listB);
        listB.End();

        Assert.Equal(1, listB.Reused);
        Assert.Equal(2, listB.GlassPanes.Length);
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(listA.GlassPanes[i].Under + 2, listB.GlassPanes[i].Under);
            Assert.Equal(listA.GlassPanes[i].Start + 2, listB.GlassPanes[i].Start);
            Assert.True(listB.GlassPanes[i].Under < listB.GlassPanes[i].Start);
        }
        Assert.Null(listB.FramesDiffer());

        bool previousCheck = UIDrawList.CheckReuse;
        try
        {
            UIDrawList.CheckReuse = true;
            var listC = new UIDrawList { Previous = listB };
            listC.Begin(skin);
            for (int i = 0; i < 5; i++)
                listC.Rect(new UIRect(new Vector2(i * 12, 0), new Vector2(i * 12 + 10, 10)), Vector4.One);
            component.PaintTree(listC);
            listC.End();

            Assert.Equal(0, listC.Reused);
            Assert.Equal(2, listC.GlassPanes.Length);
            for (int i = 0; i < 2; i++)
            {
                Assert.Equal(listB.GlassPanes[i].Under + 2, listC.GlassPanes[i].Under);
                Assert.Equal(listB.GlassPanes[i].Start + 2, listC.GlassPanes[i].Start);
            }
            Assert.Null(listC.FramesDiffer());
        }
        finally
        {
            UIDrawList.CheckReuse = previousCheck;
        }

        // Changing Glass invalidates reuse even when PaintVersion is unchanged
        component.Glass = UIGlass.Clear;
        var listD = new UIDrawList { Previous = listB };
        listD.Begin(skin);
        for (int i = 0; i < 3; i++)
            listD.Rect(new UIRect(new Vector2(i * 12, 0), new Vector2(i * 12 + 10, 10)), Vector4.One);
        component.PaintTree(listD);
        listD.End();

        Assert.Equal(0, listD.Reused);
        Assert.Equal(UIGlass.Clear.Blur, listD.GlassPanes[0].Blur, 0.001f);
        Assert.Null(listD.FramesDiffer());
    }

    [Fact]
    public void UIInBetween_caches_frame_index_map_across_interpolations_and_invalidates_on_clear()
    {
        var font = new UIFont(Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", "cascadia_mono"), "cascadia_mono.ttf");
        var skin = new UISkin(font);
        var older = new UIDrawList();
        var newer = new UIDrawList();

        static void PaintTick(UIDrawList list, UISkin skin, float shiftY)
        {
            list.Begin(skin);
            list.PushFrame(new Vector2(0, shiftY));
            list.Rect(new UIRect(new Vector2(0, 0), new Vector2(100, 40)), Vector4.One);
            list.PopFrame();
            list.End();
        }

        PaintTick(older, skin, 0.0f);
        PaintTick(newer, skin, 20.0f);

        var inBetween = new UIInBetween();
        inBetween.Frames(older.FrameList, newer.FrameList, 0.25f);
        inBetween.Frames(older.FrameList, newer.FrameList, 0.5f);
        inBetween.Frames(older.FrameList, newer.FrameList, 0.75f);
        Assert.Equal(1, inBetween.FrameMapRebuilds);

        // Clearing and repainting older increments Stamp and forces a single rebuild
        PaintTick(older, skin, 10.0f);
        inBetween.Frames(older.FrameList, newer.FrameList, 0.5f);
        Assert.Equal(2, inBetween.FrameMapRebuilds);

        inBetween.Frames(older.FrameList, newer.FrameList, 0.75f);
        Assert.Equal(2, inBetween.FrameMapRebuilds);
    }
}
