using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Horizon.Engine;
using Horizon.Graphics;
using Horizon.Rendering;
using Horizon.Rendering.Spriting;
using Horizon.Rendering.Text;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.Tests.UI;

/// <summary>
/// Headless unit tests for Pass 6-C UI Drawing, Glyph L1 Cache, UIFrameQuad hot/cold split,
/// multi-run/glass <see cref="UIInBetween"/> stationary bypass, lazy single-theme <see cref="UISkin"/> loading,
/// and lazy <see cref="UIModule.Runtime"/> isolation.
/// </summary>
public class Pass6UiDrawingAndSkinTests
{
    private const BindingFlags ALL_MEMBERS =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private const byte IL_NEWOBJ = 0x73;

    private static readonly string CascadiaDir = Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", "cascadia_mono");
    private static readonly UIFont CascadiaFont = new(CascadiaDir, "cascadia_mono.ttf");
    private static readonly UISkin TestSkin = new(CascadiaFont);

    private static Texture MakeDummyTexture() =>
        (Texture)RuntimeHelpers.GetUninitializedObject(typeof(Texture));

    private static string RepoRoot()
    {
        for (string? at = AppContext.BaseDirectory; at is not null; at = Path.GetDirectoryName(at))
        {
            if (File.Exists(Path.Combine(at, "Horizon.sln")))
                return at;
        }

        throw new DirectoryNotFoundException("The tests have to run somewhere under the repo.");
    }

    private sealed class CustomPainterComponent : UIComponent
    {
        private readonly Action<UIDrawList, UIRect> paint;

        public CustomPainterComponent(Action<UIDrawList, UIRect> paint) => this.paint = paint;

        public long Version { get; set; } = 1;

        protected override long PaintVersion => Version;

        protected override void Paint(UIDrawList list) => paint(list, Bounds);
    }

    [Fact]
    public void GlyphAtlas_preallocates_upload_buffers_in_constructor()
    {
        var grey = new GlyphAtlas(subpixel: false);
        var subpixel = new GlyphAtlas(subpixel: true);

        Assert.True(grey.PendingBytesCapacity >= 4096);
        Assert.True(grey.UploadBytesCapacity >= 4096);
        Assert.True(grey.PendingGlyphsCapacity >= 32);
        Assert.True(grey.UploadGlyphsCapacity >= 32);

        Assert.True(subpixel.PendingBytesCapacity >= 4096);
        Assert.True(subpixel.UploadBytesCapacity >= 4096);
        Assert.True(subpixel.PendingGlyphsCapacity >= 32);
        Assert.True(subpixel.UploadGlyphsCapacity >= 32);
    }

    [Fact]
    public void GlyphAtlas_L1_ascii_page_matches_dictionary_lookup_on_hits_misses_spaces_and_unicode()
    {
        GlyphOutlines? outlines = CascadiaFont.Outlines;
        Assert.NotNull(outlines);

        var atlas = new GlyphAtlas(subpixel: false);
        ushort size = GlyphAtlas.SizeOf(14.0f);
        Assert.True(size > 0);
        const GlyphStyle style = GlyphStyle.None;

        GlyphAtlas.AsciiPage page = atlas.GetAsciiPage(outlines!.Id, size, style);
        Assert.Same(page, atlas.GetAsciiPage(outlines.Id, size, style));

        // First pass populates via slow path -> publishes into L1 AsciiPage; second pass hits L1 array directly.
        const string sample = "Hello, World! 0123456789 \t[]{}$";
        for (int pass = 0; pass < 2; pass++)
        {
            for (byte phase = 0; phase < GlyphAtlas.PHASES; phase++)
            {
                foreach (char ch in sample)
                {
                    bool fromPage = atlas.TryGet(outlines, page, ch, size, phase, style, 0.0f, 0.0f, out GlyphSlot pageSlot);
                    bool fromDict = atlas.TryGet(outlines, new GlyphKey(outlines.Id, ch, size, phase, style), 0.0f, 0.0f, out GlyphSlot dictSlot);

                    Assert.Equal(fromDict, fromPage);
                    Assert.Equal(dictSlot, pageSlot);
                }
            }
        }

        // Space has no ink and must be cached as empty (returns false) on repeated L1 lookups.
        Assert.False(atlas.TryGet(outlines, page, ' ', size, 0, style, 0.0f, 0.0f, out GlyphSlot spaceSlot));
        Assert.True(spaceSlot.Box.IsEmpty);

        // Non-ASCII characters fall back cleanly to the dictionary without throwing or corrupting the L1 page.
        foreach (char nonAscii in new[] { 'é', 'Ω', '─' })
        {
            bool fromPage = atlas.TryGet(outlines, page, nonAscii, size, 1, style, 0.0f, 0.0f, out GlyphSlot pageSlot);
            bool fromDict = atlas.TryGet(outlines, new GlyphKey(outlines.Id, nonAscii, size, 1, style), 0.0f, 0.0f, out GlyphSlot dictSlot);
            Assert.Equal(fromDict, fromPage);
            Assert.Equal(dictSlot, pageSlot);
        }
    }

    [Fact]
    public void UIFont_MeasureLine_fast_path_matches_Measure_and_bold_advance()
    {
        const float scale = 0.5f;
        const string plain = "AVATAR To WA quick brown fox 12345";

        float plainLine = CascadiaFont.MeasureLine(plain, scale, UITextStyle.None, default, markup: true);
        float plainNoMarkup = CascadiaFont.MeasureLine(plain, scale, UITextStyle.None, default, markup: false);
        float measured = CascadiaFont.Measure(plain, scale, markup: false).X;

        Assert.Equal(measured, plainLine, 4);
        Assert.Equal(plainNoMarkup, plainLine, 6);

        float boldLine = CascadiaFont.MeasureLine(plain, scale, UITextStyle.Bold, default, markup: true);
        float expectedBold = plainLine + plain.Length * CascadiaFont.BoldAdvance * scale;
        Assert.Equal(expectedBold, boldLine, 3);

        // Strings containing '$' or '[' fall through to the markup-aware path and still match when no valid tag exists.
        const string withDelims = "cost $42 [literal]";
        float delimMarkup = CascadiaFont.MeasureLine(withDelims, scale, UITextStyle.None, default, markup: true);
        float delimPlain = CascadiaFont.MeasureLine(withDelims, scale, UITextStyle.None, default, markup: false);
        Assert.Equal(delimPlain, delimMarkup, 4);
    }

    [Fact]
    public void UIFrameQuad_is_compact_and_hot_cold_split_round_trips_through_Resolve_and_Reuse()
    {
        Assert.True(Unsafe.SizeOf<UIFrameQuad>() <= 24, $"Expected compact UIFrameQuad <= 24 B, got {Unsafe.SizeOf<UIFrameQuad>()} B.");

        Texture tex1 = MakeDummyTexture();
        Texture tex2 = MakeDummyTexture();
        Texture tex3 = MakeDummyTexture();

        var plainComponent = new CustomPainterComponent((list, rect) =>
        {
            list.Rect(rect, new Vector4(0.2f, 0.4f, 0.8f, 1.0f));
            list.RoundRect(rect.Shrink(new UIEdges(4.0f)), 4.0f, new Vector4(0.8f, 0.3f, 0.2f, 1.0f));
        });
        plainComponent.ArrangeTree(new UIRect(new Vector2(10, 10), new Vector2(120, 50)));

        var italicAndMultiRun = new CustomPainterComponent((list, rect) =>
        {
            list.Image(tex1, new UIRect(rect.Min, rect.Min + new Vector2(16, 16)), Vector2.Zero, new Vector2(16, 16), Vector4.One);
            list.Image(tex2, new UIRect(rect.Min + new Vector2(20, 0), rect.Min + new Vector2(36, 16)), Vector2.Zero, new Vector2(16, 16), Vector4.One);
            list.Image(tex3, new UIRect(rect.Min + new Vector2(40, 0), rect.Min + new Vector2(56, 16)), Vector2.Zero, new Vector2(16, 16), Vector4.One);
            // Italic text uses Slant != 0 -> populates cold UIQuadExtra entries
            list.Text("Italic", rect.Min + new Vector2(0, 30), 0.5f, Vector4.One, markup: false, UITextStyle.Italic, default);
        });
        italicAndMultiRun.ArrangeTree(new UIRect(new Vector2(10, 60), new Vector2(200, 110)));

        // Plain paint produces zero UIQuadExtra entries
        var plainList = new UIDrawList();
        plainList.Begin(TestSkin, 1);
        plainComponent.PaintTree(plainList);
        plainList.End();
        Assert.Empty(plainList.FrameList.Extras);
        Assert.Null(plainList.FramesDiffer());

        // Tick 1: paint both components (italic populates UIQuadExtra; 3 textures force 2 runs)
        var listA = new UIDrawList();
        listA.Begin(TestSkin, 1);
        plainComponent.PaintTree(listA);
        italicAndMultiRun.PaintTree(listA);
        listA.End();
        Assert.True(listA.FrameList.Extras.Count > 0);
        Assert.Equal(2, listA.Runs.Length);
        Assert.Null(listA.FramesDiffer());

        // Tick 2 (zero-delta fast-skip Reuse): exact same layout & positions
        var listB = new UIDrawList { Previous = listA };
        listB.Begin(TestSkin, 2);
        plainComponent.PaintTree(listB);
        italicAndMultiRun.PaintTree(listB);
        listB.End();
        Assert.Equal(2, listB.Reused);
        Assert.True(listA.SameAs(listB));
        Assert.Null(listB.FramesDiffer());

        // Tick 3 (non-zero-delta rebased Reuse): prefix adds extra quads before reused components
        var listC = new UIDrawList { Previous = listB };
        listC.Begin(TestSkin, 3);
        listC.Rect(new UIRect(new Vector2(0, 0), new Vector2(5, 5)), Vector4.One);
        listC.Rect(new UIRect(new Vector2(6, 0), new Vector2(11, 5)), Vector4.One);
        plainComponent.PaintTree(listC);
        italicAndMultiRun.PaintTree(listC);
        listC.End();
        Assert.Equal(2, listC.Reused);
        Assert.Equal(2, listC.Runs.Length);
        Assert.Null(listC.FramesDiffer());
    }

    [Fact]
    public void TryBypassStationary_handles_multi_run_and_glass_frames_identically_to_full_Resolve()
    {
        Texture tex1 = MakeDummyTexture();
        Texture tex2 = MakeDummyTexture();
        Texture tex3 = MakeDummyTexture();

        var list = new UIDrawList();
        list.Begin(TestSkin, 1);
        list.Image(tex1, new UIRect(new Vector2(0, 0), new Vector2(20, 20)), Vector2.Zero, new Vector2(16, 16), Vector4.One);
        list.Image(tex2, new UIRect(new Vector2(24, 0), new Vector2(44, 20)), Vector2.Zero, new Vector2(16, 16), Vector4.One);
        list.Glass(new UIRect(new Vector2(10, 10), new Vector2(120, 80)), 8.0f, UIGlass.Frosted, new Vector4(0.1f, 0.1f, 0.12f, 0.8f));
        // Third texture forces a second run after the glass pane
        list.Image(tex3, new UIRect(new Vector2(48, 0), new Vector2(68, 20)), Vector2.Zero, new Vector2(16, 16), Vector4.One);
        list.Rect(new UIRect(new Vector2(72, 0), new Vector2(100, 20)), new Vector4(0.3f, 0.6f, 0.9f, 1.0f));
        list.End();

        Assert.True(list.Runs.Length > 1);
        Assert.True(list.GlassPanes.Length > 0);
        Assert.Null(list.FramesDiffer());

        var framesA = new UIFrameList();
        framesA.CopyFrom(list.FrameList);
        var framesB = new UIFrameList();
        framesB.CopyFrom(list.FrameList);

        // Full Resolve baseline (no paintedItems passed)
        var full = new UIInBetween();
        full.Frames(framesA, framesB, 1.0f);

        // Stationary bypass with paintedItems + paintedRuns + paintedGlass
        var bypassWithMeta = new UIInBetween();
        bypassWithMeta.Frames(framesA, framesB, 1.0f, null, list.Items, list.Runs, list.GlassPanes, hasPaintedGlass: true);
        Assert.Equal(1, bypassWithMeta.StationaryBypasses);
        Assert.True(MemoryMarshal.AsBytes(full.FramedQuads).SequenceEqual(MemoryMarshal.AsBytes(bypassWithMeta.FramedQuads)));
        Assert.Equal(full.FramedRuns.ToArray(), bypassWithMeta.FramedRuns.ToArray());
        Assert.True(MemoryMarshal.AsBytes(full.FramedGlass).SequenceEqual(MemoryMarshal.AsBytes(bypassWithMeta.FramedGlass)));

        // Stationary bypass with paintedItems only (uses UIFrameRun.ItemEnd + ResolveStationaryGlass)
        var bypassItemsOnly = new UIInBetween();
        bypassItemsOnly.Frames(framesA, framesB, 0.5f, null, list.Items);
        Assert.Equal(1, bypassItemsOnly.StationaryBypasses);
        Assert.True(MemoryMarshal.AsBytes(full.FramedQuads).SequenceEqual(MemoryMarshal.AsBytes(bypassItemsOnly.FramedQuads)));
        Assert.Equal(full.FramedRuns.ToArray(), bypassItemsOnly.FramedRuns.ToArray());
        Assert.True(MemoryMarshal.AsBytes(full.FramedGlass).SequenceEqual(MemoryMarshal.AsBytes(bypassItemsOnly.FramedGlass)));
    }

    [Fact]
    public void UISkin_PaletteOf_lazily_evaluates_only_requested_theme_and_its_base_chain()
    {
        string flatDir = Path.Combine(RepoRoot(), "Horizon", "Assets", "uix", "flat") + Path.DirectorySeparatorChar;
        var (_, allThemes, usual) = UISkin.Describe(flatDir, UISkins.FILE);
        Assert.True(allThemes.Length >= 8);
        Assert.Equal("horizon", usual);

        int before = UISkin.ThemeFilesEvaluated;
        UIPalette horizon = UISkin.PaletteOf(flatDir, UISkins.FILE, "horizon");
        int evaluatedForHorizon = UISkin.ThemeFilesEvaluated - before;

        Assert.Equal(1, evaluatedForHorizon);
        Assert.NotEqual(default, horizon.Text);

        // Verify a theme with a base chain evaluates only its chain (not all 8 themes).
        string tempDir = Path.Combine(Path.GetTempPath(), "horizon_lazy_skin_" + Guid.NewGuid().ToString("N"));
        string themesDir = Path.Combine(tempDir, "themes");
        Directory.CreateDirectory(themesDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "skin.hor"), """
                let skin = {
                    title: "Lazy Test",
                    theme: "child",
                    text_color: vec(1.0, 1.0, 1.0),
                    panel_color: vec(0.07, 0.07, 0.07)
                };
                """);
            File.WriteAllText(Path.Combine(themesDir, "parent.hor"), """
                let theme = {
                    title: "Parent",
                    accent_color: vec(0.133, 0.533, 1.0)
                };
                """);
            File.WriteAllText(Path.Combine(themesDir, "child.hor"), """
                let theme = {
                    title: "Child",
                    base: "parent",
                    text_color: vec(0.933, 0.933, 0.933)
                };
                """);
            // Unused theme with invalid syntax — would throw if eagerly evaluated!
            File.WriteAllText(Path.Combine(themesDir, "unused_broken.hor"), "this is not valid hidl {{{");

            int beforeChain = UISkin.ThemeFilesEvaluated;
            UIPalette childPalette = UISkin.PaletteOf(tempDir, "skin.hor", "child");
            int evaluatedForChain = UISkin.ThemeFilesEvaluated - beforeChain;

            Assert.Equal(2, evaluatedForChain);
            Assert.Equal(0x22 / 255.0f, childPalette.Accent.X, 2);
            Assert.Equal(0xEE / 255.0f, childPalette.Text.X, 2);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void UIModule_Runtime_is_lazy_and_isolated_for_csharp_constructed_modules()
    {
        using var compositor = new UICompositor(new Camera2D(new Vector2(800.0f, 600.0f)));
        UIModule csharpModule = compositor.CreateModule();
        UIModule scriptedModule = compositor.CreateModule();

        // Pure C# UI construction never allocates a HIDLRuntime.
        csharpModule.AddComponent(new Label { Text = "Hello" });
        Assert.False(csharpModule.HasRuntime);
        Assert.False(scriptedModule.HasRuntime);

        // Static kind registry works even before any UIModule.Runtime is touched.
        Assert.Contains("button", UIModule.Kinds);
        Assert.Equal("button", UIModule.KindOf(new Button()));
        Assert.IsType<Panel>(UIModule.CreateDetached("panel"));

        // Accessing Runtime on one module initializes ONLY that module's runtime.
        var (ok, message) = scriptedModule.Runtime.Evaluate("compositor.label({ text: \"from script\" });");
        Assert.True(ok, message);
        Assert.True(scriptedModule.HasRuntime);
        Assert.False(csharpModule.HasRuntime);
        Assert.Single(scriptedModule.Components);
        Assert.Equal("from script", Assert.IsType<Label>(scriptedModule.Components[0]).Text);
    }

    [Theory]
    [InlineData(typeof(UIDrawList), "EmitScreenGlyph")]
    [InlineData(typeof(UIDrawList), "Glyph")]
    [InlineData(typeof(UIDrawList), "FlushSubpixels")]
    [InlineData(typeof(UIInBetween), "TryBypassStationary")]
    [InlineData(typeof(UIFrameList), "Resolve")]
    [InlineData(typeof(UIFrameList), "ResolveStationaryGlass")]
    [InlineData(typeof(UICompositor), "DrawGlassy")]
    [InlineData(typeof(UIFont), "MeasureLine")]
    public void Pass6_hot_methods_allocate_no_closures(Type type, string methodName)
    {
        MethodInfo[] methods = [.. type.GetMethods(ALL_MEMBERS).Where(m => m.Name == methodName)];
        Assert.NotEmpty(methods);

        foreach (MethodInfo method in methods)
        {
            byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
            if (il is null)
                continue;

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

                if (made?.DeclaringType is { } declared)
                    Assert.False(declared.Name.StartsWith("<>c__DisplayClass", StringComparison.Ordinal),
                        $"{type.Name}.{methodName} allocates closure {declared.Name}.");
            }
        }
    }
}
