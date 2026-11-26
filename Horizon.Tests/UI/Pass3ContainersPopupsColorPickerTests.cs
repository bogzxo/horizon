using System.Numerics;
using System.Reflection;

using Horizon.Engine;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.Tests.UI;

/// <summary>
/// Headless unit tests for Pass 3 container, popup, display, and ColorPicker fixes:
/// horizontal ColorPicker hue/white-roundtrip + PaintVersion + zero-alloc hex,
/// TabPanel/MenuBar/StatusBar measurement cache invalidation, MenuBar.Keys span parsing,
/// UIChooser CancellationTokenSource cleanup, TitleBar HiDPI unit scaling, and IL zero-closure checks.
/// </summary>
public class Pass3ContainersPopupsColorPickerTests
{
    private const BindingFlags ALL = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private const byte NEWOBJ = 0x73;

    private static readonly UISkin Skin = new(new UIFont(Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", "cascadia_mono"), "cascadia_mono.ttf"));
    private static readonly UIRect Screen = new(Vector2.Zero, new Vector2(800.0f, 500.0f));

    private static void Paint(UIDrawList list, UIDrawList? previous, UIComponent root, int tick, UISkin? skin = null, UIRect? area = null)
    {
        UISkin active = skin ?? Skin;
        root.MeasureTree(active);
        root.ArrangeTree(area ?? Screen);
        list.Previous = previous;
        list.Begin(active, tick, 1.0f / 120.0f, tick / 120.0f);
        root.PaintTree(list);
        list.End();
    }

    private static Vector2 PointIn(UIRect area, float x, float y) => area.Min + area.Size * new Vector2(x, y);

    [Fact]
    public void ColorPicker_horizontal_hue_picks_green_and_preserves_hue_through_white()
    {
        int changes = 0;
        var picker = new ColorPicker
        {
            Size = new Vector2(300.0f, 150.0f),
            Color = Vector4.One,
            OnChanged = _ => changes++
        };
        picker.MeasureTree(Skin);
        picker.ArrangeTree(new UIRect(Vector2.Zero, new Vector2(300.0f, 150.0f)));

        // Top-right corner of shade square: full red (hue = 0)
        Vector2 full = PointIn(picker.ShadeBounds, 0.96f, 0.96f);
        picker.OnPointerDown(full);
        picker.OnPointerUp(full);
        Assert.True(picker.Color is { X: > 0.85f, Y: < 0.15f, Z: < 0.15f, W: 1.0f } && changes >= 1);

        // Horizontal hue strip at X = 0.34f: green
        Vector2 greenHue = PointIn(picker.HueBounds, 0.34f, 0.5f);
        picker.OnPointerDown(greenHue);
        picker.OnPointerUp(greenHue);
        Assert.True(picker.Color is { X: < 0.15f, Y: > 0.85f, Z: < 0.3f }, $"Expected green, got {picker.Color}");

        // Drag through white (saturation near 0, brightness near 1)
        picker.OnPointerDown(PointIn(picker.ShadeBounds, 0.9f, 0.9f));
        picker.OnPointerDrag(PointIn(picker.ShadeBounds, 0.02f, 0.98f));
        picker.OnPointerUp(PointIn(picker.ShadeBounds, 0.02f, 0.98f));
        Assert.True(picker.Color is { X: > 0.85f, Y: > 0.85f, Z: > 0.85f }, $"Expected near-white, got {picker.Color}");

        // Back to saturated shade: hue must still be green
        Vector2 back = PointIn(picker.ShadeBounds, 0.9f, 0.9f);
        picker.OnPointerDown(back);
        picker.OnPointerUp(back);
        Assert.True(picker.Color is { X: < 0.25f, Y: > 0.8f, Z: < 0.35f }, $"Expected preserved green hue, got {picker.Color}");

        // Horizontal alpha strip at X = 0.5f
        Vector2 halfAlpha = PointIn(picker.AlphaBounds, 0.5f, 0.5f);
        picker.OnPointerDown(halfAlpha);
        picker.OnPointerUp(halfAlpha);
        Assert.True(MathF.Abs(picker.Color.W - 0.5f) <= 0.06f, $"Expected alpha ~0.5, got {picker.Color.W}");
    }

    [Fact]
    public void ColorPicker_zero_dimension_bounds_never_produces_NaN()
    {
        var picker = new ColorPicker { Color = new Vector4(0.2f, 0.6f, 0.8f, 0.9f) };
        picker.ArrangeTree(default);

        picker.OnPointerDown(Vector2.Zero);
        picker.OnPointerDrag(new Vector2(10.0f, 10.0f));
        picker.OnPointerUp(new Vector2(10.0f, 10.0f));

        Vector4 c = picker.Color;
        Assert.True(float.IsFinite(c.X) && float.IsFinite(c.Y) && float.IsFinite(c.Z) && float.IsFinite(c.W));
    }

    [Fact]
    public void ColorPicker_hex_formatting_and_span_parsing_roundtrip()
    {
        Vector4 opaque = new(1.0f, 0.5f, 0.0f, 1.0f);
        Assert.Equal("#FF8000", ColorPicker.ToHex(opaque));

        Span<char> buf = stackalloc char[9];
        Assert.True(ColorPicker.TryFormatHex(opaque, buf, out int written));
        Assert.Equal(7, written);
        Assert.Equal("#FF8000", buf[..written].ToString());

        Vector4 semi = new(0.0f, 1.0f, 0.5f, 0.5f);
        Assert.Equal("#00FF8080", ColorPicker.ToHex(semi));
        Assert.True(ColorPicker.TryFormatHex(semi, buf, out written));
        Assert.Equal(9, written);
        Assert.Equal("#00FF8080", buf[..written].ToString());
        Assert.False(ColorPicker.TryFormatHex(semi, buf[..6], out _));

        // 3-digit, 6-digit, 8-digit, with/without '#', whitespace, lowercase
        Vector4? shortHex = ColorPicker.FromHex("  #f80 ");
        Assert.NotNull(shortHex);
        Assert.True(Vector4.Distance(shortHex.Value, new Vector4(1.0f, 0x88 / 255.0f, 0.0f, 1.0f)) < 0.001f);

        Vector4? spanHex = ColorPicker.FromHex("00ff8080".AsSpan());
        Assert.NotNull(spanHex);
        Assert.True(Vector4.Distance(spanHex.Value, new Vector4(0.0f, 1.0f, 0x80 / 255.0f, 0x80 / 255.0f)) < 0.001f);

        Assert.Null(ColorPicker.FromHex(""));
        Assert.Null(ColorPicker.FromHex("#12345"));
        Assert.Null(ColorPicker.FromHex("#GGGGGG"));
    }

    [Fact]
    public void ColorPicker_reuses_draw_list_when_idle_and_invalidates_on_changes()
    {
        var picker = new ColorPicker
        {
            Size = new Vector2(300.0f, 180.0f),
            Color = new Vector4(0.3f, 0.7f, 0.9f, 1.0f)
        };

        var a = new UIDrawList();
        var b = new UIDrawList();
        var fresh = new UIDrawList();
        var area = new UIRect(Vector2.Zero, new Vector2(300.0f, 180.0f));

        Paint(a, null, picker, 1, area: area);
        Paint(b, a, picker, 2, area: area);
        Assert.True(b.Reused > 0, "Idle ColorPicker should reuse its previous draw list.");

        Paint(fresh, null, picker, 2, area: area);
        Assert.True(b.SameAs(fresh), "Reused ColorPicker draw list must match a fresh paint.");

        picker.Color = new Vector4(0.9f, 0.2f, 0.4f, 0.5f);
        Paint(a, b, picker, 3, area: area);
        Assert.Equal(0, a.Reused);
        Paint(fresh, null, picker, 3, area: area);
        Assert.True(a.SameAs(fresh));

        picker.ShowAlpha = false;
        Paint(b, a, picker, 4, area: area);
        Assert.Equal(0, b.Reused);

        picker.Original = new Vector4(0.1f, 0.2f, 0.3f, 1.0f);
        Paint(a, b, picker, 5, area: area);
        Assert.Equal(0, a.Reused);
    }

    [Fact]
    public void TabPanel_measurement_cache_invalidates_on_title_scale_and_spacing_changes()
    {
        using var compositor = new UICompositor(new Camera2D(new Vector2(800, 600)));
        var module = compositor.CreateModule();
        var tabs = module.AddComponent(new TabPanel
        {
            Size = new Vector2(600.0f, 200.0f),
            Tabs = ["One", "Two", "Three"],
            Spacing = 20.0f
        });
        tabs.Add(new Panel { Size = new Vector2(100, 100) });
        tabs.Add(new Panel { Size = new Vector2(100, 100) });
        tabs.Add(new Panel { Size = new Vector2(100, 100) });

        var list = new UIDrawList();
        compositor.RouteForTest(Screen, Skin, 1.0f / 60.0f);
        Paint(list, null, module.Root, 1);

        // Ensure TabBounds / TabAt work with the cache
        UIRect firstBefore = tabs.TabBounds(0);
        UIRect secondBefore = tabs.TabBounds(1);
        Assert.True(firstBefore.Width > 0.0f);
        Assert.True(secondBefore.Min.X > firstBefore.Min.X);
        Assert.Equal(1, tabs.TabAt(secondBefore.Center));

        // Mutating tab titles invalidates the width cache
        tabs.Tabs = ["Much Longer First Tab Name", "Two", "Three"];
        Paint(list, null, module.Root, 2);
        UIRect firstAfter = tabs.TabBounds(0);
        UIRect secondAfter = tabs.TabBounds(1);
        Assert.True(firstAfter.Width > firstBefore.Width);
        Assert.True(secondAfter.Min.X > secondBefore.Min.X);

        // Spacing change invalidates offsets
        float gapBefore = secondAfter.Min.X - firstAfter.Max.X;
        tabs.Spacing = 44.0f;
        Paint(list, null, module.Root, 3);
        float gapAfter = tabs.TabBounds(1).Min.X - tabs.TabBounds(0).Max.X;
        Assert.True(gapAfter > gapBefore);
    }

    [Fact]
    public void MenuBar_measurement_cache_invalidates_on_title_visibility_and_room_changes()
    {
        using var compositor = new UICompositor(new Camera2D(new Vector2(800, 600)));
        var module = compositor.CreateModule();
        var bar = module.AddComponent(new MenuBar { Size = new Vector2(500.0f, 28.0f) });
        Menu file = bar.AddMenu("&File");
        Menu edit = bar.AddMenu("&Edit");
        Menu view = bar.AddMenu("&View");
        bool showEdit = true;
        edit.IsVisible = () => showEdit;

        var list = new UIDrawList();
        compositor.RouteForTest(Screen, Skin, 1.0f / 60.0f);
        Paint(list, null, module.Root, 1);

        UIRect fileBefore = bar.TitleBounds(0);
        UIRect editBefore = bar.TitleBounds(1);
        UIRect viewBefore = bar.TitleBounds(2);
        float widthBefore = bar.ShownWidth(Skin);
        Assert.True(fileBefore.Width > 0.0f && editBefore.Min.X >= fileBefore.Max.X - 0.1f);
        Assert.True(MathF.Abs(widthBefore - (fileBefore.Width + editBefore.Width + viewBefore.Width)) < 0.5f);

        // Renaming a menu invalidates cached width and offsets
        file.Title = "&File Operations";
        Paint(list, null, module.Root, 2);
        Assert.True(bar.TitleBounds(0).Width > fileBefore.Width);
        Assert.True(bar.TitleBounds(1).Min.X > editBefore.Min.X);

        // Hiding a menu invalidates shown mask and shifts later menus left
        showEdit = false;
        Paint(list, null, module.Root, 3);
        Assert.True(bar.TitleBounds(1).IsEmpty);
        Assert.True(bar.TitleBounds(2).Min.X < bar.TitleBounds(0).Max.X + 1.0f);

        // Narrowing Room folds overflow menus into FoldedMenu
        showEdit = true;
        bar.Room = 80.0f;
        Paint(list, null, module.Root, 4);
        Assert.True(bar.FoldedCount > 0);
        Assert.NotNull(bar.FoldedMenu);
    }

    [Fact]
    public void MenuBar_Keys_parses_mnemonics_accurately()
    {
        Assert.Equal("File", MenuBar.TitleShown("&File"));
        Assert.Equal("Edit", MenuBar.TitleShown("E&dit"));
        Assert.Equal("File&", MenuBar.TitleShown("File&"));
        Assert.Equal("Plain", MenuBar.TitleShown("Plain"));

        Assert.Equal('f', MenuBar.MnemonicOf(new Menu("&File")));
        Assert.Equal('d', MenuBar.MnemonicOf(new Menu("E&dit")));
        Assert.Equal('h', MenuBar.MnemonicOf(new Menu("Help")));
        Assert.Equal('f', MenuBar.MnemonicOf(new Menu("File&")));
        Assert.Equal('\0', MenuBar.MnemonicOf(new Menu("...")));

        Assert.Equal(0, MenuBar.MnemonicAt("&File"));
        Assert.Equal(1, MenuBar.MnemonicAt("E&dit"));
        Assert.Equal(0, MenuBar.MnemonicAt("Help"));
        Assert.Equal(-1, MenuBar.MnemonicAt("..."));
    }

    [Fact]
    public void StatusBar_measurement_cache_invalidates_on_text_visibility_and_bounds()
    {
        using var compositor = new UICompositor(new Camera2D(new Vector2(800, 600)));
        var module = compositor.CreateModule();
        bool showRight = true;
        var bar = module.AddComponent(new StatusBar { Size = new Vector2(600.0f, 26.0f) });
        var left = bar.Add("Ready");
        var right = bar.Add("Ln 1, Col 1", right: true);
        right.IsVisible = () => showRight;

        var list = new UIDrawList();
        compositor.RouteForTest(Screen, Skin, 1.0f / 60.0f);
        Paint(list, null, module.Root, 1);

        UIRect leftBefore = bar.ItemBounds(left);
        UIRect rightBefore = bar.ItemBounds(right);
        Assert.True(leftBefore.Width > 0.0f && rightBefore.Width > 0.0f);
        Assert.True(rightBefore.Min.X > leftBefore.Max.X);

        // Text mutation updates cached width
        left.Text = "Building solution with many projects";
        Paint(list, null, module.Root, 2);
        Assert.True(bar.ItemBounds(left).Width > leftBefore.Width);

        // Visibility mutation hides item
        showRight = false;
        Paint(list, null, module.Root, 3);
        Assert.True(bar.ItemBounds(right).IsEmpty);

        // Bounds resize shifts right-aligned item
        showRight = true;
        bar.Size = new Vector2(400.0f, 26.0f);
        Paint(list, null, module.Root, 4);
        Assert.True(bar.ItemBounds(right).Max.X < rightBefore.Max.X);
    }

    [Fact]
    public void UIChooser_cancels_and_disposes_search_CTS_on_replace_and_close()
    {
        bool motionBefore = UIDrawList.Motion;
        UIDrawList.Motion = false;
        try
        {
            using var compositor = new UICompositor(new Camera2D(new Vector2(800, 600)));
            var module = compositor.CreateModule();

            var tokens = new List<CancellationToken>();
            using var gate = new ManualResetEventSlim(false);
            using var firstStarted = new ManualResetEventSlim(false);
            using var secondStarted = new ManualResetEventSlim(false);

            try
            {
                UIChoiceSearch search = (typed, cancel) =>
                {
                    lock (tokens)
                        tokens.Add(cancel);
                    if (typed == "a")
                        firstStarted.Set();
                    else if (typed == "ab")
                        secondStarted.Set();
                    gate.Wait(TimeSpan.FromSeconds(5));
                    return [];
                };

                var dialog = UIChooser.Show(module, "Search", "", new List<UIChoice>(), searched: prefix => prefix == '\0' ? search : null);
                bool userClosedCalled = false;
                // Overwriting public Closed must NOT clobber internal CTS cleanup
                dialog.Closed = () => userClosedCalled = true;

                TextBox query = FindAll(dialog).OfType<TextBox>().Single();

                query.Text = "a";
                query.OnChanged!(query.Text);
                Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(2)));

                // Replacing the search cancels and disposes the first CTS
                query.Text = "ab";
                query.OnChanged!(query.Text);

                CancellationToken firstToken;
                lock (tokens)
                    firstToken = tokens[0];
                Assert.True(firstToken.IsCancellationRequested);

                Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(2)));

                // Closing the dialog cancels and disposes the active search CTS even though user set Closed
                dialog.Close();
                Assert.True(userClosedCalled);

                CancellationToken lastToken;
                lock (tokens)
                    lastToken = tokens[^1];
                Assert.True(lastToken.IsCancellationRequested);
            }
            finally
            {
                gate.Set();
            }
        }
        finally
        {
            UIDrawList.Motion = motionBefore;
        }
    }

    [Fact]
    public void TitleBar_UnitFor_handles_HiDPI_ScaleToDisplay_and_DesignSize()
    {
        // PixelSized off: always 1.0
        Assert.Equal(1.0f, TitleBar.UnitFor(pixelSized: false, uiScale: 2.0f, displayScale: 1.5f));

        // DesignSize 2x without HiDPI: 1.0 / 2.0 = 0.5 -> stays 32 window pixels
        Assert.Equal(0.5f, TitleBar.UnitFor(pixelSized: true, uiScale: 2.0f, displayScale: 1.0f), 4);

        // HiDPI 1.5x with Scale 1.5x (UIScale = 2.25): 1.5 / 2.25 -> stays 32 * 1.5 = 48 window pixels
        Assert.Equal(1.5f / 2.25f, TitleBar.UnitFor(pixelSized: true, uiScale: 2.25f, displayScale: 1.5f), 4);

        // Zero or negative uiScale safely falls back to 1.0
        Assert.Equal(1.0f, TitleBar.UnitFor(pixelSized: true, uiScale: 0.0f, displayScale: 1.5f));
        Assert.Equal(1.0f, TitleBar.UnitFor(pixelSized: true, uiScale: -1.0f, displayScale: 1.5f));
    }

    public static TheoryData<Type, string> ModifiedHotPaths => new()
    {
        { typeof(ColorPicker), "Paint" },
        { typeof(ColorPicker), "PaintShadeCells" },
        { typeof(ColorPicker), "PaintHueCells" },
        { typeof(ColorPicker), "PaintAlphaCells" },
        { typeof(ColorPicker), "FromHex" },
        { typeof(ColorPicker), "TryFormatHex" },
        { typeof(TabPanel), "Paint" },
        { typeof(TabPanel), "Measure" },
        { typeof(TabPanel), "TabBounds" },
        { typeof(TabPanel), "TabAt" },
        { typeof(MenuBar), "Paint" },
        { typeof(MenuBar), "Update" },
        { typeof(MenuBar), "TitleBounds" },
        { typeof(MenuBar), "ShownWidth" },
        { typeof(MenuBar), "PaintMnemonic" },
        { typeof(StatusBar), "Paint" },
        { typeof(StatusBar), "Update" },
        { typeof(StatusBar), "ItemBounds" },
        { typeof(TitleBar), "Paint" },
        { typeof(CodeView), "Paint" },
        { typeof(CodeView), "Measure" },
    };

    [Theory]
    [MemberData(nameof(ModifiedHotPaths))]
    public void Modified_hot_paths_make_no_closures(Type type, string name)
    {
        MethodInfo[] methods = [.. type.GetMethods(ALL).Where(method => method.Name == name)];
        Assert.True(methods.Length > 0, $"{type.Name} has no method called {name}.");

        foreach (MethodInfo method in methods)
        {
            string? closure = ClosureMadeIn(method);
            Assert.True(closure is null, $"{type.Name}.{name} makes a closure ({closure}).");
        }
    }

    private static IEnumerable<UIComponent> FindAll(UIComponent component) =>
        component.Children.SelectMany(child => new[] { child }.Concat(FindAll(child)));

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
