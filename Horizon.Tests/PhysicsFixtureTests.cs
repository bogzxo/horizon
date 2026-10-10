using System.Numerics;

using Horizon.Physics.Fixtures;

namespace Horizon.Tests;

public class PhysicsFixtureTests
{
    // A box and a circle on two bodies that stand apart, neither fixture sat in the middle of its body (with both
    // in the middle the mistake cancels itself out, which is how it hid). Whoever asks, the answer has to be the
    // same, and for years it was not. The box measured the circle from where its own body stood
    [Theory]
    [InlineData(0.0f, 0.0f, -12.0f, 10.0f, true)]       // the circle ends up inside of the box
    [InlineData(0.0f, 0.0f, 25.0f, -5.0f, false)]       // nowhere near, the bodies swapped had them touching
    [InlineData(100.0f, 200.0f, 94.0f, 210.0f, true)]   // just into the right edge, a long way from the origin
    [InlineData(100.0f, 200.0f, 100.0f, 200.0f, false)] // both bodies on one spot and the circle out to the side
    [InlineData(-300.0f, 50.0f, 500.0f, 500.0f, false)]
    public void A_box_and_a_circle_agree_on_whether_they_touch(float boxX, float boxY, float circleX, float circleY, bool touching)
    {
        var box = new RectanglePhysicsFixture(Vector2.Zero, new Vector2(20.0f, 20.0f));
        var circle = new CirclePhysicsFixture(5.0f, new Vector2(30.0f, 0.0f));

        Vector2 boxBody = new(boxX, boxY), circleBody = new(circleX, circleY);

        Assert.Equal(touching, circle.TestIntersection(box, circleBody, boxBody));
        Assert.Equal(touching, box.TestIntersection(circle, boxBody, circleBody));
    }

    [Fact]
    public void Two_boxes_only_touch_when_their_bodies_bring_them_together()
    {
        var a = new RectanglePhysicsFixture(Vector2.Zero, new Vector2(16.0f, 16.0f));
        var b = new RectanglePhysicsFixture(Vector2.Zero, new Vector2(16.0f, 16.0f));

        Assert.True(a.TestIntersection(b, new Vector2(40.0f, 40.0f), new Vector2(50.0f, 45.0f)));
        Assert.False(a.TestIntersection(b, new Vector2(40.0f, 40.0f), new Vector2(60.0f, 40.0f)));
    }

    [Fact]
    public void Nothing_runs_into_an_outline_but_particles()
    {
        var outline = new OutlinePhysicsFixture();
        outline.Set([new Vector2(-5.0f, 0.0f), new Vector2(5.0f, 0.0f), new Vector2(5.0f, 0.0f), new Vector2(0.0f, 8.0f)]);
        var box = new RectanglePhysicsFixture(new Vector2(-10.0f, -10.0f), new Vector2(20.0f, 20.0f));

        Assert.False(outline.TestIntersection(box, Vector2.Zero, Vector2.Zero));
        Assert.False(box.TestIntersection(outline, Vector2.Zero, Vector2.Zero));
        Assert.Equal(new Vector2(-5.0f, 0.0f), outline.Min);
        Assert.Equal(new Vector2(5.0f, 8.0f), outline.Max);
    }
}
