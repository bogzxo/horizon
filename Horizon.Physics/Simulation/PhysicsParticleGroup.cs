using System.Numerics;

using Horizon.Rendering.Particles.Simulation;

namespace Horizon.Physics.Simulation;

/// <summary>
/// A set of particles that the physics world simulates as small dynamic bodies: they fall, land on the map, bounce,
/// slide to a stop, and are shoved out of the way by the dynamic bodies moving through them (the players).
/// They are made with <see cref="PhysicsWorld.CreateParticleGroup"/> and stepped by the world along with everything else.
/// A particle is a lot lighter than a real body though: it is a circle that never turns, it doesn't push anything back
/// and particles pass through one another, which is what allows there to be thousands of them.
/// Everything here has to be called from the thread the physics runs on.
/// </summary>
public class PhysicsParticleGroup
{
    private const float REST_SPEED = 45.0f;     // Slower than this into a surface and the particle stays down instead of bouncing
    private const int MAX_SUBSTEPS = 4;         // How far a fast particle's step can be split up, so it can't skip through a tile
    private const int SOLVER_PASSES = 2;        // Being pushed out of one shape can push into the next (a body pressing into the floor)
    private const float SLEEP_SPEED = 3.0f;     // Slower than this while lying on the map and the particle is left alone until disturbed
    private protected const float LOST_MARGIN = 64.0f;    // How far past the edge of the map a particle has to fall before it is given up on

    private protected ParticleState2D[] _particles = [];

    // A particle that has come to rest on the map is asleep: it isn't moved or collided until a body or a push wakes it up.
    // Most particles spend most of their life like this, so it is what keeps large numbers of them cheap.
    private bool[] _asleep = [];
    private int _mapVersion;
    private Vector2 _gravity;

    /// <summary>
    /// How many particles there are right now.
    /// </summary>
    public int Count { get; private protected set; }

    /// <summary>
    /// How many particles there can be, the ones that don't fit are dropped.
    /// </summary>
    public int Capacity
    {
        get => _particles.Length;
        set
        {
            if (value == _particles.Length) return;

            Array.Resize(ref _particles, value);
            Array.Resize(ref _asleep, value);
            Count = Math.Min(Count, value);
        }
    }

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

    // How much of the speed of a body a particle takes on when the body shoves it out of the way. Bodies are a lot
    // faster than anything a particle does by itself, taking on all of it has them fired off rather than pushed aside.
    public float BodyPush { get; set; } = 0.3f;

    // The fastest (per second) a body can send a particle off at, however fast the body itself is going
    public float BodyPushLimit { get; set; } = 120.0f;

    // The acceleration of every particle, these don't use the gravity of the world as they are usually a lot floatier
    public Vector2 Gravity
    {
        get => _gravity;
        set
        {
            if (value == _gravity) return;

            // What was resting might not be any more
            _gravity = value;
            WakeAll();
        }
    }

    // How long (in seconds) a particle lasts, each one ages at its own ParticleState2D.Rate
    public float MaxAge { get; set; } = 2.5f;

    /// <summary>
    /// The particles as they are right now, their order changes from step to step.
    /// </summary>
    public ReadOnlySpan<ParticleState2D> Particles => _particles.AsSpan(0, Count);

    internal PhysicsParticleGroup(int capacity)
    {
        Capacity = capacity;
    }

    /// <summary>
    /// Adds particles to the group, as many as there is still room for.
    /// </summary>
    public void Add(ReadOnlySpan<ParticleState2D> particles)
    {
        int room = Math.Min(particles.Length, _particles.Length - Count);
        particles[..room].CopyTo(_particles.AsSpan(Count));
        _asleep.AsSpan(Count, room).Clear();
        Count += room;
    }

    private void WakeAll() => _asleep.AsSpan(0, Count).Clear();

    /// <summary>
    /// Removes every particle.
    /// </summary>
    public virtual void Clear() => Count = 0;

    /// <summary>
    /// Launches every particle within the radius away from the centre, the closer the harder.
    /// </summary>
    internal void ApplyRadial(Vector2 centre, float radius, float impulse)
    {
        if (Mass <= 0.0f) return;

        float radiusSquared = radius * radius;
        var particles = _particles.AsSpan(0, Count);

        for (int i = 0; i < particles.Length; i++)
        {
            ref ParticleState2D p = ref particles[i];

            Vector2 away = p.Position - centre;
            float distanceSquared = away.LengthSquared();
            if (distanceSquared >= radiusSquared) continue;

            // Anything sat exactly on the centre goes straight up
            float distance = MathF.Sqrt(distanceSquared);
            Vector2 direction = distance > 0.0001f ? away / distance : Vector2.UnitY;

            p.Velocity += direction * (impulse * (1.0f - distance / radius) / Mass);
            _asleep[i] = false;
        }
    }

    /// <summary>
    /// Moves every particle on by one step of the world.
    /// </summary>
    /// <param name="bodies">The fixtures of the dynamic bodies, where they are now.</param>
    /// <param name="bodiesMin">The corners of the box around all of <paramref name="bodies"/>.</param>
    internal virtual void Step(PhysicsStaticGrid grid, ReadOnlySpan<PhysicsShape> bodies, Vector2 bodiesMin, Vector2 bodiesMax, float dt)
    {
        var particles = _particles;
        var asleep = _asleep;
        int live = Count;

        // The floor might be gone from under whoever was resting on it
        if (grid.Version != _mapVersion)
        {
            _mapVersion = grid.Version;
            WakeAll();
        }

        // Grown by our radius, so testing a particle against the box is just testing its centre
        bodiesMin -= new Vector2(Radius);
        bodiesMax += new Vector2(Radius);

        GetLostBounds(grid, out Vector2 lostMin, out Vector2 lostMax);

        float decay = MaxAge > 0.0f ? dt / MaxAge : 0.0f;
        Vector2 gravityStep = Gravity * dt;
        float airDrag = MathF.Max(0.0f, 1.0f - LinearDrag * dt);
        float slide = MathF.Max(0.0f, 1.0f - Friction * dt);

        // Never move further than this without testing for collisions
        float safeDistance = MathF.Max(Radius, 4.0f);

        for (int i = 0; i < live;)
        {
            ref ParticleState2D p = ref particles[i];
            p.Life -= decay * p.Rate;

            bool lost = p.Position.X < lostMin.X || p.Position.X > lostMax.X ||
                        p.Position.Y < lostMin.Y || p.Position.Y > lostMax.Y;

            if (p.Life <= 0.0f || lost)
            {
                // Swap-remove: the last live particle (not yet updated this step) takes this slot,
                // then we re-process index i without advancing.
                live--;
                if (i != live)
                {
                    p = particles[live];
                    asleep[i] = asleep[live];
                }
                continue;
            }

            bool nearBodies = p.Position.X >= bodiesMin.X && p.Position.X <= bodiesMax.X &&
                              p.Position.Y >= bodiesMin.Y && p.Position.Y <= bodiesMax.Y;

            if (asleep[i])
            {
                // Only a body coming past can disturb it (pushes wake it up themselves)
                if (!nearBodies || !IsTouchingBody(p.Position, bodies))
                {
                    i++;
                    continue;
                }

                asleep[i] = false;
            }

            p.Velocity = (p.Velocity + gravityStep) * airDrag;

            // Only the fast ones need their step split up, for the rest the square root can be skipped as well
            int substeps = 1;
            float distanceSquared = p.Velocity.LengthSquared() * dt * dt;
            if (distanceSquared > safeDistance * safeDistance)
            {
                substeps = Math.Min((int)MathF.Ceiling(MathF.Sqrt(distanceSquared) / safeDistance), MAX_SUBSTEPS);
            }
            float substepTime = dt / substeps;

            bool supported = false, shoved = false;
            for (int step = 0; step < substeps; step++)
            {
                Vector2 before = p.Position;
                p.Position += p.Velocity * substepTime;

                bool touchedAnything = false;
                for (int pass = 0; pass < SOLVER_PASSES; pass++)
                {
                    bool touched = CollideWithMap(ref p, grid, slide, ref supported);
                    if (nearBodies && CollideWithBodies(ref p, bodies, slide))
                    {
                        touched = shoved = true;
                    }

                    if (!touched) break;
                    touchedAnything = true;
                }

                // Being pushed out of one tile can end inside the one next to it, and a body can press a particle into the floor
                // harder than the floor pushes back (a roll going over it). The map always wins: rather than end up inside of it
                // the particle stays where it was. Unless it was inside already (spawned there), then it has to be let out.
                if (touchedAnything && grid.Contains(p.Position) && !grid.Contains(before))
                {
                    p.Position = before;
                    p.Velocity = Vector2.Zero;
                    break;
                }
            }

            // Lying still on the map with nothing pushing it, there is nothing more to work out until that changes
            asleep[i] = supported && !shoved && p.Velocity.LengthSquared() < SLEEP_SPEED * SLEEP_SPEED;

            i++;
        }

        Count = live;
    }

    /// <summary>
    /// Helper method to find the box a particle has to stay in to still be worth simulating:
    /// one that gravity has taken past the edge of the map has nothing left to land on.
    /// </summary>
    private protected void GetLostBounds(PhysicsStaticGrid grid, out Vector2 lostMin, out Vector2 lostMax)
    {
        lostMin = new(float.MinValue);
        lostMax = new(float.MaxValue);
        if (grid.IsEmpty) return;

        if (_gravity.X < 0) lostMin.X = grid.Min.X - LOST_MARGIN;
        if (_gravity.X > 0) lostMax.X = grid.Max.X + LOST_MARGIN;
        if (_gravity.Y < 0) lostMin.Y = grid.Min.Y - LOST_MARGIN;
        if (_gravity.Y > 0) lostMax.Y = grid.Max.Y + LOST_MARGIN;
    }

    /// <param name="supported">Set when something of the map is holding the particle up against gravity.</param>
    private bool CollideWithMap(ref ParticleState2D p, PhysicsStaticGrid grid, float slide, ref bool supported)
    {
        Vector2 reach = new(Radius);
        if (!grid.TryGetCells(p.Position - reach, p.Position + reach, out int x0, out int y0, out int x1, out int y1)) return false;

        bool touched = false;
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                foreach (int index in grid.GetShapes(x, y))
                {
                    ref readonly PhysicsShape shape = ref grid[index];
                    if (!shape.Overlaps(p.Position, Radius, out Vector2 normal, out float depth)) continue;

                    Respond(ref p, normal, depth, Vector2.Zero, slide, Restitution);
                    touched = true;
                    supported |= Vector2.Dot(normal, _gravity) < 0.0f;
                }
            }
        }

        return touched;
    }

    /// <summary>
    /// Helper method to test if a particle is within reach of any body, going by the boxes around their fixtures.
    /// The box around all of the bodies isn't enough to wake a particle up on: with two players stood apart
    /// it covers the whole floor between them.
    /// </summary>
    private bool IsTouchingBody(Vector2 position, ReadOnlySpan<PhysicsShape> bodies)
    {
        for (int i = 0; i < bodies.Length; i++)
        {
            ref readonly PhysicsShape shape = ref bodies[i];
            if (position.X + Radius >= shape.Min.X && position.X - Radius <= shape.Max.X &&
                position.Y + Radius >= shape.Min.Y && position.Y - Radius <= shape.Max.Y) return true;
        }

        return false;
    }

    private bool CollideWithBodies(ref ParticleState2D p, ReadOnlySpan<PhysicsShape> bodies, float slide)
    {
        bool touched = false;
        for (int i = 0; i < bodies.Length; i++)
        {
            ref readonly PhysicsShape shape = ref bodies[i];
            if (!shape.Overlaps(p.Position, Radius, out Vector2 normal, out float depth)) continue;

            // A body is soft compared to the map: nothing bounces off of it
            Respond(ref p, normal, depth, GetBodyPush(shape), slide, 0.0f);
            touched = true;
        }

        return touched;
    }

    /// <summary>
    /// Helper method to work out how fast a body is going as far as a particle it runs into is concerned,
    /// see <see cref="BodyPush"/> and <see cref="BodyPushLimit"/>.
    /// </summary>
    private protected Vector2 GetBodyPush(in PhysicsShape shape)
    {
        Vector2 push = shape.Velocity * BodyPush;
        float speedSquared = push.LengthSquared();

        return speedSquared > BodyPushLimit * BodyPushLimit ? push * (BodyPushLimit / MathF.Sqrt(speedSquared)) : push;
    }

    /// <summary>
    /// Helper method to get a particle out of whatever it ran into, and to work out the speed it leaves with.
    /// </summary>
    /// <param name="surfaceVelocity">How fast the thing we ran into is moving, this is what lets a body shove particles along.</param>
    /// <param name="restitution">How much of its speed the particle keeps if it hit hard enough to bounce.</param>
    private void Respond(ref ParticleState2D p, Vector2 normal, float depth, Vector2 surfaceVelocity, float slide, float restitution)
    {
        p.Position += normal * depth;

        // Everything from here is as seen from the surface, so hitting a moving body works just like hitting the floor
        Vector2 velocity = p.Velocity - surfaceVelocity;
        float into = Vector2.Dot(velocity, normal);

        // Already on our way out
        if (into >= 0.0f) return;

        // Take away the speed going into the surface, and give some back if it was a hard enough hit to bounce
        float bounce = -into > REST_SPEED ? restitution : 0.0f;
        velocity -= normal * (into * (1.0f + bounce));

        // Whatever is left along the surface is slowed by friction
        float along = Vector2.Dot(velocity, normal);
        Vector2 tangent = velocity - normal * along;
        velocity -= tangent * (1.0f - slide);

        p.Velocity = velocity + surfaceVelocity;
    }
}
