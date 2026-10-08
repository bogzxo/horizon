using System.Numerics;
using System.Runtime.CompilerServices;

using Horizon.Engine;
using Horizon.Graphics;
using Horizon.Rendering.Particles.Simulation;

namespace Horizon.Physics.Simulation;

/// <summary>
/// Simulates the particles of a renderer on the GPU (shaders/particle/simulate_physics.slang) as small dynamic bodies of a
/// <see cref="PhysicsWorld"/>. They fall, land on the map, bounce, slide to a stop, and are shoved out of the way by the
/// dynamic bodies moving through them (the players). A particle is a lot lighter than a real body though. It is a circle
/// that never turns, it doesn't push anything back and particles pass through one another.
/// The particles never leave the GPU, so the world can't step them itself. It tells this class what there is to run into
/// instead (see <see cref="PhysicsParticleFeed"/>), which is uploaded for the compute shader to test against.
/// For particles that have to collide with each other there is <see cref="PhysicsFluidParticleSimulator2D"/>.
/// </summary>
public sealed class PhysicsParticleSimulator2D : ComputeParticleSimulator2D
{
    /// <summary>Must match MAX_IMPULSES in simulate_physics.slang.</summary>
    private const int MaxImpulses = 8;

    private const float LOST_MARGIN = 64.0f;    // How far past the edge of the map a particle has to fall before it is given up on

    // Must match the bindings in simulate_physics.slang
    private const uint MAP_SHAPE_BINDING = 3;
    private const uint MAP_CELL_BINDING = 4;
    private const uint BODY_BINDING = 5;
    private const uint SEGMENT_BINDING = 6;

    private readonly PhysicsWorld world;
    private readonly PhysicsParticleFeed feed;

    private readonly PhysicsGpuShape[] bodies = new PhysicsGpuShape[PhysicsParticleFeed.MaxBodies];
    private readonly Vector4[] impulses = new Vector4[MaxImpulses];
    private readonly Vector4[] segments = new Vector4[PhysicsParticleFeed.MaxSegments];
    private GpuBuffer? segmentBuffer;

    // The map only changes when the world rebuilds its grid, so it is uploaded once and kept
    private GpuBuffer? mapShapeBuffer, mapCellBuffer, bodyBuffer;
    private PhysicsParticleMap? uploadedMap;

    // The size of every particle
    public float Radius { get; set; } = 1.0f;

    // The mass of every particle, heavier ones are harder to launch (see PhysicsWorld.ApplyRadialImpulse)
    public float Mass { get; set; } = 1.0f;

    // How much of its speed a particle keeps when it bounces off something
    public float Restitution { get; set; } = 0.3f;

    // How quickly a particle sliding along a surface comes to a stop
    public float Friction { get; set; } = 6.0f;

    // How quickly a particle slows down in the air
    public float LinearDrag { get; set; } = 0.0f;

    /* None of the following is physics, it is there for the look of things */

    // The speed a particle is brought to for as long as it is in the air, and how quickly (0 for not at all, which
    // leaves the air to gravity and drag). For what falls at one speed from the moment it shows up, wind and all,
    // rain, snow. Once it has landed it is left to lie
    public Vector2 Cruise { get; set; } = Vector2.Zero;
    public float CruiseRate { get; set; } = 0.0f;

    // For what splashes rather than bounces. A particle that lands hard stops where it comes down and runs off
    // along the surface instead, to either side and at any speed up to this one. 0 for none of that
    public float Splash { get; set; } = 0.0f;

    // How much of the life it has left such a landing can cost a particle (0 to 1), a different share for each of
    // them, what has splashed doesn't all go at the same moment
    public float SplashFade { get; set; } = 0.0f;

    // How much of the speed of a body a particle takes on when the body shoves it out of the way. Bodies are a lot
    // faster than anything a particle does by itself, taking on all of it has them fired off rather than pushed aside
    public float BodyPush { get; set; } = 0.3f;

    // The fastest (per second) a body can send a particle off at, however fast the body itself is going
    public float BodyPushLimit { get; set; } = 120.0f;

    public PhysicsParticleSimulator2D(in PhysicsWorld world)
    {
        this.world = world;
        feed = world.CreateParticleFeed();
    }

    protected override Shader? CreateShader() =>
        GameEngine.Instance.ObjectManager.Shaders.TryCreateOrGet("particle2d_simulate_physics", ShaderDescription.FromPath("shaders/particle", "simulate_physics"), out var shader)
            ? shader.Asset
            : null;

    protected override void BindSimulation(Technique technique)
    {
        var map = feed.Collect(bodies, out int bodyCount, impulses, out int impulseCount, segments, out int segmentCount);

        if (map is not null && map != uploadedMap)
        {
            UploadMap(map);
        }

        // Until the map is up there is nothing to land on, the particles simply fall
        bool hasMap = uploadedMap is not null && mapShapeBuffer is not null && mapCellBuffer is not null;
        technique.SetUniform("uMapWidth", hasMap ? uploadedMap!.Width : 0);
        technique.SetUniform("uMapHeight", hasMap ? uploadedMap!.Height : 0);
        technique.SetUniform("uMapCellCount", hasMap ? uploadedMap!.Width * uploadedMap.Height : 0);
        technique.SetUniform("uMapCellSize", PhysicsStaticGrid.CELL_SIZE);

        Vector2 lostMin = new(float.MinValue), lostMax = new(float.MaxValue);
        if (hasMap)
        {
            Vector2 origin = uploadedMap!.Origin;
            technique.SetUniform("uMapOrigin", in origin);
            technique.BindBuffer(MAP_SHAPE_BINDING, mapShapeBuffer!);
            technique.BindBuffer(MAP_CELL_BINDING, mapCellBuffer!);

            // A particle that gravity has taken past the edge of the map has nothing left to land on
            Vector2 gravity = Renderer.Gravity;
            if (gravity.X < 0) lostMin.X = uploadedMap.Min.X - LOST_MARGIN;
            if (gravity.X > 0) lostMax.X = uploadedMap.Max.X + LOST_MARGIN;
            if (gravity.Y < 0) lostMin.Y = uploadedMap.Min.Y - LOST_MARGIN;
            if (gravity.Y > 0) lostMax.Y = uploadedMap.Max.Y + LOST_MARGIN;
        }
        technique.SetUniform("uLostMin", in lostMin);
        technique.SetUniform("uLostMax", in lostMax);

        BindBodies(technique, bodyCount);

        // The outlines some of the bodies have, they change with every frame of an animation
        segmentBuffer ??= CreateBuffer((uint)(PhysicsParticleFeed.MaxSegments * Unsafe.SizeOf<Vector4>()));
        if (segmentBuffer is not null)
        {
            if (segmentCount > 0) segmentBuffer.Update<Vector4>(segments.AsSpan(0, segmentCount));
            technique.BindBuffer(SEGMENT_BINDING, segmentBuffer);
        }

        // Heavier particles are harder to launch
        float mass = Mass;
        technique.SetUniform("uImpulseCount", mass > 0.0f ? impulseCount : 0);
        for (int i = 0; i < impulseCount && mass > 0.0f; i++)
        {
            Vector4 impulse = impulses[i] with { W = impulses[i].W / mass };
            technique.SetUniform("uImpulses", i, in impulse);
        }

        technique.SetUniform("uRadius", Radius);
        technique.SetUniform("uRestitution", Restitution);
        technique.SetUniform("uFriction", Friction);
        technique.SetUniform("uLinearDrag", LinearDrag);

        Vector2 cruise = Cruise;
        technique.SetUniform("uCruise", in cruise);
        technique.SetUniform("uCruiseRate", MathF.Max(0.0f, CruiseRate));
        technique.SetUniform("uSplash", MathF.Max(0.0f, Splash));
        technique.SetUniform("uSplashFade", Math.Clamp(SplashFade, 0.0f, 1.0f));
        technique.SetUniform("uBodyPush", BodyPush);
        technique.SetUniform("uBodyPushLimit", BodyPushLimit);
    }

    /// <summary>Helper method to hand the shader where the dynamic bodies are, they move every step so this is done every time.</summary>
    private void BindBodies(Technique technique, int bodyCount)
    {
        bodyBuffer ??= CreateBuffer((uint)(PhysicsParticleFeed.MaxBodies * Unsafe.SizeOf<PhysicsGpuShape>()));
        if (bodyBuffer is null) bodyCount = 0;

        // Most particles are nowhere near a body, one box around all of them rules those out in a single test
        Vector2 bodiesMin = new(float.MaxValue), bodiesMax = new(float.MinValue);
        for (int i = 0; i < bodyCount; i++)
        {
            bodiesMin = Vector2.Min(bodiesMin, bodies[i].Min);
            bodiesMax = Vector2.Max(bodiesMax, bodies[i].Max);
        }

        // Grown by our radius, so testing a particle against the box is just testing its centre
        bodiesMin -= new Vector2(Radius);
        bodiesMax += new Vector2(Radius);

        technique.SetUniform("uBodyCount", bodyCount);
        technique.SetUniform("uBodiesMin", in bodiesMin);
        technique.SetUniform("uBodiesMax", in bodiesMax);

        if (bodyBuffer is null) return;

        if (bodyCount > 0)
        {
            bodyBuffer.Update<PhysicsGpuShape>(bodies.AsSpan(0, bodyCount));
        }
        technique.BindBuffer(BODY_BINDING, bodyBuffer);
    }

    /// <summary>Helper method to replace the map the shader collides against. The buffers can't be resized, so they are made anew.</summary>
    private void UploadMap(PhysicsParticleMap map)
    {
        ReleaseBuffer(ref mapShapeBuffer);
        ReleaseBuffer(ref mapCellBuffer);
        uploadedMap = map;

        // A map without anything in it has nothing to upload, and a buffer can't be empty
        if (map.Shapes.Length == 0 || map.Width == 0) return;

        mapShapeBuffer = CreateBuffer((uint)(map.Shapes.Length * Unsafe.SizeOf<PhysicsGpuShape>()));
        mapCellBuffer = CreateBuffer((uint)(map.Cells.Length * sizeof(int)));
        if (mapShapeBuffer is null || mapCellBuffer is null) return;

        mapShapeBuffer.Update<PhysicsGpuShape>(map.Shapes.AsSpan());
        mapCellBuffer.Update<int>(map.Cells.AsSpan());
    }

    private static GpuBuffer? CreateBuffer(uint sizeInBytes)
    {
        if (!GameEngine.Instance.ObjectManager.Buffers.TryCreate(new BufferDescription(BufferUsage.Storage, BufferAccess.Dynamic, sizeInBytes), out var result))
            return null;

        return result.Asset;
    }

    private static void ReleaseBuffer(ref GpuBuffer? buffer)
    {
        if (buffer is null) return;

        GameEngine.Instance.ObjectManager.Buffers.Remove(buffer);
        buffer = null;
    }

    public override void Dispose()
    {
        // The world would otherwise keep leaving word for particles nobody draws
        world.RemoveParticleFeed(feed);

        ReleaseBuffer(ref mapShapeBuffer);
        ReleaseBuffer(ref mapCellBuffer);
        ReleaseBuffer(ref bodyBuffer);
        ReleaseBuffer(ref segmentBuffer);

        base.Dispose();
    }
}
