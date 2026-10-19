using System.Numerics;

using Horizon.Physics;

namespace Horizon.Tests;

public class BodyGravityTests
{
    private const float STEP = 1.0f / 120.0f;
    private const float GRAVITY = 1000.0f;

    private static (PhysicsWorld World, PhysicsBodyComponent2D Body) Sky()
    {
        var world = new PhysicsWorld { Gravity = new Vector2(0.0f, -GRAVITY) };
        var body = world.CreateBody(PhysicsBodySimulationType.Dynamic, Vector2.Zero);
        body.CreateRectangularFixture(Vector2.Zero, new Vector2(10.0f, 10.0f));
        return (world, body);
    }

    private static void Step(PhysicsWorld world, int ticks)
    {
        for (int i = 0; i < ticks; i++) world.UpdatePhysics(STEP);
    }

    [Fact]
    public void Half_the_gravity_falls_half_as_far()
    {
        var (world, whole) = Sky();
        var half = world.CreateBody(PhysicsBodySimulationType.Dynamic, new Vector2(100.0f, 0.0f));
        half.CreateRectangularFixture(Vector2.Zero, new Vector2(10.0f, 10.0f));
        half.GravityScale = 0.5f;

        Step(world, 60);

        Assert.Equal(whole.Position.Y * 0.5f, half.Position.Y, 2);
        Assert.Equal(whole.Velocity.Y * 0.5f, half.Velocity.Y, 2);
    }

    [Fact]
    public void A_fall_tops_out_at_its_limit_and_a_throw_slows_to_it()
    {
        var (world, body) = Sky();
        body.MaxFallSpeed = 300.0f;

        Step(world, 120);
        Assert.Equal(-300.0f, body.Velocity.Y, 2);

        // Thrown down quicker than that, it's straight back to the limit, and sideways is left alone
        body.SetVelocity(new Vector2(50.0f, -900.0f));
        Step(world, 1);
        Assert.Equal(-300.0f, body.Velocity.Y, 2);
        Assert.Equal(50.0f, body.Velocity.X, 2);

        // Going up isn't falling, nothing is taken off that
        body.SetVelocity(new Vector2(0.0f, 900.0f));
        Step(world, 1);
        Assert.Equal(900.0f - GRAVITY * STEP, body.Velocity.Y, 2);
    }
}
