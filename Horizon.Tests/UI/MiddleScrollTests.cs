using System.Numerics;
using Horizon.UI.Components;

namespace Horizon.Tests.UI;

public class MiddleScrollTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Speed_can_change_during_held_and_latched_scrolling_without_changing_the_dead_zone(bool held)
    {
        var gesture = new CodeEditor.MiddleScrollGesture();
        gesture.Step(Vector2.Zero, true, true, false, 0.02f);
        if (!held) gesture.Step(Vector2.Zero, false, true, false, 0.02f);
        float Move(float distance, float speed) => gesture.Step(new(0, -distance), held, true, false, 0.02f, speed);
        float normal = Move(60, 1);
        Assert.True(normal > 0);
        Assert.Equal(normal * 0.25f, Move(60, 0.25f), 4);
        Assert.Equal(normal * 2, Move(60, 2), 4);
        Assert.Equal(-normal * 5, Move(-60, 5), 4);
        Assert.Equal(0, Move(5, 5));
        Assert.Equal(normal, Move(60, float.NaN));
        Assert.Equal(normal * 0.25f, Move(60, -1), 4);
        Assert.True(gesture.Active);
    }

    [Fact]
    public void A_tap_latches_and_distance_controls_speed_in_both_directions()
    {
        var gesture = new CodeEditor.MiddleScrollGesture();
        gesture.Step(Vector2.Zero, true, true, false, 0.02f);
        gesture.Step(Vector2.Zero, false, true, false, 0.02f);
        Assert.True(gesture.Active);
        Assert.Equal(0, gesture.Step(new(0, 5), false, true, false, 0.02f));
        float down = gesture.Step(new(0, -40), false, true, false, 0.02f);
        float faster = gesture.Step(new(0, -80), false, true, false, 0.02f);
        float up = gesture.Step(new(0, 40), false, true, false, 0.02f);
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
        Assert.True(gesture.Step(new(0, -60), true, true, false, 0.02f) > 0);
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
}
