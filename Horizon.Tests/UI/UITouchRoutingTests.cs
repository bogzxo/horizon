using System.Numerics;

using Horizon.Engine;
using Horizon.Input;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.Tests.UI;

// Fingers, a mouse and keys driven through the input queue and the compositor's own routing, the way a platform's
// events reach a UI, rather than by calling a component's handlers directly
[Collection("UI focus restoration")]
public sealed class UITouchRoutingTests
{
    private static readonly UISkin Skin = new(new UIFont(Path.GetTempPath(), "no-font-here.ttf"));
    private const float TICK = 1.0f / 60.0f;

    [Fact]
    public void Middle_click_scrolls_a_panel_and_second_click_stops_it()
    {
        using var rig = new Rig();
        var panel = rig.Module.AddComponent(new ScrollPanel { Size = new Vector2(300, 200) });
        panel.Add(new Panel { Size = new Vector2(280, 1000) });
        rig.Tick();
        Vector2 at = rig.At(panel);
        rig.Mouse(InputEventKind.PointerDown, at, MouseButton.Middle); rig.Tick();
        rig.Mouse(InputEventKind.PointerUp, at, MouseButton.Middle); rig.Tick();
        Assert.Same(panel, rig.Compositor.AutoScrollTarget);
        rig.Mouse(InputEventKind.PointerMove, at + new Vector2(0, 70)); rig.Tick(20);
        Assert.True(panel.Offset > 0);
        rig.Mouse(InputEventKind.PointerDown, at, MouseButton.Middle); rig.Tick();
        Assert.Null(rig.Compositor.AutoScrollTarget);
        float stopped = panel.Offset;
        rig.Mouse(InputEventKind.PointerUp, at, MouseButton.Middle); rig.Tick(10);
        Assert.Equal(stopped, panel.Offset);
    }

    [Fact]
    public void Middle_scroll_does_not_start_behind_an_unscrollable_front_panel()
    {
        using var rig = new Rig();
        var panel = rig.Module.AddComponent(new ScrollPanel { Size = new Vector2(300, 200) });
        panel.Add(new Panel { Size = new Vector2(280, 1000) });
        var front = rig.Compositor.CreateModule();
        front.AddComponent(new Button("Front") { Size = new Vector2(300, 200) });
        rig.Tick();
        rig.Mouse(InputEventKind.PointerDown, rig.At(panel), MouseButton.Middle); rig.Tick();
        Assert.Null(rig.Compositor.AutoScrollTarget);
        Assert.Equal(0, panel.Offset);
        rig.Compositor.RemoveModule(front);
    }

    private sealed class Rig : IDisposable
    {
        public readonly InputManager Input = new();
        public readonly Camera2D Camera = new(new Vector2(800, 600));
        public readonly UICompositor Compositor;
        public readonly UIModule Module;
        private readonly UIRect screen;

        public Rig()
        {
            Compositor = new UICompositor(Camera) { InputOverride = Input, ScreenToWorldOverride = ToWorld };
            Module = Compositor.CreateModule();
            screen = new UIRect(new Vector2(-400.0f, -300.0f), new Vector2(400.0f, 300.0f));
            UIKeyboard.Hook(Input);
            while (UIKeyboard.TryRead(out _)) { }
        }

        public void Tick(int ticks = 1)
        {
            for (int i = 0; i < ticks; i++)
            {
                Input.UpdateState(TICK);
                Compositor.RouteForTest(screen, Skin, TICK);
            }
        }

        // The window is 800 by 600 with the world's middle in its middle and Y going up, which is what the camera has
        private static Vector2 ToWorld(Vector2 screen) => new(screen.X - 400.0f, 300.0f - screen.Y);

        public Vector2 At(UIComponent component, Vector2 offset = default)
        {
            Vector2 world = component.Bounds.Center + offset;
            return new Vector2(world.X + 400.0f, 300.0f - world.Y);
        }

        public void Finger(InputEventKind kind, int id, Vector2 at) => Input.Post(InputEvent.Pointer(kind, at, PointerKind.Touch, id));

        public void Mouse(InputEventKind kind, Vector2 at, MouseButton button = MouseButton.Left) => Input.Post(InputEvent.Pointer(kind, at, PointerKind.Mouse, 0, button));

        public void Tap(int id, Vector2 at)
        {
            Finger(InputEventKind.PointerDown, id, at);
            Tick();
            Finger(InputEventKind.PointerUp, id, at);
            Tick(2);
        }

        public void Dispose()
        {
            Compositor.Focus = null;
            Compositor.RemoveModule(Module);
        }
    }

    private static (Button Button, Func<int> Clicks) AddButton(Rig rig, Vector2 at, string label = "Go")
    {
        int clicks = 0;
        var button = rig.Module.AddComponent(new Button(label) { Position = at, Size = new Vector2(160, 50) });
        button.OnPressed = () => clicks++;
        return (button, () => clicks);
    }

    [Fact]
    public void A_tap_clicks_once_and_leaves_nothing_hovered()
    {
        using var rig = new Rig();
        var (button, clicks) = AddButton(rig, Vector2.Zero);
        rig.Tick();

        rig.Finger(InputEventKind.PointerDown, 1, rig.At(button));
        rig.Tick();
        Assert.True(button.IsPressed);
        Assert.True(button.IsHovered);
        Assert.Equal(0, clicks());

        rig.Finger(InputEventKind.PointerUp, 1, rig.At(button));
        rig.Tick(3);
        Assert.Equal(1, clicks());
        Assert.False(button.IsPressed);
        Assert.False(button.IsHovered);
        Assert.Null(rig.Compositor.HoveredComponent);
    }

    [Fact]
    public void A_tap_quicker_than_a_tick_still_clicks_exactly_once()
    {
        using var rig = new Rig();
        var (button, clicks) = AddButton(rig, Vector2.Zero);
        rig.Tick();

        rig.Finger(InputEventKind.PointerDown, 7, rig.At(button));
        rig.Finger(InputEventKind.PointerUp, 7, rig.At(button));
        rig.Tick(4);
        Assert.Equal(1, clicks());
    }

    [Fact]
    public void A_cancelled_finger_never_clicks()
    {
        using var rig = new Rig();
        var (button, clicks) = AddButton(rig, Vector2.Zero);
        rig.Tick();

        rig.Finger(InputEventKind.PointerDown, 1, rig.At(button));
        rig.Tick();
        rig.Finger(InputEventKind.PointerCancel, 1, rig.At(button));
        rig.Tick(3);
        Assert.Equal(0, clicks());
        Assert.False(button.IsPressed);
        Assert.False(button.IsHovered);
    }

    [Fact]
    public void A_second_finger_neither_clicks_nor_steals_the_first_ones_press()
    {
        using var rig = new Rig();
        var (first, firstClicks) = AddButton(rig, new Vector2(-150, 0), "A");
        var (second, secondClicks) = AddButton(rig, new Vector2(150, 0), "B");
        rig.Tick();

        rig.Finger(InputEventKind.PointerDown, 1, rig.At(first));
        rig.Tick();
        rig.Finger(InputEventKind.PointerDown, 2, rig.At(second));
        rig.Tick();

        // Both are there for a game, by their own ids
        Assert.Equal(2, rig.Input.Touch.Count);
        Assert.True(rig.Input.Touch.TryGet(1, out _));
        Assert.True(rig.Input.Touch.TryGet(2, out _));
        Assert.True(first.IsPressed);
        Assert.False(second.IsPressed);

        rig.Finger(InputEventKind.PointerUp, 2, rig.At(second));
        rig.Tick(2);
        Assert.Equal(0, secondClicks());
        Assert.True(first.IsPressed);

        rig.Finger(InputEventKind.PointerUp, 1, rig.At(first));
        rig.Tick(2);
        Assert.Equal(1, firstClicks());
        Assert.Equal(0, secondClicks());
    }

    private static (ScrollPanel Scroll, Button Row, Func<int> Clicks, Slider Slider) AddScrollList(Rig rig)
    {
        var scroll = rig.Module.AddComponent(new ScrollPanel { Position = Vector2.Zero, Size = new Vector2(300, 200) });
        var stack = scroll.Add(new StackPanel());
        int clicks = 0;
        Button? row = null;
        Slider? slider = null;
        for (int i = 0; i < 12; i++)
        {
            if (i == 1)
            {
                slider = stack.Add(new Slider { Size = new Vector2(260, 40), Value = 0.5f });
                continue;
            }

            var button = stack.Add(new Button($"Row {i}") { Size = new Vector2(260, 40) });
            button.OnPressed = () => clicks++;
            row ??= button;
        }

        return (scroll, row!, () => clicks, slider!);
    }

    [Fact]
    public void A_finger_dragged_over_a_list_scrolls_it_and_clicks_nothing()
    {
        using var rig = new Rig();
        var (scroll, row, clicks, _) = AddScrollList(rig);
        rig.Tick(2);
        Assert.True(scroll.MaxOffset > 0.0f);

        Vector2 start = rig.At(row);
        rig.Finger(InputEventKind.PointerDown, 1, start);
        rig.Tick();

        // Up the screen, which is content moving up, which is further down the list
        for (int step = 1; step <= 8; step++)
        {
            rig.Finger(InputEventKind.PointerMove, 1, start - new Vector2(0, step * 10));
            rig.Tick();
        }

        Assert.True(rig.Compositor.IsTouchPanning);
        Assert.False(row.IsPressed);
        float scrolled = scroll.Offset;
        Assert.True(scrolled > 40.0f, $"scrolled {scrolled}");

        rig.Finger(InputEventKind.PointerUp, 1, start - new Vector2(0, 80));
        rig.Tick(3);
        Assert.Equal(0, clicks());
        Assert.False(rig.Compositor.IsTouchPanning);
        Assert.Null(rig.Compositor.HoveredComponent);
    }

    [Fact]
    public void A_wobble_inside_the_slop_is_still_a_tap_on_a_row()
    {
        using var rig = new Rig();
        var (scroll, row, clicks, _) = AddScrollList(rig);
        rig.Tick(2);

        Vector2 start = rig.At(row);
        rig.Finger(InputEventKind.PointerDown, 1, start);
        rig.Tick();
        rig.Finger(InputEventKind.PointerMove, 1, start + new Vector2(3, -4));
        rig.Tick();
        rig.Finger(InputEventKind.PointerUp, 1, start + new Vector2(3, -4));
        rig.Tick(2);
        Assert.Equal(1, clicks());
        Assert.Equal(0.0f, scroll.Offset);
    }

    [Fact]
    public void A_slider_in_a_list_keeps_its_drag()
    {
        using var rig = new Rig();
        var (scroll, _, _, slider) = AddScrollList(rig);
        rig.Tick(2);

        Vector2 start = rig.At(slider);
        rig.Finger(InputEventKind.PointerDown, 1, start);
        rig.Tick();
        for (int step = 1; step <= 6; step++)
        {
            rig.Finger(InputEventKind.PointerMove, 1, start + new Vector2(step * 15, -step * 12));
            rig.Tick();
        }
        rig.Finger(InputEventKind.PointerUp, 1, start + new Vector2(90, -72));
        rig.Tick(2);

        Assert.True(slider.Value > 0.6f, $"value {slider.Value}");
        Assert.Equal(0.0f, scroll.Offset);
    }

    [Fact]
    public void A_list_at_its_end_hands_the_drag_to_the_list_it_is_in()
    {
        using var rig = new Rig();
        var outer = rig.Module.AddComponent(new ScrollPanel { Position = Vector2.Zero, Size = new Vector2(320, 220) });
        var outerStack = outer.Add(new StackPanel());
        var inner = outerStack.Add(new ScrollPanel { Size = new Vector2(280, 120) });
        var innerStack = inner.Add(new StackPanel());
        for (int i = 0; i < 4; i++) innerStack.Add(new Button($"Inner {i}") { Size = new Vector2(240, 40) });
        for (int i = 0; i < 8; i++) outerStack.Add(new Button($"Outer {i}") { Size = new Vector2(280, 40) });
        rig.Tick(2);
        Assert.True(inner.MaxOffset > 0.0f);
        Assert.True(outer.MaxOffset > 0.0f);

        inner.Offset = inner.MaxOffset;
        rig.Tick();
        Vector2 start = rig.At(inner);
        rig.Finger(InputEventKind.PointerDown, 1, start);
        rig.Tick();
        for (int step = 1; step <= 6; step++)
        {
            rig.Finger(InputEventKind.PointerMove, 1, start - new Vector2(0, step * 12));
            rig.Tick();
        }
        rig.Finger(InputEventKind.PointerUp, 1, start - new Vector2(0, 72));
        rig.Tick();

        Assert.Equal(inner.MaxOffset, inner.Offset, 0.01f);
        Assert.True(outer.Offset > 20.0f, $"outer {outer.Offset}");
    }

    [Fact]
    public void A_flicked_list_carries_on_after_the_finger_lifts()
    {
        using var rig = new Rig();
        var (scroll, row, _, _) = AddScrollList(rig);
        rig.Tick(2);

        Vector2 start = rig.At(row);
        rig.Finger(InputEventKind.PointerDown, 1, start);
        rig.Tick();
        for (int step = 1; step <= 4; step++)
        {
            rig.Finger(InputEventKind.PointerMove, 1, start - new Vector2(0, step * 20));
            rig.Tick();
        }
        rig.Finger(InputEventKind.PointerUp, 1, start - new Vector2(0, 80));
        rig.Tick();
        float lifted = scroll.Offset;
        rig.Tick(10);
        Assert.True(scroll.Offset > lifted, $"{scroll.Offset} after {lifted}");
    }

    [Fact]
    public void A_long_press_opens_the_context_menu_and_does_not_click()
    {
        using var rig = new Rig();
        var (button, clicks) = AddButton(rig, Vector2.Zero);
        int menus = 0;
        button.OnContextMenu = _ => menus++;
        rig.Tick();

        rig.Finger(InputEventKind.PointerDown, 1, rig.At(button));
        rig.Tick((int)(rig.Compositor.LongPressTime / TICK) + 3);
        Assert.Equal(1, menus);
        Assert.False(button.IsPressed);

        rig.Finger(InputEventKind.PointerUp, 1, rig.At(button));
        rig.Tick(2);
        Assert.Equal(1, menus);
        Assert.Equal(0, clicks());
    }

    [Fact]
    public void A_long_press_with_nothing_to_offer_is_a_held_click()
    {
        using var rig = new Rig();
        var (button, clicks) = AddButton(rig, Vector2.Zero);
        rig.Tick();

        rig.Finger(InputEventKind.PointerDown, 1, rig.At(button));
        rig.Tick((int)(rig.Compositor.LongPressTime / TICK) + 3);
        rig.Finger(InputEventKind.PointerUp, 1, rig.At(button));
        rig.Tick(2);
        Assert.Equal(1, clicks());
    }

    [Fact]
    public void A_tapped_text_box_takes_the_focus_on_release_asks_for_the_soft_keyboard_and_types()
    {
        using var rig = new Rig();
        var box = rig.Module.AddComponent(new TextBox { Position = new Vector2(0, 100), Size = new Vector2(240, 40) });
        var (button, _) = AddButton(rig, new Vector2(0, -100));
        rig.Tick();

        rig.Finger(InputEventKind.PointerDown, 1, rig.At(box));
        rig.Tick();
        Assert.NotEqual(box, rig.Compositor.Focus);
        rig.Finger(InputEventKind.PointerUp, 1, rig.At(box));
        rig.Tick();
        Assert.Equal(box, rig.Compositor.Focus);
        Assert.True(rig.Input.TextInputActive);

        rig.Input.Post(InputEvent.Typed("hi!"));
        rig.Tick();
        rig.Input.Post(InputEvent.KeyChange(Key.Backspace, true));
        rig.Input.Post(InputEvent.KeyChange(Key.Backspace, false));
        rig.Tick(2);
        Assert.Equal("hi", box.Text);

        rig.Tap(2, rig.At(button));
        Assert.NotEqual(box, rig.Compositor.Focus);
        Assert.False(rig.Input.TextInputActive);
    }

    [Fact]
    public void A_mouse_clicked_text_box_needs_no_soft_keyboard()
    {
        using var rig = new Rig();
        var box = rig.Module.AddComponent(new TextBox { Position = Vector2.Zero, Size = new Vector2(240, 40) });
        rig.Tick();

        rig.Mouse(InputEventKind.PointerMove, rig.At(box));
        rig.Mouse(InputEventKind.PointerDown, rig.At(box));
        rig.Tick();
        Assert.Equal(box, rig.Compositor.Focus);
        rig.Mouse(InputEventKind.PointerUp, rig.At(box));
        rig.Tick();
        Assert.False(rig.Input.TextInputActive);
    }

    [Fact]
    public void The_mouse_hovers_again_once_it_moves_after_a_finger()
    {
        using var rig = new Rig();
        var (left, leftClicks) = AddButton(rig, new Vector2(-150, 0), "Left");
        var (right, rightClicks) = AddButton(rig, new Vector2(150, 0), "Right");
        rig.Tick();

        rig.Mouse(InputEventKind.PointerMove, rig.At(left));
        rig.Tick();
        Assert.True(left.IsHovered);

        rig.Tap(1, rig.At(right));
        Assert.Equal(1, rightClicks());
        Assert.False(left.IsHovered);
        Assert.False(right.IsHovered);

        // The mouse hasn't moved, so it isn't over anything as far as the UI is concerned until it does
        rig.Tick();
        Assert.Null(rig.Compositor.HoveredComponent);

        rig.Mouse(InputEventKind.PointerMove, rig.At(left, new Vector2(2, 0)));
        rig.Tick();
        Assert.True(left.IsHovered);
        Assert.Equal(PointerKind.Mouse, rig.Compositor.Pointer.Kind);

        rig.Mouse(InputEventKind.PointerDown, rig.At(left));
        rig.Tick();
        rig.Mouse(InputEventKind.PointerUp, rig.At(left));
        rig.Tick();
        Assert.Equal(1, leftClicks());
    }

    [Fact]
    public void A_finger_waits_while_the_mouse_is_dragging()
    {
        using var rig = new Rig();
        var (left, leftClicks) = AddButton(rig, new Vector2(-150, 0), "Left");
        var (right, rightClicks) = AddButton(rig, new Vector2(150, 0), "Right");
        rig.Tick();

        rig.Mouse(InputEventKind.PointerMove, rig.At(left));
        rig.Mouse(InputEventKind.PointerDown, rig.At(left));
        rig.Tick();
        rig.Finger(InputEventKind.PointerDown, 1, rig.At(right));
        rig.Tick();
        rig.Finger(InputEventKind.PointerUp, 1, rig.At(right));
        rig.Tick();
        Assert.True(left.IsPressed);
        Assert.Equal(0, rightClicks());

        rig.Mouse(InputEventKind.PointerUp, rig.At(left));
        rig.Tick();
        Assert.Equal(1, leftClicks());
        Assert.Equal(0, rightClicks());
    }

    [Fact]
    public void The_right_mouse_button_and_the_wheel_still_route()
    {
        using var rig = new Rig();
        var (scroll, row, _, _) = AddScrollList(rig);
        int menus = 0;
        row.OnContextMenu = _ => menus++;
        rig.Tick(2);

        rig.Mouse(InputEventKind.PointerMove, rig.At(row));
        rig.Mouse(InputEventKind.PointerDown, rig.At(row), MouseButton.Right);
        rig.Tick();
        rig.Mouse(InputEventKind.PointerUp, rig.At(row), MouseButton.Right);
        rig.Tick();
        Assert.Equal(1, menus);

        rig.Input.Post(new InputEvent { Kind = InputEventKind.Scroll, ScrollY = -1.0f, PointerKind = PointerKind.Mouse });
        rig.Tick(30);
        Assert.True(scroll.Offset > 0.0f);
    }
}
