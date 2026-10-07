using System.Numerics;
using System.Runtime.InteropServices;

namespace Horizon.Physics.Simulation;

/// <summary>
/// A shape the way the compute shader has it, laid out exactly like the std430 <c>Shape</c> struct
/// in shaders/particle/simulate_physics.comp (40 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PhysicsGpuShape
{
    public Vector2 Min, Max;
    public Vector2 Velocity;
    public float Radius;
    public float IsCircle;      // 1 for a circle, 0 for a rectangle, 2 for an outline
    public Vector2 Range;       // For an outline: the first of its pieces among all of the pieces there are, and how many it has

    public const float OUTLINE = 2.0f;

    public PhysicsGpuShape(in PhysicsShape shape)
    {
        Min = shape.Min;
        Max = shape.Max;
        Velocity = shape.Velocity;
        Radius = shape.Radius;

        // An outline is one without any pieces until it is told where they are, which nothing runs into
        IsCircle = shape.Outline is not null ? OUTLINE : shape.IsCircle ? 1.0f : 0.0f;
        Range = Vector2.Zero;
    }
}

/// <summary>
/// The map as it was when the static grid was last rebuilt, in the form the compute shader takes it. Never changes once made.
/// </summary>
internal sealed class PhysicsParticleMap
{
    public required int Version { get; init; }
    public required PhysicsGpuShape[] Shapes { get; init; }

    // The start of every cell (one more than there are cells), followed by the shapes of every cell one after the other
    public required int[] Cells { get; init; }

    public required Vector2 Origin { get; init; }
    public required Vector2 Min { get; init; }
    public required Vector2 Max { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
}

/// <summary>
/// What the world tells particles that are simulated on the GPU, which can't be stepped along with everything else:
/// the world steps on its own thread and a compute shader can only be run from the GL one. So after every step the
/// world leaves what there is to run into here (the map, where the bodies are, the pushes that were asked for),
/// and the simulator comes to collect it when it is about to move its particles.
/// </summary>
internal sealed class PhysicsParticleFeed
{
    /// <summary>The most shapes of dynamic bodies the particles collide with, any more are left out.</summary>
    public const int MaxBodies = 64;

    /// <summary>The most pieces of outline all of the bodies can have between them, an outline that doesn't fit is left out.</summary>
    public const int MaxSegments = 1024;

    /// <summary>The most pushes that are kept for the particles to be given, any more are dropped.</summary>
    public const int MaxImpulses = 32;

    private readonly Lock _gate = new();
    private readonly PhysicsGpuShape[] _bodies = new PhysicsGpuShape[MaxBodies];
    private readonly List<Vector4> _impulses = [];

    // The pieces of every outline among the bodies, each as where it starts and ends in the world
    private readonly Vector4[] _segments = new Vector4[MaxSegments];
    private int _segmentCount;
    private PhysicsParticleMap? _map;
    private int _bodyCount;

    /// <summary>
    /// Leaves where everything is after a step of the world.
    /// </summary>
    /// <param name="bodies">The fixtures of the dynamic bodies, where they are now.</param>
    public void Publish(PhysicsStaticGrid grid, ReadOnlySpan<PhysicsShape> bodies)
    {
        // Copying the map out is only worth doing when it has changed, which it hardly ever does
        PhysicsParticleMap? map = _map;
        if (map is null || map.Version != grid.Version)
        {
            map = CreateMap(grid);
        }

        lock (_gate)
        {
            _map = map;
            _bodyCount = Math.Min(bodies.Length, MaxBodies);
            _segmentCount = 0;

            for (int i = 0; i < _bodyCount; i++)
            {
                _bodies[i] = new PhysicsGpuShape(bodies[i]);

                if (bodies[i].Outline is not { } outline) continue;

                int pieces = bodies[i].OutlineCount / 2;
                if (_segmentCount + pieces > MaxSegments) continue;

                _bodies[i].Range = new Vector2(_segmentCount, pieces);
                for (int piece = 0; piece < pieces; piece++)
                {
                    _segments[_segmentCount++] = new Vector4(bodies[i].Origin + outline[piece * 2], bodies[i].Origin.X + outline[piece * 2 + 1].X, bodies[i].Origin.Y + outline[piece * 2 + 1].Y);
                }
            }
        }
    }

    private static PhysicsParticleMap CreateMap(PhysicsStaticGrid grid)
    {
        var shapes = new PhysicsGpuShape[grid.Shapes.Length];
        for (int i = 0; i < shapes.Length; i++)
        {
            shapes[i] = new PhysicsGpuShape(grid.Shapes[i]);
        }

        return new PhysicsParticleMap
        {
            Version = grid.Version,
            Shapes = shapes,
            Cells = [.. grid.CellStart, .. grid.CellShapes],
            Origin = grid.Min,
            Min = grid.Min,
            Max = grid.Max,
            Width = grid.Width,
            Height = grid.Height
        };
    }

    /// <summary>
    /// Leaves a push for the particles: everything within the radius is launched away from the centre, the closer the harder.
    /// </summary>
    public void AddImpulse(Vector2 centre, float radius, float impulse)
    {
        lock (_gate)
        {
            if (_impulses.Count < MaxImpulses)
            {
                _impulses.Add(new Vector4(centre, radius, impulse));
            }
        }
    }

    /// <summary>
    /// Collects what was left since the last time.
    /// </summary>
    /// <param name="bodies">Filled with the shapes of the dynamic bodies, has to have room for <see cref="MaxBodies"/>.</param>
    /// <param name="impulses">Filled with as many of the waiting pushes as fit (centre, radius, impulse), the rest stay for the next time.</param>
    /// <param name="segments">Filled with the pieces of the outlines the bodies have (xy to zw), has to have room for <see cref="MaxSegments"/>.</param>
    /// <returns>The map, null until the world has stepped for the first time.</returns>
    public PhysicsParticleMap? Collect(Span<PhysicsGpuShape> bodies, out int bodyCount, Span<Vector4> impulses, out int impulseCount, Span<Vector4> segments, out int segmentCount)
    {
        lock (_gate)
        {
            bodyCount = _bodyCount;
            _bodies.AsSpan(0, bodyCount).CopyTo(bodies);

            segmentCount = _segmentCount;
            _segments.AsSpan(0, segmentCount).CopyTo(segments);

            impulseCount = Math.Min(_impulses.Count, impulses.Length);
            for (int i = 0; i < impulseCount; i++)
            {
                impulses[i] = _impulses[i];
            }
            _impulses.RemoveRange(0, impulseCount);

            return _map;
        }
    }
}
