using System.Numerics;

using Horizon.Physics;
using Horizon.Physics.Fixtures;

namespace Horizon.Tests;

/// <summary>
/// A platform that is one way (<see cref="IPhysicsFixture.OneWay"/>) is landed on from above, jumped up through
/// from below, walked through from the side, and dropped through by a body that says it passes them.
/// </summary>
public class OneWayPlatformTests
{
    private const float STEP = 1.0f / 120.0f;

    // A platform 100 wide and 8 tall with its top at 100, and the floor under it
    private const float PLATFORM_TOP = 100.0f;

    private static (PhysicsWorld World, PhysicsBodyComponent2D Body, RectanglePhysicsFixture Feet) Stage(Vector2 start)
    {
        var world = new PhysicsWorld { Gravity = new Vector2(0.0f, -1000.0f) };

        var platform = world.CreateBody(PhysicsBodySimulationType.Static, Vector2.Zero);
        platform.CreateRectangularFixture(new Vector2(0.0f, PLATFORM_TOP - 8.0f), new Vector2(100.0f, 8.0f), oneWay: true);

        var floor = world.CreateBody(PhysicsBodySimulationType.Static, Vector2.Zero);
        floor.CreateRectangularFixture(new Vector2(-100.0f, -8.0f), new Vector2(300.0f, 8.0f));

        var body = world.CreateBody(PhysicsBodySimulationType.Dynamic, start);
        body.StopsFlush = true;
        body.Restitution = 0.0f;
        body.CreateRectangularFixture(Vector2.Zero, new Vector2(10.0f, 20.0f));
        var feet = body.CreateRectangularFixture(new Vector2(1.0f, -4.0f), new Vector2(8.0f, 4.0f), kinematic: true, tag: "feet");

        return (world, body, feet);
    }

    private static void Step(PhysicsWorld world, int ticks)
    {
        for (int i = 0; i < ticks; i++) world.UpdatePhysics(STEP);
    }

    [Fact]
    public void Falling_onto_it_lands_on_it()
    {
        var (world, body, feet) = Stage(new Vector2(45.0f, 150.0f));

        Step(world, 120);

        Assert.Equal(PLATFORM_TOP, body.Position.Y, 1);
        Assert.True(feet.IsTouching, "the feet should be on the platform");
    }

    [Fact]
    public void Jumping_up_through_it_goes_through_and_comes_down_on_it()
    {
        var (world, body, feet) = Stage(new Vector2(45.0f, 0.0f));
        Step(world, 10);
        body.SetVelocity(new Vector2(0.0f, 600.0f));

        // On the way up it passes the platform, and the feet don't count as on it going through
        float highest = 0.0f;
        bool touchedGoingUp = false;
        for (int i = 0; i < 60; i++)
        {
            world.UpdatePhysics(STEP);
            highest = MathF.Max(highest, body.Position.Y);
            if (body.Velocity.Y > 0.0f && body.Position.Y > PLATFORM_TOP - 10.0f && feet.IsTouching) touchedGoingUp = true;
        }

        Assert.True(highest > PLATFORM_TOP + 20.0f, $"should have gone up through the platform, got to {highest}");
        Assert.False(touchedGoingUp, "the feet shouldn't touch a platform they go up through");

        Step(world, 120);
        Assert.Equal(PLATFORM_TOP, body.Position.Y, 1);
    }

    [Fact]
    public void Walking_into_its_side_goes_through()
    {
        // Standing on the floor next to the platform at its height, which is 92 to 100, walking right into it
        var (world, body, _) = Stage(new Vector2(-30.0f, 95.0f));
        body.GravityScale = 0.0f;

        for (int i = 0; i < 60; i++)
        {
            body.SetVelocity(new Vector2(200.0f, 0.0f));
            world.UpdatePhysics(STEP);
        }

        Assert.True(body.Position.X > 50.0f, $"should have walked through the side of the platform, at {body.Position.X}");
    }

    [Fact]
    public void A_body_that_passes_them_drops_through_to_the_floor()
    {
        var (world, body, _) = Stage(new Vector2(45.0f, PLATFORM_TOP));
        Step(world, 10);
        Assert.Equal(PLATFORM_TOP, body.Position.Y, 1);

        body.PassesOneWay = true;
        Step(world, 20);
        body.PassesOneWay = false;
        Step(world, 120);

        Assert.Equal(0.0f, body.Position.Y, 1);
    }

    [Fact]
    public void A_wall_still_stops_a_body_fitting_itself_but_a_platform_does_not()
    {
        var (world, body, _) = Stage(new Vector2(45.0f, 95.0f));
        Step(world, 1);

        var probe = new RectanglePhysicsFixture(Vector2.Zero, new Vector2(10.0f, 20.0f));
        Assert.False(world.OverlapsStatic(probe, new Vector2(45.0f, 95.0f)), "half inside a platform is not stuck");
        Assert.True(world.OverlapsStatic(probe, new Vector2(45.0f, -5.0f)), "half inside the floor is");
    }
}
