using System.Numerics;
using System.Reflection;

using Horizon.Physics;
using Horizon.Physics.Colliders;
using Horizon.Physics.Fixtures;
using Horizon.Physics.Simulation;
using Horizon.Rendering.Particles.Simulation;

namespace Horizon.Tests;

public class Pass1PhysicsImprovementsTests
{
    private const float STEP_120 = 1.0f / 120.0f;
    private const float STEP_240 = 1.0f / 240.0f;

    [Theory]
    [InlineData(STEP_120)]
    [InlineData(STEP_240)]
    public void Fluid_poured_into_thin_walled_basin_stays_contained_and_lies_level(float dt)
    {
        const float gravity = 1200.0f;
        const int drops = 600, go = 100;

        var world = new PhysicsWorld { Gravity = new Vector2(0.0f, -gravity) };
        var ground = world.CreateBody(PhysicsBodySimulationType.Static);
        ground.CreateRectangularFixture(new Vector2(-800.0f, -350.0f), new Vector2(1600.0f, 50.0f));
        ground.CreateRectangularFixture(new Vector2(400.0f, -300.0f), new Vector2(10.0f, 160.0f));
        ground.CreateRectangularFixture(new Vector2(640.0f, -300.0f), new Vector2(10.0f, 160.0f));

        PhysicsFluidParticleGroup fluid = world.CreateFluidParticleGroup(2048);
        fluid.Radius = 3.0f;
        fluid.Friction = 1.0f;
        fluid.MaxLife = 0.0f;
        fluid.MaxAge = 1000.0f;
        fluid.Gravity = new Vector2(0.0f, -gravity);

        Span<ParticleState2D> batch = stackalloc ParticleState2D[go];
        int settleTicks = (int)MathF.Round(0.4f / dt);
        for (int pour = 0; pour < drops / go; pour++)
        {
            for (int i = 0; i < go; i++)
            {
                batch[i] = new ParticleState2D
                {
                    Position = new Vector2(470.0f + (i % 20) * 5.0f, -260.0f + (i / 20) * 5.0f),
                    Velocity = Vector2.Zero,
                    Life = 1.0f,
                    Rate = 1.0f
                };
            }
            fluid.Add(batch);

            for (int t = 0; t < settleTicks; t++) world.UpdatePhysics(dt);
        }

        int finalTicks = (int)MathF.Round(2.5f / dt);
        for (int t = 0; t < finalTicks; t++) world.UpdatePhysics(dt);

        Assert.True(fluid.Count >= drops - 12, $"Expected at least {drops - 12} live particles, got {fluid.Count}");

        int inside = 0, leftOut = 0, rightOut = 0;
        float lowest = float.MaxValue, highest = float.MinValue;
        float minX = float.MaxValue, maxX = float.MinValue;
        foreach (ref readonly ParticleState2D drop in fluid.Particles)
        {
            minX = MathF.Min(minX, drop.Position.X);
            maxX = MathF.Max(maxX, drop.Position.X);
            if (drop.Position.X < 405.0f) { leftOut++; continue; }
            if (drop.Position.X > 645.0f) { rightOut++; continue; }
            inside++;
            lowest = MathF.Min(lowest, drop.Position.Y);
            highest = MathF.Max(highest, drop.Position.Y);
        }

        Assert.True(inside >= drops - 12, $"Expected at least {drops - 12} particles inside basin, got {inside} (live={fluid.Count}, leftOut={leftOut}, rightOut={rightOut}, X=[{minX:F1}..{maxX:F1}], Y=[{lowest:F1}..{highest:F1}])");
        Assert.True(lowest > -302.0f, $"Lowest particle fell below floor: {lowest}");
        Assert.True(highest - lowest < 120.0f, $"Fluid surface did not lie level: height={highest - lowest}");
    }

    [Fact]
    public void Fluid_poured_into_tall_thin_walled_basin_has_zero_wall_or_floor_tunneling()
    {
        const float gravity = 1200.0f;
        const int drops = 600, go = 100;

        var world = new PhysicsWorld { Gravity = new Vector2(0.0f, -gravity) };
        var ground = world.CreateBody(PhysicsBodySimulationType.Static);
        ground.CreateRectangularFixture(new Vector2(-800.0f, -350.0f), new Vector2(1600.0f, 50.0f));
        ground.CreateRectangularFixture(new Vector2(400.0f, -300.0f), new Vector2(10.0f, 1200.0f));
        ground.CreateRectangularFixture(new Vector2(640.0f, -300.0f), new Vector2(10.0f, 1200.0f));

        PhysicsFluidParticleGroup fluid = world.CreateFluidParticleGroup(2048);
        fluid.Radius = 3.0f;
        fluid.Friction = 1.0f;
        fluid.MaxLife = 0.0f;
        fluid.MaxAge = 1000.0f;
        fluid.Gravity = new Vector2(0.0f, -gravity);

        Span<ParticleState2D> batch = stackalloc ParticleState2D[go];
        int settleTicks = (int)MathF.Round(0.4f / STEP_240);
        for (int pour = 0; pour < drops / go; pour++)
        {
            for (int i = 0; i < go; i++)
            {
                batch[i] = new ParticleState2D
                {
                    Position = new Vector2(470.0f + (i % 20) * 5.0f, -260.0f + (i / 20) * 5.0f),
                    Velocity = Vector2.Zero,
                    Life = 1.0f,
                    Rate = 1.0f
                };
            }
            fluid.Add(batch);

            for (int t = 0; t < settleTicks; t++) world.UpdatePhysics(STEP_240);
        }

        int finalTicks = (int)MathF.Round(2.5f / STEP_240);
        for (int t = 0; t < finalTicks; t++) world.UpdatePhysics(STEP_240);

        Assert.Equal(drops, fluid.Count);
        int inside = 0;
        foreach (ref readonly ParticleState2D drop in fluid.Particles)
        {
            if (drop.Position.X >= 405.0f && drop.Position.X <= 645.0f && drop.Position.Y > -302.0f)
                inside++;
        }
        Assert.Equal(drops, inside);
    }

    [Fact]
    public void Multi_tile_seam_contact_bounces_only_once()
    {
        var world = new PhysicsWorld { Gravity = new Vector2(0.0f, -1000.0f) };
        var ground = world.CreateBody(PhysicsBodySimulationType.Static);
        ground.CreateRectangularFixture(new Vector2(-100.0f, -20.0f), new Vector2(100.0f, 20.0f));
        ground.CreateRectangularFixture(new Vector2(0.0f, -20.0f), new Vector2(100.0f, 20.0f));

        // Two fixtures straddling the seam at X = 0 so both tiles are contacted in the same tick
        var body = world.CreateBody(PhysicsBodySimulationType.Dynamic, new Vector2(0.0f, 1.0f));
        body.Restitution = 0.6f;
        body.StopsFlush = true;
        body.CreateRectangularFixture(new Vector2(-12.0f, 0.0f), new Vector2(10.0f, 10.0f));
        body.CreateRectangularFixture(new Vector2(2.0f, 0.0f), new Vector2(10.0f, 10.0f));
        body.SetVelocity(new Vector2(0.0f, -300.0f));

        world.UpdatePhysics(STEP_120);

        Assert.True(body.Velocity.Y > 100.0f, $"Expected single upward bounce, got Velocity.Y={body.Velocity.Y}");
    }

    [Fact]
    public void Non_pushing_dynamic_collision_preserves_perpendicular_velocity()
    {
        var world = new PhysicsWorld { BodiesPush = false };

        var moverX = world.CreateBody(PhysicsBodySimulationType.Dynamic, Vector2.Zero);
        moverX.Restitution = 0.5f;
        moverX.CreateRectangularFixture(new Vector2(-5.0f, -5.0f), new Vector2(10.0f, 10.0f));
        moverX.SetVelocity(new Vector2(100.0f, 0.0f));

        var targetX = world.CreateBody(PhysicsBodySimulationType.Dynamic, new Vector2(10.5f, 0.0f));
        targetX.CreateRectangularFixture(new Vector2(-5.0f, -5.0f), new Vector2(10.0f, 10.0f));
        targetX.SetVelocity(new Vector2(0.0f, 75.0f));

        world.UpdatePhysics(STEP_120);

        Assert.Equal(-50.0f, moverX.Velocity.X, 3);
        Assert.Equal(50.0f, targetX.Velocity.X, 3);
        Assert.Equal(75.0f, targetX.Velocity.Y, 3);

        // Now test vertical Y collision preserving perpendicular X velocity
        var worldY = new PhysicsWorld { BodiesPush = false };

        var moverY = worldY.CreateBody(PhysicsBodySimulationType.Dynamic, Vector2.Zero);
        moverY.Restitution = 0.5f;
        moverY.CreateRectangularFixture(new Vector2(-5.0f, -5.0f), new Vector2(10.0f, 10.0f));
        moverY.SetVelocity(new Vector2(0.0f, 100.0f));

        var targetY = worldY.CreateBody(PhysicsBodySimulationType.Dynamic, new Vector2(0.0f, 10.5f));
        targetY.CreateRectangularFixture(new Vector2(-5.0f, -5.0f), new Vector2(10.0f, 10.0f));
        targetY.SetVelocity(new Vector2(-42.0f, 0.0f));

        worldY.UpdatePhysics(STEP_120);

        Assert.Equal(-50.0f, moverY.Velocity.Y, 3);
        Assert.Equal(50.0f, targetY.Velocity.Y, 3);
        Assert.Equal(-42.0f, targetY.Velocity.X, 3);
    }

    [Fact]
    public void ActiveContacts_are_symmetric_and_clear_when_separated()
    {
        var world = new PhysicsWorld();

        var wall = world.CreateBody(PhysicsBodySimulationType.Static, new Vector2(20.0f, 0.0f));
        var wallFixture = wall.CreateRectangularFixture(new Vector2(-5.0f, -20.0f), new Vector2(10.0f, 40.0f));

        var a = world.CreateBody(PhysicsBodySimulationType.Dynamic, new Vector2(9.5f, 0.0f));
        var aFixture = a.CreateRectangularFixture(new Vector2(-5.0f, -5.0f), new Vector2(10.0f, 10.0f));
        a.SetVelocity(new Vector2(120.0f, 0.0f));

        var b = world.CreateBody(PhysicsBodySimulationType.Dynamic, new Vector2(-10.5f, 0.0f));
        var bFixture = b.CreateRectangularFixture(new Vector2(-5.0f, -5.0f), new Vector2(10.0f, 10.0f));
        b.SetVelocity(new Vector2(0.0f, 0.0f));

        // A hits static wall
        world.UpdatePhysics(STEP_120);
        Assert.Contains(wallFixture, aFixture.ActiveContacts);
        Assert.Contains(aFixture, wallFixture.ActiveContacts);

        // Move A away from wall and into B
        a.Position = new Vector2(0.0f, 0.0f);
        a.SetVelocity(new Vector2(-120.0f, 0.0f));
        world.UpdatePhysics(STEP_120);

        Assert.False(wallFixture.IsTouching);
        Assert.Empty(wallFixture.ActiveContacts);
        Assert.Contains(bFixture, aFixture.ActiveContacts);
        Assert.Contains(aFixture, bFixture.ActiveContacts);
    }

    [Fact]
    public void Static_grid_refresh_detects_moved_resized_and_swapped_static_fixtures()
    {
        var world = new PhysicsWorld();

        var wall = world.CreateBody(PhysicsBodySimulationType.Static, Vector2.Zero);
        var wallFixture = wall.CreateRectangularFixture(new Vector2(0.0f, 0.0f), new Vector2(10.0f, 10.0f));

        var probe = new RectanglePhysicsFixture(new Vector2(-2.0f, -2.0f), new Vector2(4.0f, 4.0f));

        world.UpdatePhysics(STEP_120);
        Assert.True(world.OverlapsStatic(probe, new Vector2(5.0f, 5.0f)));
        Assert.False(world.OverlapsStatic(probe, new Vector2(105.0f, 5.0f)));

        // 1. Move static body
        wall.Position = new Vector2(100.0f, 0.0f);
        world.UpdatePhysics(STEP_120);
        Assert.False(world.OverlapsStatic(probe, new Vector2(5.0f, 5.0f)));
        Assert.True(world.OverlapsStatic(probe, new Vector2(105.0f, 5.0f)));

        // 2. Resize static fixture in place
        wallFixture.Bounds = new PhysicsRectangle(new Vector2(0.0f, 0.0f), new Vector2(50.0f, 10.0f));
        world.UpdatePhysics(STEP_120);
        Assert.True(world.OverlapsStatic(probe, new Vector2(145.0f, 5.0f)));

        // 3. Swap static body (same total fixture count = 1)
        world.StaticBodies.Clear();
        var replacement = world.CreateBody(PhysicsBodySimulationType.Static, new Vector2(-200.0f, 0.0f));
        replacement.CreateRectangularFixture(new Vector2(0.0f, 0.0f), new Vector2(10.0f, 10.0f));
        world.UpdatePhysics(STEP_120);

        Assert.False(world.OverlapsStatic(probe, new Vector2(145.0f, 5.0f)));
        Assert.True(world.OverlapsStatic(probe, new Vector2(-195.0f, 5.0f)));
    }

    [Fact]
    public void Crouched_3D_character_does_not_step_off_ledge()
    {
        var world = new CollisionWorld3D();
        // A platform from X=[0, 4], Y=[0, 1], Z=[0, 4]
        world.Add(new BoxCollider3D(new Box3(new Vector3(0.0f, 0.0f, 0.0f), new Vector3(4.0f, 1.0f, 4.0f))));

        var player = new CharacterController3D(world, new Vector3(3.2f, 1.0f, 2.0f))
        {
            Crouching = true,
            StepHeight = 0.6f,
            Move = Vector3.UnitX
        };

        // Settle onto ground, then walk toward the +X edge for 2 seconds
        for (int i = 0; i < 240; i++)
        {
            player.UpdatePhysics(STEP_120);
        }

        Assert.True(player.IsGrounded, "Crouched character walked off the ledge");
        Assert.True(player.Position.Y >= 0.99f, $"Crouched character fell below platform: Y={player.Position.Y}");
        Assert.True(player.Position.X <= 4.35f, $"Crouched character moved past edge support: X={player.Position.X}");
    }

    [Fact]
    public void CollisionWorld3D_Overlaps_exits_early_before_querying_fields()
    {
        var world = new CollisionWorld3D();
        world.Add(new BoxCollider3D(new Box3(new Vector3(0.0f, 0.0f, 0.0f), new Vector3(2.0f, 2.0f, 2.0f))));

        var firstField = new CountingCollisionField(new Box3(new Vector3(10.0f, 0.0f, 0.0f), new Vector3(12.0f, 2.0f, 2.0f)));
        var secondField = new CountingCollisionField(new Box3(new Vector3(20.0f, 0.0f, 0.0f), new Vector3(22.0f, 2.0f, 2.0f)));
        world.Add(firstField);
        world.Add(secondField);

        // Overlaps box collider -> neither field should be queried
        Assert.True(world.Overlaps(new Box3(new Vector3(0.5f, 0.5f, 0.5f), new Vector3(1.5f, 1.5f, 1.5f))));
        Assert.Equal(0, firstField.CollectCalls);
        Assert.Equal(0, secondField.CollectCalls);

        // Overlaps first field -> second field should not be queried
        Assert.True(world.Overlaps(new Box3(new Vector3(10.5f, 0.5f, 0.5f), new Vector3(11.5f, 1.5f, 1.5f))));
        Assert.Equal(1, firstField.CollectCalls);
        Assert.Equal(0, secondField.CollectCalls);

        // Overlaps nothing -> both fields queried
        Assert.False(world.Overlaps(new Box3(new Vector3(50.0f, 0.0f, 0.0f), new Vector3(51.0f, 1.0f, 1.0f))));
        Assert.Equal(2, firstField.CollectCalls);
        Assert.Equal(1, secondField.CollectCalls);
    }

    [Theory]
    [InlineData(typeof(PhysicsWorld), "UpdatePhysics")]
    [InlineData(typeof(PhysicsWorld), "OutlineBodies")]
    [InlineData(typeof(PhysicsFluidParticleGroup), "Step")]
    public void Physics_hot_paths_make_no_closures(Type type, string methodName)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        MethodInfo? method = type.GetMethod(methodName, flags);
        Assert.NotNull(method);

        byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
        Assert.NotNull(il);

        for (int i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != 0x73) continue; // newobj
            MethodBase? ctor;
            try
            {
                ctor = method.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1));
            }
            catch (Exception)
            {
                continue;
            }

            if (ctor?.DeclaringType is { } declared)
            {
                Assert.False(declared.Name.StartsWith("<>c__DisplayClass", StringComparison.Ordinal),
                    $"{type.Name}.{methodName} allocates closure {declared.Name}");
            }
        }
    }

    private sealed class CountingCollisionField(Box3 solid) : ICollisionField
    {
        public int CollectCalls { get; private set; }

        public void CollectSolids(in Box3 region, List<Box3> into)
        {
            CollectCalls++;
            if (solid.Intersects(region)) into.Add(solid);
        }

        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RaycastHit3D hit)
        {
            hit = default;
            return false;
        }
    }
}
