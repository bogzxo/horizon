using System.Numerics;

using Horizon.Input;

namespace Horizon.Tests.Core;

public sealed class TouchTests
{
    private static InputEvent Finger(InputEventKind kind, int id, float x, PointerKind pointer = PointerKind.Touch) =>
        InputEvent.Pointer(kind, new Vector2(x, 10), pointer, id);

    [Fact]
    public void A_cancel_is_told_apart_from_an_up()
    {
        var input = new InputManager();
        input.Post(Finger(InputEventKind.PointerDown, 1, 10));
        input.Post(Finger(InputEventKind.PointerDown, 2, 20));
        input.UpdateState(0.01f);
        input.Post(Finger(InputEventKind.PointerUp, 1, 11));
        input.Post(Finger(InputEventKind.PointerCancel, 2, 21));
        input.UpdateState(0.01f);

        Assert.True(input.Touch.TryGet(1, out TouchPoint up));
        Assert.True(up.WasReleased);
        Assert.False(up.WasCancelled);
        Assert.True(input.Touch.TryGet(2, out TouchPoint cancelled));
        Assert.True(cancelled.WasReleased);
        Assert.True(cancelled.WasCancelled);
        Assert.Equal(0, input.Touch.Count);

        input.UpdateState(0.01f);
        Assert.Empty(input.Touch.Points);
    }

    [Fact]
    public void A_pen_is_a_contact_of_its_own_kind_and_the_last_pointer_is_tracked()
    {
        var input = new InputManager();
        Assert.Equal(PointerKind.Mouse, input.LastPointerKind);
        input.Post(Finger(InputEventKind.PointerDown, 4, 10, PointerKind.Pen));
        input.UpdateState(0.01f);
        Assert.True(input.Touch.TryGet(4, out TouchPoint pen));
        Assert.Equal(PointerKind.Pen, pen.Kind);
        Assert.Equal(PointerKind.Pen, input.LastPointerKind);

        input.Post(InputEvent.Pointer(InputEventKind.PointerMove, new Vector2(5, 5)));
        input.UpdateState(0.01f);
        Assert.Equal(PointerKind.Mouse, input.LastPointerKind);

        // The mouse never moves for a finger
        input.Post(Finger(InputEventKind.PointerMove, 4, 300, PointerKind.Pen));
        input.UpdateState(0.01f);
        Assert.Equal(new Vector2(5, 5), input.Mouse.Position);
    }

    [Fact]
    public void A_tap_between_two_ticks_is_pressed_and_released_in_the_one_tick()
    {
        var input = new InputManager();
        input.Post(Finger(InputEventKind.PointerDown, 9, 10));
        input.Post(Finger(InputEventKind.PointerUp, 9, 10));
        input.UpdateState(0.01f);
        Assert.True(input.Touch.TryGet(9, out TouchPoint tap));
        Assert.True(tap.WasPressed);
        Assert.True(tap.WasReleased);
        Assert.False(tap.WasCancelled);
        input.UpdateState(0.01f);
        Assert.False(input.Touch.TryGet(9, out _));
    }
}
