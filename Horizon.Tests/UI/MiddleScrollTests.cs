using System.Numerics;
using Horizon.UI.Components;

namespace Horizon.Tests.UI;

public class MiddleScrollTests
{
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
