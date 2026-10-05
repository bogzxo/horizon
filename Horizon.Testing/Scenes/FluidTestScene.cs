using System.Numerics;

using Horizon.Engine;
using Horizon.Input;
using Horizon.Physics;
using Horizon.Physics.Simulation;
using Horizon.Rendering.Particles;

using Silk.NET.Input;

namespace Horizon.Testing.Scenes;

/// <summary>
/// Pours the particles of a <see cref="PhysicsFluidParticleSimulator2D"/> into three basins next to each other.
/// A spout fills the first one (past a block in its way) until it runs over the divider into the second, and so on.
/// The water levels itself out, the sand (which has no spread) heaps up where it lands instead.
/// Over all of it falls the rain of a <see cref="PhysicsParticleSimulator2D"/>, which is simulated on the GPU: it lands on
/// the map and bounces off the crate, but passes through the water and through itself.
/// Hold left click: pour water. Hold right click: pour sand. P: toggle the spout. R: toggle the rain. Space: shockwave.
/// B: drop the crate. C: clear. D: toggle the outlines of the physics world.
/// </summary>
public class FluidTestScene : Scene
{
    private const float Wall = 24.0f;           // How thick the floor, the walls and the dividers are
    private const float SpoutRate = 400.0f;     // Particles per second
    private const int SpoutLimit = 7000;        // The spout stops by itself here, the basins are about full by then
    private const float PourRate = 400.0f;      // Particles per second while a mouse button is held
    private const float RainRate = 300.0f;      // Particles per second
    private const float StatusInterval = 5.0f;
    private const float MaxLife = 45.0f;        // Seconds, after which what was poured is gone and the spout starts over

    private static readonly Vector2 Gravity = new(0, -500);

    public override Camera ActiveCamera { get; protected set; }

    private PhysicsWorld world = null!;
    private PhysicsBodyComponent2D crate = null!;
    private PhysicsFluidParticleSimulator2D waterSimulator = null!, sandSimulator = null!;
    private ParticleRenderer2D water = null!, sand = null!, rain = null!;

    private Vector2 spoutPosition;
    private bool spoutOpen = true, raining = true;
    private float spoutDue, pourDue, rainDue, statusTimer;
    private int updates;

    public FluidTestScene()
    {
        ActiveCamera = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize));
    }

    public override void Initialize()
    {
        world = AddComponent<PhysicsWorld>();
        world.Gravity = new Vector2(0, -1500);
        world.RenderDebug = true;

        BuildBasins();

        // Parked out of sight until it is dropped
        crate = world.CreateBody(PhysicsBodySimulationType.Dynamic, new Vector2(0, -10000));
        crate.CreateRectangularFixture(new Vector2(-28, -28), new Vector2(56, 56));

        // Runny, it finds its own level
        waterSimulator = new PhysicsFluidParticleSimulator2D(world);
        waterSimulator.Particles.Radius = 3.0f;
        waterSimulator.Particles.Friction = 1.0f;
        waterSimulator.MaxLife = MaxLife;

        // Dry, with nothing spreading it out it stays in a heap
        sandSimulator = new PhysicsFluidParticleSimulator2D(world);
        sandSimulator.Particles.Radius = 3.0f;
        sandSimulator.Particles.Friction = 12.0f;
        sandSimulator.Particles.Spread = 0.0f;
        sandSimulator.MaxLife = MaxLife;

        // The renderers compile their shaders as they are constructed, so they need the GL context.
        // The particles darken over their life, and are all gone by the time it is up.
        water = AddEntity(
            new ParticleRenderer2D(8192, waterSimulator)
            {
                StartColor = new Vector3(0.2f, 0.55f, 1.0f),
                EndColor = new Vector3(0.0f, 0.1f, 0.5f),
                ParticleSize = 3.0f,
                MaxAge = MaxLife,
                Gravity = Gravity
            });

        sand = AddEntity(
            new ParticleRenderer2D(4096, sandSimulator)
            {
                StartColor = new Vector3(0.95f, 0.8f, 0.45f),
                EndColor = new Vector3(0.5f, 0.35f, 0.1f),
                ParticleSize = 3.0f,
                MaxAge = MaxLife,
                Gravity = Gravity
            });

        // Light and bouncy, these only have to land on things so the GPU can have them
        rain = AddEntity(
            new ParticleRenderer2D(8192, new PhysicsParticleSimulator2D(world) { Radius = 2.0f, Restitution = 0.45f, Friction = 4.0f })
            {
                StartColor = new Vector3(0.9f, 0.95f, 1.0f),
                EndColor = new Vector3(0.3f, 0.4f, 0.5f),
                ParticleSize = 2.0f,
                MaxAge = 6.0f,
                Gravity = Gravity
            });

        base.Initialize();
    }

    /// <summary>
    /// Helper method to build the map: a box around the view, split into three basins by two dividers,
    /// the second lower than the first so each basin overflows into the next.
    /// </summary>
    private void BuildBasins()
    {
        Vector2 half = Engine.WindowManager.ViewportSize / 2.0f;
        float floor = -half.Y + Wall;

        var map = world.CreateBody(PhysicsBodySimulationType.Static);
        map.CreateRectangularFixture(new Vector2(-half.X, -half.Y), new Vector2(half.X * 2, Wall));
        map.CreateRectangularFixture(new Vector2(-half.X, -half.Y), new Vector2(Wall, half.Y * 2));
        map.CreateRectangularFixture(new Vector2(half.X - Wall, -half.Y), new Vector2(Wall, half.Y * 2));

        map.CreateRectangularFixture(new Vector2(-half.X / 3 - Wall / 2, floor), new Vector2(Wall, half.Y * 0.3f));
        map.CreateRectangularFixture(new Vector2(half.X / 3 - Wall / 2, floor), new Vector2(Wall, half.Y * 0.18f));

        // In the way of the spout, so what comes out of it has to go around
        float spoutX = -half.X * 2 / 3;
        map.CreateRectangularFixture(new Vector2(spoutX - 40, floor + 60), new Vector2(80, Wall));

        spoutPosition = new Vector2(spoutX, half.Y - 40);
    }

    public override void PostInit()
    {
        base.PostInit();

        Console.WriteLine("FluidTestScene\r\n\r\n The spout fills the left basin, which overflows into the next one, and that into the last.");
        Console.WriteLine("Hold left click: pour water. Hold right click: pour sand. P: toggle the spout. R: toggle the rain. Space: shockwave.");
        Console.WriteLine("B: drop the crate. C: clear. D: toggle the outlines of the physics world.");
        Engine.GL.ClearColor(0.02f, 0.02f, 0.04f, 1.0f);
    }

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);

        var keyboard = Engine.InputManager.KeyboardManager;
        var mouseData = Engine.InputManager.MouseManager.GetData();
        var mousePos = ActiveCamera.ScreenToWorld(mouseData.Position);

        if (keyboard.IsKeyPressed(Key.P)) spoutOpen = !spoutOpen;
        if (keyboard.IsKeyPressed(Key.R)) raining = !raining;
        if (keyboard.IsKeyPressed(Key.D)) world.RenderDebug = !world.RenderDebug;

        if (keyboard.IsKeyPressed(Key.C))
        {
            waterSimulator.Particles.Clear();
            sandSimulator.Particles.Clear();
        }

        if (keyboard.IsKeyPressed(Key.Space))
        {
            world.ApplyRadialImpulse(mousePos, 160, 500, PhysicsRadialTargets.Particles);
        }

        if (keyboard.IsKeyPressed(Key.B))
        {
            crate.Position = mousePos;
            crate.SetVelocity(Vector2.Zero);
        }

        if (spoutOpen && water.Count < SpoutLimit)
        {
            Pour(water, spoutPosition, ref spoutDue, SpoutRate * dt);
        }

        if ((mouseData.Actions & VirtualAction.PrimaryAction) != 0)
            Pour(water, mousePos, ref pourDue, PourRate * dt);
        else if ((mouseData.Actions & VirtualAction.SecondaryAction) != 0)
            Pour(sand, mousePos, ref pourDue, PourRate * dt);

        if (raining)
        {
            Rain(RainRate * dt);
        }

        // Something to go by when nobody is looking at the window
        statusTimer += dt;
        updates++;
        if (statusTimer >= StatusInterval)
        {
            Console.WriteLine($"water: {water.Count} [{Describe(waterSimulator.Particles)}], sand: {sand.Count}, rain: {rain.Count}, {updates / statusTimer:0} updates/s");
            statusTimer = 0.0f;
            updates = 0;
        }
    }

    /// <summary>
    /// Helper method to let particles fall from a spot, spread out a little so they don't all start inside of each other.
    /// </summary>
    /// <param name="due">What is left over of a particle from the last time, so the rate holds whatever the frame time.</param>
    private static void Pour(ParticleRenderer2D renderer, Vector2 position, ref float due, float amount)
    {
        due += amount;
        Span<Particle2D> particles = stackalloc Particle2D[Math.Min((int)due, 64)];
        due -= (int)due;

        for (int i = 0; i < particles.Length; i++)
        {
            Vector2 offset = new((Random.Shared.NextSingle() - 0.5f) * 24, (Random.Shared.NextSingle() - 0.5f) * 12);
            particles[i] = new Particle2D(-Vector2.UnitY, position + offset, 120);
        }

        renderer.AddRange(particles);
    }

    /// <summary>
    /// Helper method to let rain fall from all along the top of the view.
    /// </summary>
    private void Rain(float amount)
    {
        Vector2 half = Engine.WindowManager.ViewportSize / 2.0f;

        rainDue += amount;
        Span<Particle2D> particles = stackalloc Particle2D[Math.Min((int)rainDue, 64)];
        rainDue -= (int)rainDue;

        for (int i = 0; i < particles.Length; i++)
        {
            float x = (Random.Shared.NextSingle() * 2 - 1) * (half.X - Wall * 2);
            particles[i] = new Particle2D(-Vector2.UnitY, new Vector2(x, half.Y - Wall), 200);
        }

        rain.AddRange(particles);
    }

    /// <summary>
    /// Helper method to say how many particles are in each basin and how high the surface of each one is.
    /// </summary>
    private string Describe(PhysicsFluidParticleGroup group)
    {
        Vector2 half = Engine.WindowManager.ViewportSize / 2.0f;
        Span<int> counts = stackalloc int[3];
        Span<float> tops = [-half.Y, -half.Y, -half.Y];

        foreach (ref readonly var particle in group.Particles)
        {
            int basin = particle.Position.X < -half.X / 3 ? 0 : particle.Position.X < half.X / 3 ? 1 : 2;
            counts[basin]++;
            tops[basin] = MathF.Max(tops[basin], particle.Position.Y);
        }

        return $"{counts[0]} up to {tops[0]:0}, {counts[1]} up to {tops[1]:0}, {counts[2]} up to {tops[2]:0}";
    }
}
