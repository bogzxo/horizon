using System.Numerics;

using Horizon.Physics;

namespace Horizon.Tests;

public class CharacterControllerTests
{
    private const float STEP = 1.0f / 120.0f;
    private const float GRAVITY = 620.0f;

    /// <summary>A floor from -500 to 500 with its top at 0, a wall on its right hand end, and somebody stood in the middle of it.</summary>
    private static (PhysicsWorld World, CharacterController2D Hero) Street(float height = 0.0f)
    {
        var world = new PhysicsWorld { Gravity = new Vector2(0.0f, -GRAVITY) };

        var ground = world.CreateBody(PhysicsBodySimulationType.Static);
        ground.CreateRectangularFixture(new Vector2(-500.0f, -50.0f), new Vector2(1000.0f, 50.0f));
        ground.CreateRectangularFixture(new Vector2(200.0f, 0.0f), new Vector2(50.0f, 200.0f));

        var hero = new CharacterController2D(world, new Vector2(0.0f, height), new Vector2(10.0f, 10.0f));
        return (world, hero);
    }

    private static void Step(PhysicsWorld world, CharacterController2D hero, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++)
        {
            hero.UpdatePhysics(STEP);
            world.UpdatePhysics(STEP);
        }
    }

    [Fact]
    public void A_key_is_a_push_and_not_a_speed()
    {
        var (world, hero) = Street();
        Step(world, hero, 10);
        Assert.True(hero.Grounded);

        // Held, they are nowhere near flat out a tick later, and flat out a while after that
        hero.Move = new Vector2(1.0f, 0.0f);
        Step(world, hero);
        Assert.InRange(hero.Velocity.X, 1.0f, hero.MaxSpeed * 0.2f);

        Step(world, hero, 60);
        Assert.Equal(hero.MaxSpeed, hero.Velocity.X, 1);

        // Let go, they are still moving a tick later, and stood still a while after that
        hero.Move = Vector2.Zero;
        Step(world, hero);
        Assert.InRange(hero.Velocity.X, hero.MaxSpeed * 0.7f, hero.MaxSpeed);

        Step(world, hero, 60);
        Assert.Equal(0.0f, hero.Velocity.X, 2);
    }

    [Fact]
    public void Turning_round_is_quicker_than_stopping_and_starting()
    {
        var (world, hero) = Street();
        hero.Move = new Vector2(1.0f, 0.0f);
        Step(world, hero, 80);

        hero.Move = new Vector2(-1.0f, 0.0f);
        int ticks = 0;
        while (hero.Velocity.X > -hero.MaxSpeed + 0.5f && ticks < 1000)
        {
            Step(world, hero);
            ticks++;
        }

        float plain = (hero.MaxSpeed / hero.Deceleration + hero.MaxSpeed / hero.Acceleration) / STEP;
        Assert.True(ticks < plain, $"turning round took {ticks} ticks, stopping and starting would take {plain:0}");
        Assert.Equal(new Vector2(-1.0f, 0.0f), hero.Facing);
    }

    [Fact]
    public void A_jump_goes_as_high_as_its_speed_says_and_lands_flush()
    {
        var (world, hero) = Street();
        Step(world, hero, 10);

        int jumps = 0, landings = 0;
        hero.Jumped += () => jumps++;
        hero.Landed += _ => landings++;

        hero.Jump();
        float highest = 0.0f;
        for (int i = 0; i < 240; i++)
        {
            Step(world, hero);
            highest = MathF.Max(highest, hero.Position.Y);
        }

        // Held all the way up, so it is the plain arc, the speed squared over twice the gravity
        float expected = hero.JumpSpeed * hero.JumpSpeed / (2.0f * GRAVITY);
        Assert.InRange(highest, expected * 0.93f, expected * 1.02f);

        Assert.Equal(1, jumps);
        Assert.Equal(1, landings);
        Assert.True(hero.Grounded);

        // On the floor, not hung a step of falling above it
        Assert.InRange(hero.Position.Y, 0.0f, 0.06f);
    }

    [Fact]
    public void Letting_go_early_is_a_short_hop()
    {
        var (world, hero) = Street();
        Step(world, hero, 10);

        hero.Jump();
        hero.HoldingJump = false;

        float highest = 0.0f;
        for (int i = 0; i < 240; i++)
        {
            Step(world, hero);
            highest = MathF.Max(highest, hero.Position.Y);
        }

        float full = hero.JumpSpeed * hero.JumpSpeed / (2.0f * GRAVITY);
        Assert.InRange(highest, full * 0.25f, full * 0.6f);
    }

    [Fact]
    public void There_is_no_jumping_off_thin_air_but_there_is_just_after_the_ledge()
    {
        // In the air from the start, well above the floor
        var (world, hero) = Street(height: 100.0f);
        Step(world, hero, 30);
        Assert.False(hero.Grounded);

        hero.Jump();
        Step(world, hero, 2);
        Assert.True(hero.Velocity.Y < 0.0f, "jumped off nothing");

        // Landed, then knocked off the ground, a jump within the grace still counts
        Step(world, hero, 240);
        Assert.True(hero.Grounded);

        hero.Teleport(hero.Position + new Vector2(0.0f, 3.0f));
        Step(world, hero, 3);
        Assert.False(hero.Grounded);

        hero.Jump();
        Step(world, hero, 2);
        Assert.True(hero.Velocity.Y > hero.JumpSpeed * 0.8f, "the grace after the ledge did not count");
    }

    [Fact]
    public void A_press_just_before_landing_is_a_jump_on_landing()
    {
        var (world, hero) = Street(height: 20.0f);

        int jumps = 0;
        hero.Jumped += () => jumps++;

        // Pressed every tick of the fall would be cheating, so once, a few ticks before the floor
        bool pressed = false;
        for (int i = 0; i < 240 && jumps == 0; i++)
        {
            if (!pressed && hero.Position.Y < 3.0f && hero.Velocity.Y < 0.0f)
            {
                hero.Jump();
                pressed = true;
            }

            Step(world, hero);
        }

        Assert.True(pressed);
        Assert.Equal(1, jumps);
    }

    [Fact]
    public void A_wall_stops_them_and_they_stand_right_up_against_it()
    {
        var (world, hero) = Street();
        hero.Move = new Vector2(1.0f, 0.0f);
        Step(world, hero, 600);

        // The wall starts at 200 and they are 10 wide around their middle
        Assert.InRange(hero.Position.X, 194.9f, 195.0f);
        Assert.InRange(hero.Velocity.X, -0.01f, hero.Acceleration * STEP + 0.01f);
    }

    [Fact]
    public void Seen_from_above_they_go_where_they_are_pointed_and_no_faster_on_the_slant()
    {
        var world = new PhysicsWorld();
        var hero = new CharacterController2D(world, Vector2.Zero, new Vector2(10.0f, 10.0f), topDown: true);

        hero.Move = new Vector2(1.0f, 1.0f);
        Step(world, hero);
        Assert.InRange(hero.Velocity.Length(), 1.0f, hero.MaxSpeed * 0.2f);

        Step(world, hero, 120);
        Assert.Equal(hero.MaxSpeed, hero.Velocity.Length(), 1);
        Assert.Equal(hero.Velocity.X, hero.Velocity.Y, 2);

        hero.Move = Vector2.Zero;
        Step(world, hero, 120);
        Assert.Equal(0.0f, hero.Velocity.Length(), 2);
        Assert.True(hero.Position.X > 50.0f && hero.Position.Y > 50.0f);
    }

    [Theory]
    [InlineData(1.0f, 50.0f)]       // as heavy as whoever pushes it, they go on together at half the speed
    [InlineData(3.0f, 25.0f)]       // three times as heavy, a quarter
    public void Something_pushed_gets_going_and_slows_whoever_pushed_it(float mass, float together)
    {
        var world = new PhysicsWorld { BodiesPush = true };

        var pusher = world.CreateBody(PhysicsBodySimulationType.Dynamic, Vector2.Zero);
        pusher.Restitution = 0.0f;
        pusher.CreateRectangularFixture(new Vector2(-5.0f, -5.0f), new Vector2(10.0f, 10.0f));
        pusher.SetVelocity(new Vector2(100.0f, 7.0f));

        var crate = world.CreateBody(PhysicsBodySimulationType.Dynamic, new Vector2(11.0f, 0.0f));
        crate.Mass = mass;
        crate.Restitution = 0.0f;
        crate.CreateRectangularFixture(new Vector2(-5.0f, -5.0f), new Vector2(10.0f, 10.0f));

        for (int i = 0; i < 4; i++) world.UpdatePhysics(STEP);

        // What the two had between them is what they still have, and it went the way of the push only
        Assert.Equal(together, crate.Velocity.X, 2);
        Assert.Equal(together, pusher.Velocity.X, 2);
        Assert.Equal(0.0f, crate.Velocity.Y, 3);
        Assert.Equal(7.0f, pusher.Velocity.Y, 3);
        Assert.True(crate.Position.X > 11.0f);
    }

    [Fact]
    public void Without_being_asked_two_bodies_meet_the_way_they_always_did()
    {
        var world = new PhysicsWorld();

        var one = world.CreateBody(PhysicsBodySimulationType.Dynamic, Vector2.Zero);
        one.Restitution = 0.5f;
        one.CreateRectangularFixture(new Vector2(-5.0f, -5.0f), new Vector2(10.0f, 10.0f));
        one.SetVelocity(new Vector2(100.0f, 0.0f));

        var other = world.CreateBody(PhysicsBodySimulationType.Dynamic, new Vector2(10.5f, 0.0f));
        other.CreateRectangularFixture(new Vector2(-5.0f, -5.0f), new Vector2(10.0f, 10.0f));

        world.UpdatePhysics(STEP);

        // Sent back at half its speed, and the other off at that turned round
        Assert.Equal(-50.0f, one.Velocity.X, 3);
        Assert.Equal(50.0f, other.Velocity.X, 3);
    }

    [Fact]
    public void A_body_that_does_not_stop_flush_is_left_where_it_was()
    {
        // The old way, which every body that never asked for the new one still gets
        var world = new PhysicsWorld { Gravity = new Vector2(0.0f, -GRAVITY) };
        var ground = world.CreateBody(PhysicsBodySimulationType.Static);
        ground.CreateRectangularFixture(new Vector2(-50.0f, -50.0f), new Vector2(100.0f, 50.0f));

        var crate = world.CreateBody(PhysicsBodySimulationType.Dynamic, new Vector2(0.0f, 100.0f));
        crate.Restitution = 0.0f;
        crate.CreateRectangularFixture(new Vector2(-5.0f, 0.0f), new Vector2(10.0f, 10.0f));

        float before = 100.0f;
        for (int i = 0; i < 120; i++)
        {
            world.UpdatePhysics(STEP);
            if (crate.Velocity.Y == 0.0f && i > 5) break;
            before = crate.Position.Y;
        }

        // Stopped where it was the tick before it would have hit, which is short of the floor by a step
        Assert.Equal(before, crate.Position.Y);
        Assert.True(crate.Position.Y > 0.5f);
    }
}
