using System.Numerics;
using System.Reflection;

using Horizon.Engine;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.Tests.UI;

/// <summary>
/// Headless unit tests for Pass 4 control bug fixes, cache invalidation, O(N) text hit-testing,
/// draw-list reuse stability, and IL zero-closure checks on modified hot paths.
/// </summary>
public sealed class Pass4ControlsAndViewsTests
{
    private const BindingFlags ALL = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private const byte NEWOBJ = 0x73;

    private static UISkin MakeSkin() => new(new UIFont(Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", "cascadia_mono"), "cascadia_mono.ttf"));

    private static (UICompositor Compositor, UIModule Module, UISkin Skin) MakeHost()
    {
        var skin = MakeSkin();
        var compositor = new UICompositor(new Camera2D(new Vector2(800, 600)));
        typeof(UICompositor).GetField("skin", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(compositor, skin);
        var module = compositor.CreateModule();
        return (compositor, module, skin);
    }

    private sealed class InspectableTreeView : TreeView
    {
        public long Version => PaintVersion;
        public void Draw(UIDrawList list) => Paint(list);
    }

    private sealed class InspectableTable : Table
    {
        public long Version => PaintVersion;
        public void Draw(UIDrawList list) => Paint(list);
    }

    private sealed class InspectableListBox : ListBox
    {
        public long Version => PaintVersion;
        public void Draw(UIDrawList list) => Paint(list);
    }

    private sealed class InspectableTabStrip : TabStrip
    {
        public long Version => PaintVersion;
        public void Draw(UIDrawList list) => Paint(list);
    }

    private sealed class InspectableTextBox : TextBox
    {
        public long Version => PaintVersion;
        public void Draw(UIDrawList list) => Paint(list);
    }

    [Fact]
    public void TreeView_failed_edit_retry_preserves_check_and_cancelled_callbacks()
    {
        var (_, module, skin) = MakeHost();
        var item = new UITreeItem("original.cs");
        var tree = module.AddComponent(new TreeView { Items = [item], Size = new Vector2(300, 200) });
        module.Root.MeasureTree(skin);
        module.Root.ArrangeTree(new UIRect(Vector2.Zero, new Vector2(400, 300)));

        int checkCalls = 0, commitCalls = 0, cancelCalls = 0;
        tree.Edit(
            item,
            "renamed.cs",
            0,
            7,
            check: text =>
            {
                checkCalls++;
                return text.Contains('!') ? "invalid name" : null;
            },
            commit: _ =>
            {
                commitCalls++;
                return false;
            },
            cancelled: () => cancelCalls++);

        // Failed commit retries editing on the same item and must preserve both editCheck and editCancelled.
        tree.EndEdit(keep: true);
        Assert.Equal(1, commitCalls);
        Assert.Same(item, tree.Edited);
        Assert.NotNull(tree.EditBox);

        int checksBefore = checkCalls;
        tree.EditBox!.Insert('!');
        Assert.True(checkCalls > checksBefore);
        Assert.Equal("invalid name", tree.EditProblem);

        tree.EndEdit(keep: false);
        Assert.Null(tree.Edited);
        Assert.Equal(1, cancelCalls);
    }

    [Fact]
    public void TreeView_wrapping_note_preserves_selection_identity_and_full_text()
    {
        var (_, module, skin) = MakeHost();
        const string fullText = "This is a deliberately long wrapping tree note that must split across multiple visible lines";
        var note = new UITreeItem(fullText) { Wraps = true };
        var tree = module.AddComponent(new InspectableTreeView { Items = [note], Size = new Vector2(140, 240) });
        tree.Selected = note;

        module.Root.MeasureTree(skin);
        module.Root.ArrangeTree(new UIRect(Vector2.Zero, new Vector2(140, 240)));
        tree.UpdateTree(1.0f / 60.0f);

        Assert.True(tree.VisibleCount > 1);
        Assert.Same(note, tree.Selected);
        Assert.Same(note, tree.VisibleAt(0));
        Assert.Equal(fullText, note.Text);
        Assert.NotNull(tree.RowBounds(note));
    }

    [Fact]
    public void TreeView_PaintVersion_handles_FocusVisible_and_truncation_invalidates_on_scale_change()
    {
        var (compositor, module, skin) = MakeHost();
        var item = new UITreeItem("A very long tree item label that gets truncated in a narrow column");
        var tree = module.AddComponent(new InspectableTreeView { Items = [item], Size = new Vector2(140, 200), TextScale = 0.4f });
        module.Root.MeasureTree(skin);
        module.Root.ArrangeTree(new UIRect(Vector2.Zero, new Vector2(140, 200)));

        var list = new UIDrawList();
        list.Begin(skin);
        tree.Draw(list);
        list.End();

        // Unfocused tree should not change its PaintVersion when compositor FocusVisible toggles.
        var focusVisibleProp = typeof(UICompositor).GetProperty("FocusVisible", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        focusVisibleProp.SetValue(compositor, false);
        long unfocusedOff = tree.Version;
        focusVisibleProp.SetValue(compositor, true);
        long unfocusedOn = tree.Version;
        Assert.True(unfocusedOff >= 0);
        Assert.Equal(unfocusedOff, unfocusedOn);

        // Focused idle tree with FocusVisible should still reuse (non-negative PaintVersion) and report FocusShownOn.
        tree.Focus();
        long focusedOn = tree.Version;
        Assert.True(focusedOn >= 0);
        Assert.Same(tree, compositor.FocusShownOn);

        // Changing TextScale with identical text and bounds must invalidate the cached truncated string.
        string fittedAtSmall = item.fitted;
        tree.TextScale = 1.2f;
        list.Begin(skin);
        tree.Draw(list);
        list.End();
        Assert.NotEqual(fittedAtSmall, item.fitted);
    }

    [Fact]
    public void Table_Rows_reset_clears_stale_order_and_CellBounds_shows_partially_clipped_right_column()
    {
        var (_, module, skin) = MakeHost();
        var table = module.AddComponent(new InspectableTable
        {
            Columns = [new("Name", 100), new("Value", 120)],
            Rows = [[new("a"), new("1")], [new("b"), new("2")], [new("c"), new("3")]],
            RowHeight = 24,
            Size = new Vector2(160, 140)
        });
        module.Root.MeasureTree(skin);
        module.Root.ArrangeTree(new UIRect(Vector2.Zero, new Vector2(160, 140)));

        Assert.Equal(3, table.RowOrder.Count);

        // Second column starts at X=100 and ends at X=220, partially clipped at Bounds.Max.X=160.
        UIRect? partial = table.CellBounds(0, 1);
        Assert.NotNull(partial);
        Assert.Equal(100.0f, partial.Value.Min.X, 2);
        Assert.Equal(160.0f, partial.Value.Max.X, 2);

        // Replacing Rows must immediately rebuild RowOrder without requiring a manual Refresh() call.
        table.Rows = [[new("only"), new("42")]];
        Assert.Equal([0], table.RowOrder);

        var list = new UIDrawList();
        list.Begin(skin);
        table.Draw(list);
        list.End();

        long v1 = table.Version;
        Assert.True(v1 >= 0);
        Assert.Equal(v1, table.Version);

        table.Rows[0][1].Text = "99";
        Assert.NotEqual(v1, table.Version);
    }

    [Fact]
    public void ListBox_Choices_matching_and_PaintVersion_work_without_substring_errors()
    {
        var (_, module, skin) = MakeHost();
        var listBox = module.AddComponent(new InspectableListBox
        {
            Items = ["alpha", "beta", "gamma"],
            Rows = 4,
            Size = new Vector2(260, 120)
        });
        module.Root.MeasureTree(skin);
        module.Root.ArrangeTree(new UIRect(Vector2.Zero, new Vector2(260, 120)));

        var draw = new UIDrawList();
        draw.Begin(skin);
        listBox.Draw(draw);
        draw.End();

        long v1 = listBox.Version;
        Assert.True(v1 >= 0);
        Assert.Equal(v1, listBox.Version);

        listBox.Index = 2;
        Assert.NotEqual(v1, listBox.Version);

        // Segmented choice matching parity
        var segmented = new ListBox
        {
            Items = ["row1"],
            Query = "main",
            Choice = _ => new UIChoice("Main.cs", static () => { })
            {
                Segments = [("src", UIChoiceTone.Dim), ("Main.cs", UIChoiceTone.Primary)]
            }
        };
        var spans = segmented.Matched("row1");
        Assert.Contains(spans, span => span.Part == 1 && span.Start == 0 && span.Length == 4);

        var drawChoice = new UIDrawList();
        drawChoice.Begin(skin);
        segmented.ArrangeTree(new UIRect(Vector2.Zero, new Vector2(260, 120)));
        var paint = typeof(ListBox).GetMethod("Paint", BindingFlags.Instance | BindingFlags.NonPublic)!;
        paint.Invoke(segmented, [drawChoice]);
        drawChoice.End();
        Assert.True(drawChoice.Items.Length > 0);
    }

    [Fact]
    public void TabStrip_PaintVersion_stays_stable_and_truncation_invalidates_on_scale_change()
    {
        var (_, module, skin) = MakeHost();
        var tab = new StripTab { Title = "VeryLongDocumentFileNameForTestingTruncation.cs" };
        var strip = module.AddComponent(new InspectableTabStrip
        {
            MaxTabWidth = 140.0f,
            TextScale = 0.4f,
            Selected = 0,
            Size = new Vector2(300, 32)
        });
        strip.Tabs.Add(tab);

        module.Root.MeasureTree(skin);
        module.Root.ArrangeTree(new UIRect(Vector2.Zero, new Vector2(300, 32)));

        var list = new UIDrawList();
        list.Begin(skin);
        strip.Draw(list);
        list.End();

        long v1 = strip.Version;
        long v2 = strip.Version;
        Assert.True(v1 >= 0);
        Assert.Equal(v1, v2);

        string fittedAtSmall = tab.fitted;
        strip.TextScale = 1.2f;
        list.Begin(skin);
        strip.Draw(list);
        list.End();
        Assert.NotEqual(fittedAtSmall, tab.fitted);
    }

    [Fact]
    public void TextBox_CaretAt_matches_prefix_measurement_and_Tail_invalidates_on_scale_change()
    {
        var (_, module, skin) = MakeHost();
        var box = module.AddComponent(new InspectableTextBox
        {
            Text = "Hello, Horizon UI 2026!",
            TextScale = 0.5f,
            Size = new Vector2(260, 32)
        });
        module.Root.MeasureTree(skin);
        module.Root.ArrangeTree(new UIRect(Vector2.Zero, new Vector2(260, 32)));

        var list = new UIDrawList();
        list.Begin(skin);
        box.Draw(list);
        list.End();

        var caretAt = typeof(TextBox).GetMethod("CaretAt", BindingFlags.Instance | BindingFlags.NonPublic)!;
        float textLeft = (float)typeof(TextBox).GetField("textLeft", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(box)!;
        float textScale = (float)typeof(TextBox).GetField("textScale", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(box)!;

        int ReferenceCaretAt(float x)
        {
            float wanted = x - textLeft;
            float before = 0.0f;
            for (int i = 1; i <= box.Text.Length; i++)
            {
                float after = skin.Font.Measure(box.Text.AsSpan(0, i), textScale, markup: false).X;
                if (wanted < (before + after) * 0.5f)
                    return i - 1;
                before = after;
            }
            return box.Text.Length;
        }

        for (float x = textLeft - 20.0f; x <= textLeft + 300.0f; x += 3.5f)
        {
            int actual = (int)caretAt.Invoke(box, [new Vector2(x, 16.0f)])!;
            int expected = ReferenceCaretAt(x);
            Assert.Equal(expected, actual);
        }

        // Tail truncation cache must invalidate when TextScale changes with the same Text and room.
        var tailMethod = typeof(TextBox).GetMethod("Tail", BindingFlags.Instance | BindingFlags.NonPublic)!;
        string smallTail = (string)tailMethod.Invoke(box, [skin, 0.4f, 80.0f])!;
        string largeTail = (string)tailMethod.Invoke(box, [skin, 1.2f, 80.0f])!;
        Assert.NotEqual(smallTail, largeTail);
    }

    [Fact]
    public void OnScreenKeyboard_Measure_caches_key_configuration_across_ticks()
    {
        var skin = MakeSkin();
        var keyboard = new OnScreenKeyboard(OnScreenKeyboard.Alphanumeric);

        keyboard.MeasureTree(skin);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 60; i++)
            keyboard.MeasureTree(skin);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Dropdown_handles_precise_trackpad_scroll_stops_momentum_at_ends_and_blocks_background_scroll()
    {
        var input = new Horizon.Input.InputManager();
        UIPointer pointer = default;
        var screen = new UIRect(Vector2.Zero, new Vector2(800.0f, 600.0f));
        var skin = MakeSkin();
        using var compositor = new UICompositor(new Camera2D(new Vector2(800.0f, 600.0f)))
        {
            InputOverride = input,
            ScreenToWorldOverride = static p => p,
            PointerSource = () => pointer,
        };

        var module = compositor.CreateModule();
        var scroll = module.AddComponent(new ScrollPanel { Size = new Vector2(400.0f, 300.0f) });
        var stack = scroll.Add(new StackPanel { Size = new Vector2(380.0f, 900.0f), Spacing = 10.0f });
        var dropdown = stack.Add(new Dropdown(Enumerable.Range(0, 20).Select(i => $"Option {i:D2}").ToArray())
        {
            Size = new Vector2(240.0f, 32.0f),
            MaxRows = 5,
            Index = 0,
        });
        stack.Add(new Panel { Size = new Vector2(240.0f, 700.0f) });

        compositor.RouteForTest(screen, skin, 1.0f / 60.0f);
        dropdown.Open();
        compositor.RouteForTest(screen, skin, 1.0f / 60.0f);

        Assert.True(dropdown.IsOpen);
        float scrollBefore = scroll.Offset;

        // Scrolling (discrete or precise) while the pointer is outside ListBounds must not scroll the ScrollPanel underneath the open popup.
        Vector2 outsideList = new(dropdown.ListBounds.Max.X + 40.0f, dropdown.Bounds.Center.Y);
        pointer = new UIPointer(outsideList, Down: false);
        compositor.Scroll(-2.0f);
        compositor.RouteForTest(screen, skin, 1.0f / 60.0f);
        Assert.Equal(scrollBefore, scroll.Offset);

        compositor.ScrollPrecisely(new Vector2(0.0f, -1.5f));
        compositor.RouteForTest(screen, skin, 1.0f / 60.0f);
        Assert.Equal(scrollBefore, scroll.Offset);

        // Precise trackpad scroll inside ListBounds moves the dropdown smoothly and stops gliding at its ends.
        pointer = new UIPointer(dropdown.ListBounds.Center, Down: false);
        compositor.ScrollPrecisely(new Vector2(0.0f, -1.5f));
        compositor.RouteForTest(screen, skin, 1.0f / 60.0f);
        Assert.True(dropdown.FirstVisible > 0);

        // Scroll past the bottom end during glide and confirm bounce/overscroll and end stop.
        UIWheel.Precise = true;
        UIWheel.Gliding = true;
        var onScroll = typeof(Dropdown).GetMethod("OnScroll", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (int i = 0; i < 40; i++)
            onScroll.Invoke(dropdown, [-2.0f]);
        Assert.Equal(15, dropdown.FirstVisible);
        Assert.True(dropdown.HasOverscroll);

        UIWheel.Gliding = false;
        UIWheel.Precise = false;
    }

    public static TheoryData<Type, string> HotPathMethods => new()
    {
        { typeof(TreeView), "Refresh" },
        { typeof(TreeView), "ForgetHidden" },
        { typeof(TreeView), "Paint" },
        { typeof(Table), "Refresh" },
        { typeof(Table), "Paint" },
        { typeof(ListBox), "Paint" },
        { typeof(ListBox), "PaintChoice" },
        { typeof(ListBox), "Write" },
        { typeof(TabStrip), "Paint" },
        { typeof(TextBox), "Paint" },
        { typeof(TextBox), "CaretAt" },
        { typeof(OnScreenKeyboard), "Measure" },
        { typeof(OnScreenKeyboard), "Select" },
        { typeof(ArrangeOverlay), "Paint" },
        { typeof(ArrangeOverlay), "Update" },
    };

    [Theory]
    [MemberData(nameof(HotPathMethods))]
    public void Modified_control_hot_paths_make_no_closure(Type type, string name)
    {
        MethodInfo[] methods = [.. type.GetMethods(ALL).Where(method => method.Name == name)];
        Assert.True(methods.Length > 0, $"{type.Name} has no method called {name}.");

        foreach (MethodInfo method in methods)
        {
            string? closure = ClosureMadeIn(method);
            Assert.True(closure is null, $"{type.Name}.{name} allocates closure {closure}.");
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
