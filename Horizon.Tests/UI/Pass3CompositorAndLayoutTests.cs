using System.Numerics;
using System.Reflection;

using Horizon.Engine;
using Horizon.Input;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.Tests.UI;

[Collection("UI focus restoration")]
public sealed class Pass3CompositorAndLayoutTests
{
    private static readonly UISkin Skin = new(new UIFont(Path.GetTempPath(), "no-font-here.ttf"));
    private static readonly UIRect Screen = new(Vector2.Zero, new Vector2(800.0f, 600.0f));
    private const float TICK = 1.0f / 60.0f;

    private static UICompositor MakeCompositor(InputManager input, Func<UIPointer>? pointerSource = null)
    {
        var compositor = new UICompositor(new Camera2D(new Vector2(800.0f, 600.0f)))
        {
            InputOverride = input,
            ScreenToWorldOverride = static p => p,
            PointerSource = pointerSource
        };
        return compositor;
    }

    private sealed class TestPopupOwner : UIComponent
    {
        public UIRect PopupArea { get; set; }
        public int Clicks { get; private set; }

        protected override bool HitTestVisible => true;
        protected internal override bool PopupContains(Vector2 point) => PopupArea.Contains(point);
        protected internal override void OnClick() => Clicks++;

        public void ShowPopup() => OpenPopup();
    }

    private sealed class VersionedResizableBox : UIComponent
    {
        public long Version { get; set; } = 1;
        public int PaintCalls { get; private set; }

        protected override bool HitTestVisible => true;
        protected override long PaintVersion => Version;

        protected override void Paint(UIDrawList list)
        {
            PaintCalls++;
            list.Rect(Bounds, Vector4.One);
        }
    }

    [Fact]
    public void Removing_a_module_clears_focus_hover_pressed_and_middle_scroll_state()
    {
        var input = new InputManager();
        UIPointer pointer = default;
        using var compositor = MakeCompositor(input, () => pointer);
        var module = compositor.CreateModule();
        var scroll = module.AddComponent(new ScrollPanel { Size = new Vector2(300.0f, 200.0f) });
        var button = scroll.Add(new Button("Action") { Size = new Vector2(120.0f, 40.0f) });

        // First tick lays out and hovers/presses the button, then focuses it
        compositor.RouteForTest(Screen, Skin, TICK);
        Vector2 center = button.Bounds.Center;
        pointer = new UIPointer(center, Down: true);
        compositor.RouteForTest(Screen, Skin, TICK);
        button.Focus();

        Assert.Same(button, compositor.Focus);
        Assert.Same(button, compositor.HoveredComponent);
        Assert.True(button.IsHovered);
        Assert.True(button.IsPressed);

        // Start middle-scroll on the scroll panel
        pointer = new UIPointer(center, Down: false);
        compositor.RouteForTest(Screen, Skin, TICK);
        pointer = new UIPointer(center, Down: false, MiddleDown: true);
        compositor.RouteForTest(Screen, Skin, TICK);
        Assert.Same(scroll, compositor.AutoScrollTarget);

        // Re-press and focus before removing the module
        pointer = new UIPointer(center, Down: true, MiddleDown: true);
        compositor.RouteForTest(Screen, Skin, TICK);
        button.Focus();

        Assert.True(compositor.RemoveModule(module));
        Assert.Null(compositor.Focus);
        Assert.Null(compositor.HoveredComponent);
        Assert.Null(compositor.AutoScrollTarget);
        Assert.False(button.IsHovered);
        Assert.False(button.IsPressed);
    }

    [Fact]
    public void Hiding_disabling_or_detaching_a_component_clears_focus_hover_and_pressed()
    {
        var input = new InputManager();
        UIPointer pointer = default;
        using var compositor = MakeCompositor(input, () => pointer);
        var module = compositor.CreateModule();
        var box = module.AddComponent(new TextBox { Size = new Vector2(200.0f, 36.0f) });

        compositor.RouteForTest(Screen, Skin, TICK);
        Vector2 center = box.Bounds.Center;

        // 1. Visible = false clears focus, hover, and press
        pointer = new UIPointer(center, Down: true);
        compositor.RouteForTest(Screen, Skin, TICK);
        Assert.Same(box, compositor.Focus);
        Assert.Same(box, compositor.HoveredComponent);
        Assert.True(box.IsPressed);

        box.Visible = false;
        compositor.RouteForTest(Screen, Skin, TICK);
        Assert.Null(compositor.Focus);
        Assert.Null(compositor.HoveredComponent);
        Assert.False(box.IsHovered);
        Assert.False(box.IsPressed);

        // 2. Enabled = false clears focus, hover, and press
        box.Visible = true;
        pointer = new UIPointer(center, Down: false);
        compositor.RouteForTest(Screen, Skin, TICK);
        pointer = new UIPointer(center, Down: true);
        compositor.RouteForTest(Screen, Skin, TICK);
        Assert.Same(box, compositor.Focus);
        Assert.Same(box, compositor.HoveredComponent);
        Assert.True(box.IsPressed);

        box.Enabled = false;
        compositor.RouteForTest(Screen, Skin, TICK);
        Assert.Null(compositor.Focus);
        Assert.Null(compositor.HoveredComponent);
        Assert.False(box.IsHovered);
        Assert.False(box.IsPressed);

        // 3. Detaching from parent clears focus, hover, and press
        box.Enabled = true;
        pointer = new UIPointer(center, Down: false);
        compositor.RouteForTest(Screen, Skin, TICK);
        pointer = new UIPointer(center, Down: true);
        compositor.RouteForTest(Screen, Skin, TICK);
        Assert.Same(box, compositor.Focus);
        Assert.Same(box, compositor.HoveredComponent);
        Assert.True(box.IsPressed);

        Assert.True(module.RemoveComponent(box));
        Assert.Null(box.Module);
        compositor.RouteForTest(Screen, Skin, TICK);
        Assert.Null(compositor.Focus);
        Assert.Null(compositor.HoveredComponent);
        Assert.False(box.IsHovered);
        Assert.False(box.IsPressed);
    }

    [Fact]
    public void Switching_tab_panel_page_clears_focus_hover_and_press_on_hidden_tab()
    {
        var input = new InputManager();
        UIPointer pointer = default;
        using var compositor = MakeCompositor(input, () => pointer);
        var module = compositor.CreateModule();
        var tabs = module.AddComponent(new TabPanel { Size = new Vector2(400.0f, 300.0f), Tabs = ["One", "Two"] });
        var page1 = tabs.Add(new Panel { Fill = UIFill.Both });
        var page2 = tabs.Add(new Panel { Fill = UIFill.Both });
        var input1 = page1.Add(new TextBox { Size = new Vector2(180.0f, 36.0f) });
        _ = page2.Add(new TextBox { Size = new Vector2(180.0f, 36.0f) });

        compositor.RouteForTest(Screen, Skin, TICK);
        Vector2 center = input1.Bounds.Center;
        pointer = new UIPointer(center, Down: true);
        compositor.RouteForTest(Screen, Skin, TICK);

        Assert.Same(input1, compositor.Focus);
        Assert.Same(input1, compositor.HoveredComponent);
        Assert.True(input1.IsPressed);

        tabs.Selected = 1;
        compositor.RouteForTest(Screen, Skin, TICK);

        Assert.Null(compositor.Focus);
        Assert.NotSame(input1, compositor.HoveredComponent);
        Assert.False(input1.IsHovered);
        Assert.False(input1.IsPressed);
    }

    [Fact]
    public void Invisible_or_disabled_popup_does_not_intercept_hit_testing_or_clicks()
    {
        var input = new InputManager();
        UIPointer pointer = default;
        using var compositor = MakeCompositor(input, () => pointer);
        var module = compositor.CreateModule();

        int behindClicks = 0;
        var behind = module.AddComponent(new Button("Behind")
        {
            Size = new Vector2(200.0f, 80.0f),
            OnPressed = () => behindClicks++
        });

        var owner = module.AddComponent(new TestPopupOwner
        {
            Size = new Vector2(40.0f, 40.0f),
            Position = new Vector2(-250.0f, 0.0f)
        });

        compositor.RouteForTest(Screen, Skin, TICK);
        owner.PopupArea = behind.Bounds;
        owner.ShowPopup();

        // Active visible & enabled popup intercepts hit-testing
        Assert.Same(owner, module.HitTest(behind.Bounds.Center));

        // Hidden popup must NOT intercept hit-testing or block clicks on underlying button
        owner.Visible = false;
        Assert.Same(behind, module.HitTest(behind.Bounds.Center));

        pointer = new UIPointer(behind.Bounds.Center, Down: true);
        compositor.RouteForTest(Screen, Skin, TICK);
        pointer = new UIPointer(behind.Bounds.Center, Down: false);
        compositor.RouteForTest(Screen, Skin, TICK);
        Assert.Equal(1, behindClicks);

        // Re-open popup and disable it: disabled popup must NOT intercept hit-testing
        owner.Visible = true;
        owner.ShowPopup();
        owner.Enabled = false;
        Assert.Same(behind, module.HitTest(behind.Bounds.Center));
    }

    [Fact]
    public void Disposing_compositor_unsubscribes_composition_events_and_clears_references()
    {
        var input = new InputManager();
        var compositor = MakeCompositor(input);
        var module = compositor.CreateModule();
        var box = module.AddComponent(new TextBox { Size = new Vector2(200.0f, 36.0f) });
        box.Focus();

        // Hook composition via a keyboard routing pass
        compositor.RouteForTest(Screen, Skin, TICK);
        Assert.Same(box, compositor.Focus);

        FieldInfo eventField = typeof(InputManager).GetField("EventReceived", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Delegate? beforeDispose = (Delegate?)eventField.GetValue(input);
        Assert.NotNull(beforeDispose);
        Assert.Contains(beforeDispose.GetInvocationList(), d => ReferenceEquals(d.Target, compositor));

        compositor.Dispose();

        Assert.Null(compositor.Focus);
        Assert.Null(compositor.HoveredComponent);
        Assert.Null(compositor.AutoScrollTarget);

        Delegate? afterDispose = (Delegate?)eventField.GetValue(input);
        if (afterDispose is not null)
            Assert.DoesNotContain(afterDispose.GetInvocationList(), d => ReferenceEquals(d.Target, compositor));

        // Posting a composition event after disposal must not throw or re-trigger the disposed compositor
        input.Post(InputEvent.Composition("abc", 3, 0));

        // Disposing twice is idempotent
        compositor.Dispose();
    }

    [Fact]
    public void Resize_grip_and_dragging_state_invalidate_cached_paint_version_without_geometry_change()
    {
        var box = new VersionedResizableBox
        {
            Size = new Vector2(200.0f, 120.0f),
            Resizable = UIResize.Right | UIResize.Bottom
        };

        var a = new UIDrawList();
        var b = new UIDrawList();

        void PaintTick(UIDrawList into, UIDrawList? previous, int tick)
        {
            box.MeasureTree(Skin);
            box.ArrangeTree(new UIRect(Vector2.Zero, new Vector2(200.0f, 120.0f)));
            into.Previous = previous;
            into.Begin(Skin, tick, TICK, tick * TICK);
            box.PaintTree(into);
            into.End();
        }

        // Tick 1 records, Tick 2 reuses with identical geometry and grip state
        PaintTick(a, null, 1);
        Assert.Equal(1, box.PaintCalls);

        PaintTick(b, a, 2);
        Assert.Equal(1, b.Reused);
        Assert.Equal(1, box.PaintCalls);

        // Hovering a resize grip changes ResizePaintStamp without changing Bounds -> must repaint
        int stampIdle = box.ResizePaintStamp;
        box.ActiveGrip = UIResize.Right;
        Assert.NotEqual(stampIdle, box.ResizePaintStamp);

        PaintTick(a, b, 3);
        Assert.Equal(0, a.Reused);
        Assert.Equal(2, box.PaintCalls);

        // Starting a drag (IsResizing = true) on the same grip changes ResizePaintStamp -> must repaint
        int stampHovered = box.ResizePaintStamp;
        box.BeginResize(UIResize.Right, new Vector2(200.0f, 60.0f));
        Assert.NotEqual(stampHovered, box.ResizePaintStamp);

        PaintTick(b, a, 4);
        Assert.Equal(0, b.Reused);
        Assert.Equal(3, box.PaintCalls);

        // Ending resize clears IsResizing and ActiveGrip -> must repaint back to idle chrome
        box.EndResize();
        Assert.Equal(stampIdle, box.ResizePaintStamp);

        PaintTick(a, b, 5);
        Assert.Equal(0, a.Reused);
        Assert.Equal(4, box.PaintCalls);
    }

    [Fact]
    public void Cached_module_reference_propagates_across_add_reparent_and_remove()
    {
        using var compositor = new UICompositor(new Camera2D(new Vector2(800.0f, 600.0f)));
        var first = compositor.CreateModule();
        var second = compositor.CreateModule();

        // Build a nested subtree before attaching it to any module
        var group = new Panel();
        var inner = group.Add(new StackPanel());
        var leaf = inner.Add(new Label("Leaf"));

        Assert.Null(group.Module);
        Assert.Null(inner.Module);
        Assert.Null(leaf.Module);

        // Attaching root of subtree propagates module down to every descendant
        first.AddComponent(group);
        Assert.Same(first, group.Module);
        Assert.Same(first, inner.Module);
        Assert.Same(first, leaf.Module);

        // Reparenting into another module updates the entire subtree
        second.AddComponent(group);
        Assert.Same(second, group.Module);
        Assert.Same(second, inner.Module);
        Assert.Same(second, leaf.Module);

        // Removing clears module across the subtree
        Assert.True(second.RemoveComponent(group));
        Assert.Null(group.Module);
        Assert.Null(inner.Module);
        Assert.Null(leaf.Module);
    }

    [Fact]
    public void Layout_and_tween_channel_replacement_edge_cases_behave_cleanly()
    {
        UIRect rect = new(new Vector2(10.0f, 20.0f), new Vector2(110.0f, 120.0f));
        UIRect shrunk = rect.Shrink(new UIEdges(5.0f, 10.0f, 15.0f, 20.0f));
        Assert.Equal(new Vector2(15.0f, 40.0f), shrunk.Min);
        Assert.Equal(new Vector2(95.0f, 110.0f), shrunk.Max);

        var box = new Panel { Size = new Vector2(100.0f, 50.0f) };
        box.TweenOffset(new Vector2(50.0f, 0.0f), 1.0f);
        // Replacing the same channel starts cleanly toward the new target
        box.TweenOffset(new Vector2(0.0f, 20.0f), 0.1f);
        box.UpdateTree(0.2f);
        Assert.Equal(new Vector2(0.0f, 20.0f), box.VisualOffset);

        box.PopIn(duration: 0.1f);
        box.UpdateTree(0.2f);
        Assert.Equal(1.0f, box.Opacity, 3);
        Assert.Equal(Vector2.One, box.VisualScale);

        var shortcuts = new UIShortcuts();
        var stroke = new UIKeyStroke(Key.K, Control: true, Shift: false, Alt: false);
        var second = new UIKeyStroke(Key.Z, Control: false, Shift: false, Alt: false);
        Assert.False(shortcuts.Has(stroke, second));
        shortcuts.Add(Key.K, static () => { }, control: true, then: second);
        Assert.True(shortcuts.Has(stroke, second));
    }

    private const BindingFlags ALL = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private const byte NEWOBJ = 0x73;

    public static TheoryData<Type, string> ModifiedHotPaths => new()
    {
        { typeof(UICompositor), "RoutePointer" },
        { typeof(UICompositor), "RouteKeyboard" },
        { typeof(UICompositor), "PaintPending" },
        { typeof(UICompositor), "PrunePointerReferences" },
        { typeof(UICompositor), "HasOpenDialog" },
        { typeof(UIComponent), "MeasureTree" },
        { typeof(UIComponent), "MeasureTreeUntimed" },
        { typeof(UIComponent), "PaintTree" },
        { typeof(UIComponent), "UpdateTree" },
        { typeof(UIModule), "HitTest" },
        { typeof(UIModule), "Paint" },
        { typeof(UIModule), "CollectFocusable" },
        { typeof(UINavigator), "Collect" },
        { typeof(UIShortcuts), "Has" },
        { typeof(UIShortcuts), "Update" },
    };

    [Theory]
    [MemberData(nameof(ModifiedHotPaths))]
    public void Modified_hot_paths_make_no_closure(Type type, string name)
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
