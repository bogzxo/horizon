using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Horizon.Graphics;
using Horizon.Rendering.Spriting;
using Horizon.Rendering.Text;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.Tests.UI;

/// <summary>
/// Headless unit tests for Pass 3 Drawing and Skinning fixes and optimizations: multi-run and sibling-isolated
/// <see cref="UIDrawList"/> reuse, <see cref="UIDrawList.ScreenShape"/> / <see cref="UIDrawList.RoundRect"/> clamping,
/// <see cref="UIRenderer"/> disposal, <see cref="UIGlass"/> backdrop generation invalidation, <see cref="UIFont"/>
/// ASCII fast-path parity, <see cref="UITooltip"/> LRU cache, <see cref="UIInBetween"/> stationary bypass,
/// <see cref="UISkin.TryGetRegion"/> negative caching, and IL zero-closure checks on modified hot methods.
/// </summary>
public class Pass3DrawingAndGlassTests
{
    private const BindingFlags ALL_MEMBERS =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private const byte IL_NEWOBJ = 0x73;

    private static readonly UIFont CascadiaFont =
        new(Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", "cascadia_mono"), "cascadia_mono.ttf");
    private static readonly UISkin TestSkin = new(CascadiaFont);

    private static Texture MakeDummyTexture() =>
        (Texture)RuntimeHelpers.GetUninitializedObject(typeof(Texture));

    private sealed class CustomPainterComponent : UIComponent
    {
        private readonly Action<UIDrawList, UIRect> paint;

        public CustomPainterComponent(Action<UIDrawList, UIRect> paint) => this.paint = paint;

        public long Version { get; set; } = 1;

        protected override long PaintVersion => Version;

        protected override void Paint(UIDrawList list) => paint(list, Bounds);
    }

    [Fact]
    public void UIDrawList_Reuse_does_not_inherit_previous_sibling_run_images()
    {
        Texture siblingTexA = MakeDummyTexture();
        Texture siblingTexB = MakeDummyTexture();
        Texture otherTexC = MakeDummyTexture();
        Texture otherTexD = MakeDummyTexture();

        var plain = new CustomPainterComponent((list, rect) =>
        {
            list.Rect(rect, new Vector4(0.2f, 0.5f, 0.8f, 1.0f));
        });
        plain.ArrangeTree(new UIRect(new Vector2(20, 20), new Vector2(120, 60)));

        // Tick 1: sibling binds two custom images right before the plain cached component paints.
        var listA = new UIDrawList();
        listA.Begin(TestSkin, 1);
        listA.Image(siblingTexA, new UIRect(new Vector2(0, 0), new Vector2(10, 10)), Vector2.Zero, new Vector2(16, 16), Vector4.One);
        listA.Image(siblingTexB, new UIRect(new Vector2(10, 0), new Vector2(20, 10)), Vector2.Zero, new Vector2(16, 16), Vector4.One);
        plain.PaintTree(listA);
        listA.End();
        Assert.Equal(0, listA.Reused);
        Assert.Null(listA.FramesDiffer());

        // Tick 2: sibling now binds two completely different images; reusing the plain component must NOT bind
        // siblingTexA/siblingTexB or split the run.
        var listB = new UIDrawList { Previous = listA };
        listB.Begin(TestSkin, 2);
        listB.Image(otherTexC, new UIRect(new Vector2(0, 0), new Vector2(10, 10)), Vector2.Zero, new Vector2(16, 16), Vector4.One);
        listB.Image(otherTexD, new UIRect(new Vector2(10, 0), new Vector2(20, 10)), Vector2.Zero, new Vector2(16, 16), Vector4.One);
        plain.PaintTree(listB);
        listB.End();

        Assert.Equal(1, listB.Reused);
        Assert.Equal(1, listB.Runs.Length);
        Assert.Same(otherTexC, listB.Runs[0].Image0);
        Assert.Same(otherTexD, listB.Runs[0].Image1);
        Assert.Null(listB.FramesDiffer());

        // Tick 3: no sibling images at all; reusing the plain component must keep run images null.
        var listC = new UIDrawList { Previous = listB };
        listC.Begin(TestSkin, 3);
        plain.PaintTree(listC);
        listC.End();

        Assert.Equal(1, listC.Reused);
        Assert.Equal(1, listC.Runs.Length);
        Assert.Null(listC.Runs[0].Image0);
        Assert.Null(listC.Runs[0].Image1);
        Assert.Null(listC.FramesDiffer());
    }

    [Fact]
    public void UIDrawList_Reuse_preserves_multi_run_boundaries_and_remaps_slots_identically_to_fresh_paint()
    {
        Texture tex1 = MakeDummyTexture();
        Texture tex2 = MakeDummyTexture();
        Texture tex3 = MakeDummyTexture();
        Texture tex4 = MakeDummyTexture();
        Texture prefixTex = MakeDummyTexture();

        // Component paints 4 distinct textures, forcing a mid-component run split (2 images per run).
        var multi = new CustomPainterComponent((list, rect) =>
        {
            list.Image(tex1, new UIRect(rect.Min, rect.Min + new Vector2(20, 20)), Vector2.Zero, new Vector2(16, 16), new Vector4(1, 0, 0, 1));
            list.Image(tex2, new UIRect(rect.Min + new Vector2(24, 0), rect.Min + new Vector2(44, 20)), Vector2.Zero, new Vector2(16, 16), new Vector4(0, 1, 0, 1));
            list.Image(tex3, new UIRect(rect.Min + new Vector2(48, 0), rect.Min + new Vector2(68, 20)), Vector2.Zero, new Vector2(16, 16), new Vector4(0, 0, 1, 1));
            list.Image(tex4, new UIRect(rect.Min + new Vector2(72, 0), rect.Min + new Vector2(92, 20)), Vector2.Zero, new Vector2(16, 16), new Vector4(1, 1, 0, 1));
        });
        multi.ArrangeTree(new UIRect(new Vector2(10, 10), new Vector2(160, 40)));

        // Tick 1: record the multi-run component with no leading textures.
        var listA = new UIDrawList();
        listA.Begin(TestSkin, 1);
        multi.PaintTree(listA);
        listA.End();
        Assert.Equal(0, listA.Reused);
        Assert.Equal(2, listA.Runs.Length);
        Assert.Null(listA.FramesDiffer());

        // Tick 2: replay after a prefix texture that occupies slot 0, forcing a run split before tex1+tex2.
        var listB = new UIDrawList { Previous = listA };
        listB.Begin(TestSkin, 2);
        listB.Image(prefixTex, new UIRect(new Vector2(0, 0), new Vector2(8, 8)), Vector2.Zero, new Vector2(8, 8), Vector4.One);
        multi.PaintTree(listB);
        listB.End();

        Assert.Equal(1, listB.Reused);
        Assert.Null(listB.FramesDiffer());

        // Verify every quad's resolved texture matches the exact texture painted by the component.
        Assert.Equal(5, listB.Items.Length);
        Texture?[] resolvedTextures = ResolveQuadTextures(listB);
        Assert.Same(prefixTex, resolvedTextures[0]);
        Assert.Same(tex1, resolvedTextures[1]);
        Assert.Same(tex2, resolvedTextures[2]);
        Assert.Same(tex3, resolvedTextures[3]);
        Assert.Same(tex4, resolvedTextures[4]);

        // Tick 3: replay again from listB (chained reuse) and verify identical textures and frame resolution.
        var listC = new UIDrawList { Previous = listB };
        listC.Begin(TestSkin, 3);
        multi.PaintTree(listC);
        listC.End();

        Assert.Equal(1, listC.Reused);
        Assert.Null(listC.FramesDiffer());
        Assert.True(listC.SameAs(listA));
    }

    private static Texture?[] ResolveQuadTextures(UIDrawList list)
    {
        var result = new Texture?[list.Items.Length];
        foreach (UIDrawList.Run run in list.Runs)
        {
            for (int i = run.First; i < run.First + run.Count; i++)
            {
                uint slot = SpriteItem.TextureOf(list.Items[i].Flags);
                result[i] = slot == 4 ? run.Image0 : slot == 5 ? run.Image1 : null;
            }
        }
        return result;
    }

    [Fact]
    public void UIFont_ASCII_fast_path_matches_full_dictionary_resolution_and_kerning()
    {
        for (int c = 0; c < 256; c++)
        {
            char ch = (char)c;
            bool fastDrawable = CascadiaFont.Resolve(ch, out CharDefinition fastGlyph);
            bool slowDrawable = CascadiaFont.ResolveDictionary(ch, out CharDefinition slowGlyph);

            Assert.Equal(slowDrawable, fastDrawable);
            Assert.Equal(slowGlyph.Id, fastGlyph.Id);
            Assert.Equal(slowGlyph.XAdvance, fastGlyph.XAdvance);
            Assert.Equal(slowGlyph.Size, fastGlyph.Size);
            Assert.Equal(slowGlyph.Position, fastGlyph.Position);
            Assert.Equal(slowGlyph.TexelSize, fastGlyph.TexelSize);
            Assert.Equal(slowGlyph.Offset, fastGlyph.Offset);
        }

        // Plain Measure with markup=true (no '$'/'[' tags) must match markup=false identically.
        const string sample = "The quick brown fox jumps over 123 lazy dogs!\nSecond line: 0xDEADBEEF";
        Vector2 withMarkup = CascadiaFont.Measure(sample, 0.5f, markup: true);
        Vector2 withoutMarkup = CascadiaFont.Measure(sample, 0.5f, markup: false);
        Assert.Equal(withoutMarkup, withMarkup);

        // Kerning fast path matches Field.Kerning dictionary for ASCII pairs.
        for (char a = 'A'; a <= 'Z'; a++)
        {
            for (char b = 'a'; b <= 'z'; b++)
            {
                float expected = CascadiaFont.Field.Kerning.TryGetValue(DistanceFieldFont.KerningKey(a, b), out float k) ? k : 0.0f;
                Assert.Equal(expected, CascadiaFont.Kerning(a, b));
            }
        }
    }

    [Fact]
    public void UISkin_TryGetRegion_caches_missing_keys_after_first_lookup()
    {
        var skin = new UISkin(CascadiaFont);
        int before = skin.RegionLookupMissComputes;

        for (int i = 0; i < 10; i++)
        {
            Assert.False(skin.TryGetRegion("nonexistent_optional_region_key", out UIRegion region));
            Assert.Equal(default, region);
        }

        Assert.Equal(before + 1, skin.RegionLookupMissComputes);

        // Distinct missing key computes once and caches its own miss sentinel.
        Assert.False(skin.TryGetRegion("another_missing_region_key", out _));
        Assert.False(skin.TryGetRegion("another_missing_region_key", out _));
        Assert.Equal(before + 2, skin.RegionLookupMissComputes);
    }

    [Fact]
    public void ScreenShape_and_RoundRect_clamp_corners_and_borders_under_HiDPI_and_non_uniform_scaling()
    {
        var rect = new UIRect(new Vector2(10, 20), new Vector2(110, 60)); // 100 x 40
        var unclipped = new UIRect(new Vector2(float.NegativeInfinity), new Vector2(float.PositiveInfinity));
        uint white = SpriteItem.PackColor(Vector4.One);

        // Uniform 2x HiDPI: screen box is 200 x 80, radius 20 -> 40 (half of shorter side 80).
        SpriteItem hidpi = UIDrawList.ScreenShape(rect, unclipped, new Vector2(2.0f, 0.0f), 20.0f, white, Vector2.One, Vector2.Zero, new Vector2(2.0f, 2.0f), Vector2.Zero);
        Assert.Equal(200.0f, hidpi.AxisX.X, 0.01f);
        Assert.Equal(80.0f, hidpi.AxisY.Y, 0.01f);
        Assert.Equal(40.0f, hidpi.Ring, 0.01f);
        Assert.Equal(4.0f, hidpi.TexMin.X, 0.01f);

        // Non-uniform stretch (2.0, 0.5): screen box is 200 x 20, max corner radius MUST clamp to 10 (half of 20).
        SpriteItem stretched = UIDrawList.ScreenShape(rect, unclipped, new Vector2(30.0f, 0.0f), 20.0f, white, Vector2.One, Vector2.Zero, new Vector2(2.0f, 0.5f), Vector2.Zero);
        Assert.Equal(200.0f, stretched.AxisX.X, 0.01f);
        Assert.Equal(20.0f, stretched.AxisY.Y, 0.01f);
        Assert.InRange(stretched.Ring, 0.0f, 10.0f);
        Assert.InRange(stretched.TexMin.X, 0.0f, 10.0f);

        // Negative scale on X must still produce non-inverted quad axes and non-inverted clip bounds.
        var clip = new UIRect(new Vector2(0, 0), new Vector2(120, 80));
        SpriteItem flipped = UIDrawList.ScreenShape(rect, clip, Vector2.Zero, 15.0f, white, Vector2.One, Vector2.Zero, new Vector2(-2.0f, 1.0f), Vector2.Zero);
        Assert.True(flipped.AxisX.X >= 0.0f);
        Assert.True(flipped.AxisY.Y >= 0.0f);
        Assert.True(flipped.Key.X <= flipped.TexMax.X);
        Assert.True(flipped.Key.Y <= flipped.TexMax.Y);

        // RoundOutline with zero/negative thickness emits nothing instead of a solid fill.
        var list = new UIDrawList();
        list.Begin(TestSkin, 1);
        list.RoundOutline(rect, 8.0f, 0.0f, Vector4.One);
        list.RoundOutline(rect, 8.0f, -2.0f, Vector4.One);
        list.End();
        Assert.Equal(0, list.Items.Length);
    }

    [Fact]
    public void UIGlass_MixBackdropGenerations_invalidates_on_skin_or_glyph_atlas_changes()
    {
        Assert.Equal(0UL, UIGlass.MixBackdropGenerations(0UL, 1, 0, 0));

        const ulong baseHash = 0x123456789ABCDEF1UL;
        ulong h0 = UIGlass.MixBackdropGenerations(baseHash, skinAtlasGeneration: 1, sharedGlyphGeneration: 1, subpixelGlyphGeneration: 1);
        ulong hSame = UIGlass.MixBackdropGenerations(baseHash, skinAtlasGeneration: 1, sharedGlyphGeneration: 1, subpixelGlyphGeneration: 1);
        ulong hSkinBump = UIGlass.MixBackdropGenerations(baseHash, skinAtlasGeneration: 2, sharedGlyphGeneration: 1, subpixelGlyphGeneration: 1);
        ulong hGlyphBump = UIGlass.MixBackdropGenerations(baseHash, skinAtlasGeneration: 1, sharedGlyphGeneration: 2, subpixelGlyphGeneration: 1);
        ulong hSubpixelBump = UIGlass.MixBackdropGenerations(baseHash, skinAtlasGeneration: 1, sharedGlyphGeneration: 1, subpixelGlyphGeneration: 2);

        Assert.NotEqual(0UL, h0);
        Assert.Equal(h0, hSame);
        Assert.NotEqual(h0, hSkinBump);
        Assert.NotEqual(h0, hGlyphBump);
        Assert.NotEqual(h0, hSubpixelBump);
    }

    [Fact]
    public void UITooltip_eight_entry_LRU_cache_avoids_thrashing_across_alternating_items()
    {
        UITooltip.ResetCache();
        var lines = new List<(int Start, int Length)>();
        var sizes = new Vector2[8];

        // Fill all 8 slots.
        for (int i = 0; i < 8; i++)
            sizes[i] = UITooltip.ResolvePlainLayout(TestSkin, $"Tooltip item {i} with enough words to wrap cleanly across lines.", 0.4f, 400.0f, lines);
        Assert.Equal(8, UITooltip.CacheMisses);
        Assert.Equal(0, UITooltip.CacheHits);

        // Alternating across all 8 entries must hit every single time.
        for (int i = 0; i < 8; i++)
        {
            Vector2 cached = UITooltip.ResolvePlainLayout(TestSkin, $"Tooltip item {i} with enough words to wrap cleanly across lines.", 0.4f, 400.0f, lines);
            Assert.Equal(sizes[i], cached);
            Assert.True(lines.Count > 0);
        }
        Assert.Equal(8, UITooltip.CacheMisses);
        Assert.Equal(8, UITooltip.CacheHits);

        // A 9th distinct tooltip evicts the LRU entry (item 0) while items 1..7 remain cached.
        UITooltip.ResolvePlainLayout(TestSkin, "Tooltip item 8 evicts the oldest slot in the cache.", 0.4f, 400.0f, lines);
        Assert.Equal(9, UITooltip.CacheMisses);

        UITooltip.ResolvePlainLayout(TestSkin, "Tooltip item 7 with enough words to wrap cleanly across lines.", 0.4f, 400.0f, lines);
        Assert.Equal(9, UITooltip.CacheHits);
    }

    [Fact]
    public void UIInBetween_bypasses_per_quad_resolution_on_stationary_frames_with_identical_output()
    {
        var listA = new UIDrawList();
        listA.Begin(TestSkin, 1);
        listA.Rect(new UIRect(new Vector2(10, 10), new Vector2(110, 40)), new Vector4(0.2f, 0.3f, 0.4f, 1.0f));
        listA.End();

        var listB = new UIDrawList();
        listB.Begin(TestSkin, 2);
        listB.Rect(new UIRect(new Vector2(10, 10), new Vector2(110, 40)), new Vector4(0.8f, 0.3f, 0.4f, 1.0f));
        listB.Rect(new UIRect(new Vector2(120, 10), new Vector2(180, 40)), new Vector4(0.1f, 0.9f, 0.2f, 1.0f));
        listB.End();

        var inBetween = new UIInBetween();
        inBetween.Frames(listA.FrameList, listB.FrameList, 0.5f, paintedItems: listB.Items);

        Assert.Equal(1, inBetween.StationaryBypasses);
        Assert.True(MemoryMarshal.AsBytes(listB.Items).SequenceEqual(MemoryMarshal.AsBytes(inBetween.FramedQuads)));
        Assert.Equal(listB.Runs.Length, inBetween.FramedRuns.Length);
        Assert.Equal(listB.Runs[0], inBetween.FramedRuns[0]);

        // alpha >= 1.0f also takes the stationary bypass directly.
        inBetween.Frames(listA.FrameList, listB.FrameList, 1.0f, paintedItems: listB.Items);
        Assert.Equal(2, inBetween.StationaryBypasses);
        Assert.True(MemoryMarshal.AsBytes(listB.Items).SequenceEqual(MemoryMarshal.AsBytes(inBetween.FramedQuads)));
    }

    [Fact]
    public void UIRenderer_implements_IDisposable_and_clears_uploaded_state_on_Dispose()
    {
        var list = new UIDrawList();
        list.Begin(TestSkin, 1);
        list.Rect(new UIRect(new Vector2(0, 0), new Vector2(50, 20)), Vector4.One);
        list.End();

        var renderer = new UIRenderer();
        renderer.Upload(list);
        Assert.Equal(1, renderer.Count);

        ((IDisposable)renderer).Dispose();
        Assert.True(renderer.IsEmpty);
        Assert.Equal(0, renderer.Count);
    }

    public static TheoryData<Type, string> ModifiedHotMethods => new()
    {
        { typeof(UIDrawList), "TryReuse" },
        { typeof(UIDrawList), "BeginRecord" },
        { typeof(UIDrawList), "EndRecord" },
        { typeof(UIDrawList), "RecordRunSegments" },
        { typeof(UIDrawList), "Reuse" },
        { typeof(UIDrawList), "ScreenShape" },
        { typeof(UIDrawList), "RoundRect" },
        { typeof(UIDrawList), "RoundOutline" },
        { typeof(UIRenderer), "Upload" },
        { typeof(UIRenderer), "Begin" },
        { typeof(UIRenderer), "GroupSubpixels" },
        { typeof(UIRenderer), "FindBlends" },
        { typeof(UIGlassPass), "Capture" },
        { typeof(UIGlassPass), "CaptureSharp" },
        { typeof(UIGlass), "MixBackdropGenerations" },
        { typeof(UIFont), "Resolve" },
        { typeof(UIFont), "Kerning" },
        { typeof(UIFont), "Measure" },
        { typeof(UITooltip), "Paint" },
        { typeof(UITooltip), "ResolvePlainLayout" },
        { typeof(UIInBetween), "Frames" },
        { typeof(UIInBetween), "Between" },
        { typeof(UIInBetween), "Follow" },
        { typeof(UISkin), "TryGetRegion" },
    };

    [Theory]
    [MemberData(nameof(ModifiedHotMethods))]
    public void Modified_hot_methods_allocate_no_closures(Type type, string methodName)
    {
        MethodInfo[] methods = [.. type.GetMethods(ALL_MEMBERS).Where(m => m.Name == methodName)];
        Assert.True(methods.Length > 0, $"{type.Name} has no method named {methodName}.");

        foreach (MethodInfo method in methods)
        {
            string? closure = FindClosure(method);
            Assert.True(closure is null, $"{type.Name}.{methodName} allocates closure {closure}.");
        }
    }

    private static string? FindClosure(MethodBase method)
    {
        byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
            return null;

        Type[]? ofType = method.DeclaringType is { IsGenericType: true } owner ? owner.GetGenericArguments() : null;
        Type[]? ofMethod = method.IsGenericMethod ? method.GetGenericArguments() : null;

        for (int i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != IL_NEWOBJ)
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
