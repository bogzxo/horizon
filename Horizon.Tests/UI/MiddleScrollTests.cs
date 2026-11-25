using System.Diagnostics;
using System.Numerics;

using Horizon.Core;
using Horizon.Engine;
using Horizon.Input;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.Tests.UI;

[Collection("UI focus restoration")]
public class MiddleScrollTests
{
    private static readonly UISkin Skin = new(new UIFont(Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", "cascadia_mono"), "cascadia_mono.ttf"));
    private const float TICK = 1.0f / 60.0f;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Speed_can_change_during_held_and_latched_scrolling_without_changing_the_dead_zone(bool held)
    {
        var gesture = new CodeEditor.MiddleScrollGesture();
        gesture.Step(Vector2.Zero, true, true, false, 0.02f);
        if (!held) gesture.Step(Vector2.Zero, false, true, false, 0.02f);
        Vector2 Move(float distance, float speed) => gesture.Step(new(0, -distance), held, true, false, 0.02f, speed);
        float normal = Move(60, 1).Y;
        Assert.True(normal > 0);
        Assert.Equal(normal * 0.25f, Move(60, 0.25f).Y, 4);
        Assert.Equal(normal * 2, Move(60, 2).Y, 4);
        Assert.Equal(-normal * 5, Move(-60, 5).Y, 4);
        Assert.Equal(Vector2.Zero, Move(5, 5));
        Assert.Equal(normal, Move(60, float.NaN).Y);
        Assert.Equal(normal * 0.25f, Move(60, -1).Y, 4);
        Assert.True(gesture.Active);
    }

    [Fact]
    public void A_tap_latches_and_distance_controls_speed_in_both_directions()
    {
        var gesture = new CodeEditor.MiddleScrollGesture();
        gesture.Step(Vector2.Zero, true, true, false, 0.02f);
        gesture.Step(Vector2.Zero, false, true, false, 0.02f);
        Assert.True(gesture.Active);
        Assert.Equal(Vector2.Zero, gesture.Step(new(0, 5), false, true, false, 0.02f));
        float down = gesture.Step(new(0, -40), false, true, false, 0.02f).Y;
        float faster = gesture.Step(new(0, -80), false, true, false, 0.02f).Y;
        float up = gesture.Step(new(0, 40), false, true, false, 0.02f).Y;
        Assert.True(down > 0 && faster > down);
        Assert.Equal(-down, up);
        gesture.Step(new(0, 40), true, true, false, 0.02f);
        Assert.False(gesture.Active);
    }

    [Fact]
    public void Holding_or_dragging_stops_on_release_and_cancellation_never_restarts_a_held_button()
    {
        var gesture = new CodeEditor.MiddleScrollGesture();
        gesture.Step(Vector2.Zero, true, true, false, 0.02f);
        Assert.True(gesture.Step(new(0, -60), true, true, false, 0.02f).Y > 0);
        gesture.Step(new(0, -60), false, true, false, 0.02f);
        Assert.False(gesture.Active);
        gesture.Step(Vector2.Zero, true, true, false, 0.3f);
        gesture.Step(Vector2.Zero, false, true, false, 0.02f);
        Assert.False(gesture.Active);
        gesture.Step(Vector2.Zero, true, true, false, 0.02f);
        gesture.Step(Vector2.Zero, true, true, true, 0.02f);
        gesture.Step(Vector2.Zero, true, true, false, 0.02f);
        Assert.False(gesture.Active);
    }

    [Fact]
    public void Two_axis_gesture_has_per_axis_deadzone_diagonal_motion_and_horizontal_drag_unlatch()
    {
        var gesture = new CodeEditor.MiddleScrollGesture();
        // Quick click within 8 px latches
        gesture.Step(Vector2.Zero, true, true, false, 0.02f);
        gesture.Step(new(4, -4), false, true, false, 0.02f);
        Assert.True(gesture.Active);

        // Inside 8 px on both axes produces zero motion
        Assert.Equal(Vector2.Zero, gesture.Step(new(8, -8), false, true, false, 0.02f));

        // Pure vertical displacement with small horizontal wobble (< 8 px) has zero horizontal drift
        Vector2 vertOnly = gesture.Step(new(6, -50), false, true, false, 0.02f);
        Assert.Equal(0.0f, vertOnly.X);
        Assert.True(vertOnly.Y > 0.0f);

        // Pure horizontal displacement with small vertical wobble (< 8 px) has zero vertical drift
        Vector2 rightOnly = gesture.Step(new(50, -6), false, true, false, 0.02f);
        Assert.True(rightOnly.X > 0.0f);
        Assert.Equal(0.0f, rightOnly.Y);

        Vector2 leftOnly = gesture.Step(new(-50, 6), false, true, false, 0.02f);
        Assert.Equal(-rightOnly.X, leftOnly.X, 4);
        Assert.Equal(0.0f, leftOnly.Y);

        // Diagonal displacement moves both axes simultaneously
        Vector2 diag = gesture.Step(new(50, -50), false, true, false, 0.02f);
        Assert.Equal(rightOnly.X, diag.X, 4);
        Assert.Equal(vertOnly.Y, diag.Y, 4);

        // Click again to cancel
        gesture.Step(Vector2.Zero, true, true, false, 0.02f);
        Assert.False(gesture.Active);
        gesture.Step(Vector2.Zero, false, true, false, 0.02f);

        // Horizontal hold-and-drag beyond 8 px unlatches on release
        gesture.Step(Vector2.Zero, true, true, false, 0.02f);
        Vector2 dragged = gesture.Step(new(40, 0), true, true, false, 0.02f);
        Assert.True(dragged.X > 0.0f);
        Assert.Equal(0.0f, dragged.Y);
        gesture.Step(new(40, 0), false, true, false, 0.02f);
        Assert.False(gesture.Active);
    }

    [Fact]
    public void Code_editor_middle_scroll_moves_horizontally_and_vertically_when_unwrapped()
    {
        using var rig = new Rig();
        string longLine = "0123456789" + new string('a', 260);
        var editor = new CodeEditor(string.Join('\n', Enumerable.Repeat(longLine, 120)))
        {
            Size = new Vector2(360, 200),
            WordWrap = false
        };
        rig.Module.AddComponent(editor);
        rig.TickAndPaint();

        Vector2 at = rig.At(editor);
        rig.Mouse(InputEventKind.PointerMove, at);
        rig.TickAndPaint();

        // Latch middle-click autoscroll
        rig.Mouse(InputEventKind.PointerDown, at, MouseButton.Middle);
        rig.TickAndPaint();
        rig.Mouse(InputEventKind.PointerUp, at, MouseButton.Middle);
        rig.TickAndPaint();
        Assert.True(editor.AutoScrolling);

        // Move purely right on screen -> scrolls right (ScrollXPixels increases), ScrollPixels stays 0
        rig.Mouse(InputEventKind.PointerMove, at + new Vector2(80, 0));
        rig.TickAndPaint(12);
        float afterRight = editor.ScrollXPixels;
        Assert.True(afterRight > 0.0f);
        Assert.Equal(0.0f, editor.ScrollPixels);

        // Move diagonally down-right on screen -> both ScrollXPixels and ScrollPixels increase
        rig.Mouse(InputEventKind.PointerMove, at + new Vector2(80, 80));
        rig.TickAndPaint(12);
        Assert.True(editor.ScrollXPixels > afterRight);
        Assert.True(editor.ScrollPixels > 0.0f);

        // Move purely left -> ScrollXPixels decreases back towards 0
        float beforeLeft = editor.ScrollXPixels;
        rig.Mouse(InputEventKind.PointerMove, at + new Vector2(-80, 0));
        rig.TickAndPaint(12);
        Assert.True(editor.ScrollXPixels < beforeLeft);

        // Second middle-click cancels
        rig.Mouse(InputEventKind.PointerDown, at, MouseButton.Middle);
        rig.TickAndPaint();
        Assert.False(editor.AutoScrolling);
    }

    [Fact]
    public void Table_middle_scroll_moves_both_axes_through_compositor()
    {
        using var rig = new Rig();
        var table = new Table
        {
            Size = new Vector2(300, 200),
            RowHeight = 20,
            Columns = [new("Col 0", 120), new("Col 1", 220), new("Col 2", 220), new("Col 3", 220)],
            Rows = [.. Enumerable.Range(0, 80).Select(i => new[]
            {
                new UITableCell($"r{i}c0"),
                new UITableCell($"r{i}c1"),
                new UITableCell($"r{i}c2"),
                new UITableCell($"r{i}c3")
            })]
        };
        table.Refresh();
        rig.Module.AddComponent(table);
        rig.Tick();

        Vector2 at = rig.At(table);
        rig.Mouse(InputEventKind.PointerDown, at, MouseButton.Middle);
        rig.Tick();
        rig.Mouse(InputEventKind.PointerUp, at, MouseButton.Middle);
        rig.Tick();
        Assert.Same(table, rig.Compositor.AutoScrollTarget);

        // Move diagonally down-right on screen (+X, +Y in screen coords)
        rig.Mouse(InputEventKind.PointerMove, at + new Vector2(80, 80));
        rig.Tick(15);
        Assert.True(table.HorizontalScroll > 0.0f);
        Assert.True(table.VerticalScroll > 0.0f);

        // Move diagonally up-left on screen (-X, -Y in screen coords)
        float maxX = table.HorizontalScroll;
        float maxY = table.VerticalScroll;
        rig.Mouse(InputEventKind.PointerMove, at + new Vector2(-80, -80));
        rig.Tick(15);
        Assert.True(table.HorizontalScroll < maxX);
        Assert.True(table.VerticalScroll < maxY);

        rig.Mouse(InputEventKind.PointerDown, at, MouseButton.Middle);
        rig.Tick();
        Assert.Null(rig.Compositor.AutoScrollTarget);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Stationary_mouse_autoscroll_keeps_calm_zero_and_prevents_wait_for_events(bool lowPowerMode)
    {
        long now = (long)(10.0 * Stopwatch.Frequency);
        long staleInput = now - (long)((WindowManager.IDLE_AFTER + 1.0) * Stopwatch.Frequency);
        long smoothUntil = 0;

        // Without RequestSmooth, stale input enters calm pacing and allows WaitForEvents sleep
        Assert.True(WindowManager.CalmPeriodFor(now, staleInput, smoothUntil, exclusive: false, lifecycleWork: false, minimized: false, focused: true, lowPowerMode) > 0.0);
        Assert.True(WindowManager.MayWaitForEventsFor(ApplicationBehaviour.WaitForEvents, now, staleInput, smoothUntil, lowPowerMode));

        // While RequestSmooth is extended each tick (stationary-mouse autoscroll), calm stays 0 and WaitForEvents stays false
        WindowManager.ExtendDeadline(ref smoothUntil, now, 0.05);
        Assert.Equal(0.0, WindowManager.CalmPeriodFor(now, staleInput, smoothUntil, exclusive: false, lifecycleWork: false, minimized: false, focused: true, lowPowerMode));
        Assert.False(WindowManager.MayWaitForEventsFor(ApplicationBehaviour.WaitForEvents, now, staleInput, smoothUntil, lowPowerMode));

        // After the smooth window expires, calm pacing and WaitForEvents both resume
        long after = now + (long)(0.06 * Stopwatch.Frequency);
        Assert.True(WindowManager.CalmPeriodFor(after, staleInput, smoothUntil, exclusive: false, lifecycleWork: false, minimized: false, focused: true, lowPowerMode) > 0.0);
        Assert.True(WindowManager.MayWaitForEventsFor(ApplicationBehaviour.WaitForEvents, after, staleInput, smoothUntil, lowPowerMode));
    }

    private sealed class Rig : IDisposable
    {
        public readonly InputManager Input = new();
        public readonly Camera2D Camera = new(new Vector2(800, 600));
        public readonly UICompositor Compositor;
        public readonly UIModule Module;
        private readonly UIRect screen;
        private int frame;

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

        public void TickAndPaint(int ticks = 1)
        {
            for (int i = 0; i < ticks; i++)
            {
                Input.UpdateState(TICK);
                Compositor.RouteForTest(screen, Skin, TICK);
                frame++;
                var list = new UIDrawList();
                list.Begin(Skin, frame, TICK, frame * TICK);
                foreach (var component in Module.Components)
                    component.PaintTree(list);
                list.End();
            }
        }

        private static Vector2 ToWorld(Vector2 screen) => new(screen.X - 400.0f, 300.0f - screen.Y);

        public Vector2 At(UIComponent component, Vector2 offset = default)
        {
            Vector2 world = component.Bounds.Center + offset;
            return new Vector2(world.X + 400.0f, 300.0f - world.Y);
        }

        public void Mouse(InputEventKind kind, Vector2 at, MouseButton button = MouseButton.Left) =>
            Input.Post(InputEvent.Pointer(kind, at, PointerKind.Mouse, 0, button));

        public void Dispose()
        {
            Compositor.Focus = null;
            Compositor.RemoveModule(Module);
        }
    }
}
