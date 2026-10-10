using System.Numerics;

using Horizon.Rendering.Particles.Simulation;

namespace Horizon.Physics.Simulation;

/// <summary>
/// A set of particles that, on top of everything a <see cref="PhysicsParticleGroup"/> does, collide with one another.
/// They can't share a spot, so they stack up where they land, run off the top of the pile and fill whatever they
/// are poured into until it overflows. Enough of them behave like a liquid (or like sand, without any spread).
/// Bodies wading through them push them out of the way, which raises the level around them.
/// They are made with <see cref="PhysicsWorld.CreateFluidParticleGroup"/> and stepped by the world along with everything else.
/// All of this costs more per particle than the plain group. There is no sleeping here (what is resting is still
/// holding up what lies on top of it) and every particle is pushed out of its neighbours several times a step.
/// Particles of different groups still pass through each other.
/// <see cref="PhysicsParticleGroup.Restitution"/> is not used, what is packed this tightly doesn't bounce.
/// </summary>
public sealed class PhysicsFluidParticleGroup : PhysicsParticleGroup
{
    private const float BODY_STEP = 2.0f;       // The furthest a body moves a particle out of its way in a pass. The few passes of a step have to
                                                // stay under half a tile between them, or a fast body would plough particles through the map
    private const float CONTACT_RANGE = 1.25f;  // How far apart (in diameters) two particles can be and still be worth keeping an eye on
    private const float SPREAD_RANGE = 1.5f;    // How far (in diameters) the particles of a pile keep nudging each other away
    private const float SPREAD_RATE = 80.0f;    // How far they nudge at a Spread of 1, in diameters a second (for two right up against each other it is a ninth of this)
    private const float SETTLE_RATE = 0.25f;    // How much (in diameters) of an old overlap two particles are put apart by every step
    private const float FLOW_TIME = 0.08f;      // How long (in seconds) the speed of a particle is averaged over for Flow

    // Where every particle was before it was moved, how fast it is going is worked out from how far it got
    private Vector2[] _previous = [];

    // The way the map pushed a particle this step (zero if it didn't), sliding along it is slowed by friction
    private Vector2[] _contacts = [];

    // Where every particle was before anything at all was done to it this step, to go back to if all else fails
    private Vector2[] _origins = [];

    // The shove a body gave a particle this step (zero if none did), the speed it is sent off at, see BodyPush
    private Vector2[] _shoves = [];

    // Whether a particle is close enough to the map or to a body to run into it this step
    private bool[] _nearMap = [], _nearBodies = [];

    // How fast every particle has been going lately (see Flow), which the first _flowCount of them have been
    // around for long enough to have
    private Vector2[] _flow = [];
    private int _flowCount;

    // The particles are sorted into the cells of a grid, which is how each one finds its neighbours.
    // The grid has no edges, cells are hashed into a table, so two of them can share a slot (which only costs a few wasted tests).
    // The particles in slot i are _cellParticles[_cellStart[i].._cellStart[i + 1]]
    private int[] _cellStart = [0];
    private int[] _cellParticles = [];
    private int[] _cellOf = [];
    private int _cellMask;

    // Every two particles close enough to matter this step, one after the other. For each pair there is how far apart
    // the two have to be kept and the way from the first to the second before they were moved (x, y), see Settle
    private int[] _pairs = [];
    private float[] _pairGaps = [];
    private float[] _pairNormals = [];
    private int _pairCount;

    /// <summary>
    /// How many times a step the particles are pushed apart. More of them make for a stiffer pile that sags less
    /// under its own weight, at the price of every one being a pass over all of the particles.
    /// </summary>
    public int Iterations { get; set; } = 6;

    /// <summary>
    /// How readily a pile flattens itself out. At 0 the particles heap up like sand and only run off where the heap
    /// gets too steep, the higher this is the more they keep nudging each other apart until the surface is level
    /// (1 is about water, which never quite stops stirring).
    /// Spreading costs more, every particle has to look further for its neighbours.
    /// </summary>
    public float Spread { get; set; } = 1.0f;

    /// <summary>
    /// The longest (in seconds) any particle is kept, 0 for no limit. <see cref="PhysicsParticleGroup.MaxAge"/> is what a
    /// particle fades over, and each one does so at its own rate, the slow ones take up to twice as long. This cuts them
    /// all off at the same age, so what was poured is certain to be gone (and its room free again) by then.
    /// </summary>
    public float MaxLife { get; set; } = 0.0f;

    /// <summary>
    /// How fast every particle is going with the last few steps averaged out, in the order of <see cref="PhysicsParticleGroup.Particles"/>.
    /// This is the speed to draw them by (to stretch or blur them along the way they move). The one a particle
    /// has from one step to the next is no good for that. What is packed into a pile is forever being put a little
    /// this way and that by its neighbours, which is speed as far as the numbers go but none that anybody sees.
    /// Here a pool lies as good as still, and what falls into it does not.
    /// </summary>
    public ReadOnlySpan<Vector2> Flow
    {
        get
        {
            TrackNew();
            return _flow.AsSpan(0, Count);
        }
    }

    internal PhysicsFluidParticleGroup(int capacity) : base(capacity)
    {
    }

    public override void Clear()
    {
        base.Clear();
        _flowCount = 0;
    }

    /// <summary>
    /// Helper method to start the particles off that were added since the last time, so far they have only ever gone
    /// as fast as they were let go.
    /// </summary>
    private void TrackNew()
    {
        if (_flow.Length != _particles.Length) Array.Resize(ref _flow, _particles.Length);

        for (int i = Math.Min(_flowCount, Count); i < Count; i++)
        {
            _flow[i] = _particles[i].Velocity;
        }

        _flowCount = Count;
    }

    internal override void Step(PhysicsStaticGrid grid, ReadOnlySpan<PhysicsShape> bodies, Vector2 bodiesMin, Vector2 bodiesMax, float dt)
    {
        if (dt <= 0.0f) return;

        var particles = _particles;
        EnsureScratch(particles.Length);
        TrackNew();

        int live = RemoveDead(grid, dt);
        Count = live;
        _flowCount = live;
        if (live == 0) return;

        // Nobody moves further than their own size in a step. Any further and a particle could end up on the far side of
        // another one (or of a tile) without ever having touched it. Splitting the step up for the fast ones (as the plain
        // particles do) means doing all of the work below again for every one of them, so they are held to this instead.
        // It is as fast as anything here falls, which makes small particles slow ones.
        float diameter = MathF.Max(Radius * 2.0f, 0.5f);
        float safeDistance = diameter;
        float maxSpeed = safeDistance / dt;

        // Grown by how far a particle can reach in a step, so testing one against the box is just testing its centre
        bodiesMin -= new Vector2(Radius + safeDistance);
        bodiesMax += new Vector2(Radius + safeDistance);

        Vector2 gravityStep = Gravity * dt;
        float airDrag = MathF.Max(0.0f, 1.0f - LinearDrag * dt);
        float slide = MathF.Max(0.0f, 1.0f - Friction * dt);
        float range = diameter * (Spread > 0.0f ? SPREAD_RANGE : CONTACT_RANGE);
        int iterations = Math.Max(Iterations, 1);

        // How far a particle can end up from where it is headed, put there by its neighbours or by a body
        // (which gets to move it once every other pass)
        float mapReach = Radius * 3.0f + safeDistance + (bodies.Length > 0 ? BODY_STEP * ((iterations + 1) / 2) : 0.0f);

        // The nudge is measured against the size of the particles, so piles of big and of small ones flatten out alike
        float spread = Spread * SPREAD_RATE * diameter * dt;

        // 1. Move everyone to where they would end up if nothing was in their way
        for (int i = 0; i < live; i++)
        {
            ref ParticleState2D p = ref particles[i];
            p.Velocity = (p.Velocity + gravityStep) * airDrag;

            Vector2 move = p.Velocity * dt;
            float distanceSquared = move.LengthSquared();
            if (distanceSquared > safeDistance * safeDistance)
            {
                move *= safeDistance / MathF.Sqrt(distanceSquared);
            }

            _previous[i] = p.Position;
            _origins[i] = p.Position;
            _contacts[i] = Vector2.Zero;
            _shoves[i] = Vector2.Zero;
            p.Position += move;

            // Most of a pool is nowhere near the map or a body, those never have to be tested against either
            _nearMap[i] = IsNearMap(p.Position, mapReach, grid);
            _nearBodies[i] = p.Position.X >= bodiesMin.X && p.Position.X <= bodiesMax.X &&
                             p.Position.Y >= bodiesMin.Y && p.Position.Y <= bodiesMax.Y;
        }

        // 2. Work out who is close enough to whom to matter, once. The passes below only go over this list
        SortIntoCells(live, range);
        FindPairs(live, range);

        // 3. Whatever was overlapping before anyone moved (let go on top of each other, or squeezed further than
        // the passes could undo) is put apart a little at a time, and what is merely close is nudged apart (the spread).
        // Where it was is moved along with it, so that none of this is taken for speed. It is what keeps a deep pool
        // from boiling, a crowded spot from firing particles off and what is poured from spraying all over.
        Settle(live, diameter, range, spread, grid);

        // 4. Then push them out of each other, the map and the bodies. Getting out of one thing can mean ending up in
        // the next, so it is done over and over, every pass gets the whole lot a bit closer to where nothing overlaps.
        for (int pass = 0; pass < iterations; pass++)
        {
            SeparatePairs(diameter);

            // The few that are near something solid only get out of it every other pass, which is plenty
            // as long as the last pass is one of them, nothing may be left inside of the map
            if (((iterations - 1 - pass) & 1) != 0) continue;

            for (int i = 0; i < live; i++)
            {
                if (_nearBodies[i]) SeparateFromBodies(ref particles[i].Position, ref _previous[i], ref _shoves[i], bodies);

                // The map goes last as it is the one thing which never gives way
                if (_nearMap[i]) SeparateFromMap(ref particles[i].Position, ref _contacts[i], grid);
            }
        }

        // 5. How far everyone got in the end is how fast they are going
        float flowBlend = 1.0f - MathF.Exp(-dt / FLOW_TIME);

        for (int i = 0; i < live; i++)
        {
            ref ParticleState2D p = ref particles[i];
            Vector2 contact = _contacts[i];

            // The weight of a pile (or a body) can press a particle into the map harder than the map pushes back.
            // The map always wins, rather than end up inside of it the particle stays where it was.
            // Unless it was inside already (spawned there), then it has to be let out.
            // Where it was is taken from before anything was done to it, settling and bodies move that along with them.
            if (contact != Vector2.Zero && grid.Contains(p.Position) && !grid.Contains(_origins[i]))
            {
                p.Position = _origins[i];
                p.Velocity = Vector2.Zero;
                _flow[i] -= _flow[i] * flowBlend;
                continue;
            }

            Vector2 velocity = (p.Position - _previous[i]) / dt;

            // Being squeezed out from between the others can be very sudden. Around a body more so, everything it
            // ploughs through is pressed into its neighbours at once, which would spray the lot into the air.
            // What is near a body comes away no faster than a body could have sent it, unless it already was.
            float limit = _nearBodies[i] ? MathF.Min(maxSpeed, MathF.Max(BodyPushLimit, p.Velocity.Length())) : maxSpeed;
            float speedSquared = velocity.LengthSquared();
            if (speedSquared > limit * limit)
            {
                velocity *= limit / MathF.Sqrt(speedSquared);
            }

            // The shove of a body is a speed to be brought up to, not one to add to every step it is leant on
            Vector2 shove = _shoves[i];
            float shoveSpeed = shove.Length();
            if (shoveSpeed > 0.0f)
            {
                Vector2 direction = shove / shoveSpeed;
                float along = Vector2.Dot(velocity, direction);
                if (along < shoveSpeed) velocity += direction * (shoveSpeed - along);
            }

            // Whatever is going along the map is slowed by friction
            if (contact != Vector2.Zero)
            {
                Vector2 tangent = velocity - contact * Vector2.Dot(velocity, contact);
                velocity -= tangent * (1.0f - slide);
            }

            p.Velocity = velocity;
            _flow[i] += (velocity - _flow[i]) * flowBlend;
        }
    }

    /// <summary>
    /// Helper method to age every particle and take out the ones that died or fell off the map.
    /// </summary>
    /// <returns>How many particles are left.</returns>
    private int RemoveDead(PhysicsStaticGrid grid, float dt)
    {
        var particles = _particles;
        int live = Count;

        GetLostBounds(grid, out Vector2 lostMin, out Vector2 lostMax);
        float decay = MaxAge > 0.0f ? dt / MaxAge : 0.0f;

        // How old a particle is isn't kept, but it follows from how much of its life it has used up and how fast it
        // does so. As a share of its life, one ageing at rate r has reached the limit once it is down to 1 - r * limit
        float limit = MaxLife > 0.0f && MaxAge > 0.0f ? MaxLife / MaxAge : float.MaxValue;

        for (int i = 0; i < live;)
        {
            ref ParticleState2D p = ref particles[i];
            p.Life -= decay * p.Rate;

            bool lost = p.Position.X < lostMin.X || p.Position.X > lostMax.X ||
                        p.Position.Y < lostMin.Y || p.Position.Y > lostMax.Y;

            if (p.Life <= 0.0f || lost || 1.0f - p.Life >= p.Rate * limit)
            {
                // Swap and remove, the last live particle (not yet aged this step) takes this slot,
                // then we re-process index i without advancing.
                p = particles[--live];
                _flow[i] = _flow[live];
                continue;
            }

            i++;
        }

        return live;
    }

    private void EnsureScratch(int capacity)
    {
        if (_previous.Length == capacity) return;

        _previous = new Vector2[capacity];
        _contacts = new Vector2[capacity];
        _origins = new Vector2[capacity];
        _shoves = new Vector2[capacity];
        _nearMap = new bool[capacity];
        _nearBodies = new bool[capacity];
        _cellParticles = new int[capacity];
        _cellOf = new int[capacity];

        // Twice as many slots as there can be particles keeps most cells in a slot of their own
        int slots = 16;
        while (slots < capacity * 2) slots <<= 1;

        _cellStart = new int[slots + 1];
        _cellMask = slots - 1;
    }

    private int GetCell(int x, int y) => ((x * 73856093) ^ (y * 19349663)) & _cellMask;

    /// <summary>
    /// Helper method to sort the particles by the cell they are in, a counting sort so nothing is allocated.
    /// </summary>
    private void SortIntoCells(int live, float cellSize)
    {
        var particles = _particles;
        var cellStart = _cellStart;
        float scale = 1.0f / cellSize;

        // First count how many particles are in every cell
        Array.Clear(cellStart);
        for (int i = 0; i < live; i++)
        {
            Vector2 position = particles[i].Position * scale;
            int cell = GetCell((int)MathF.Floor(position.X), (int)MathF.Floor(position.Y));

            _cellOf[i] = cell;
            cellStart[cell]++;
        }

        // Which makes where the particles of every cell end...
        int total = 0;
        for (int i = 0; i < cellStart.Length; i++)
        {
            total += cellStart[i];
            cellStart[i] = total;
        }

        // ...and handing them out backwards from there leaves every entry at where its cell starts
        for (int i = 0; i < live; i++)
        {
            _cellParticles[--cellStart[_cellOf[i]]] = i;
        }
    }

    /// <summary>
    /// Helper method to list every two particles that are within range of each other, each pair once.
    /// </summary>
    private void FindPairs(int live, float range)
    {
        var particles = _particles;
        float rangeSquared = range * range;
        float scale = 1.0f / range;
        int count = 0;

        Span<int> cells = stackalloc int[9];

        for (int i = 0; i < live; i++)
        {
            Vector2 position = particles[i].Position;
            int cellX = (int)MathF.Floor(position.X * scale);
            int cellY = (int)MathF.Floor(position.Y * scale);

            int cellCount = 0;
            for (int y = cellY - 1; y <= cellY + 1; y++)
            {
                for (int x = cellX - 1; x <= cellX + 1; x++)
                {
                    int cell = GetCell(x, y);

                    // Two of the cells around us sharing a slot would have us find everyone in it twice
                    if (cells[..cellCount].Contains(cell)) continue;
                    cells[cellCount++] = cell;

                    int end = _cellStart[cell + 1];
                    for (int entry = _cellStart[cell]; entry < end; entry++)
                    {
                        // Every pair is only listed from the side of the one that comes first
                        int j = _cellParticles[entry];
                        if (j <= i) continue;

                        ref ParticleState2D other = ref particles[j];
                        float awayX = other.Position.X - position.X, awayY = other.Position.Y - position.Y;
                        if (awayX * awayX + awayY * awayY >= rangeSquared) continue;

                        if (count + 2 > _pairs.Length)
                        {
                            Array.Resize(ref _pairs, Math.Max(_pairs.Length * 2, 1024));
                            Array.Resize(ref _pairGaps, _pairs.Length / 2);
                            Array.Resize(ref _pairNormals, _pairs.Length);
                        }

                        _pairs[count++] = i;
                        _pairs[count++] = j;
                    }
                }
            }
        }

        _pairCount = count;
    }

    /// <summary>
    /// Helper method to give two particles in the very same spot (let go by the same emitter) a way to go apart,
    /// whichever, as long as it is the same one every time.
    /// </summary>
    private static void GetAnyDirection(int a, int b, out float x, out float y)
    {
        (y, x) = MathF.SinCos(a * 12.9898f + b * 78.233f);
    }

    /// <summary>
    /// Helper method to undo some of the overlaps there were before this step moved anything, without anyone gaining speed
    /// from it, and to work out for every pair how far apart the two are to be kept for the rest of the step, their full
    /// size if they were clear of each other, or only as far as they got here. That way pushing them apart further is
    /// never left to the passes, where it would be taken for speed. Which way each pair is to be pushed apart is settled
    /// here too, the way they were apart before moving, as after it one may sit right on top of the other.
    /// </summary>
    /// <param name="spread">How far the ones that are merely close nudge each other away, see <see cref="Spread"/>.</param>
    private void Settle(int live, float diameter, float range, float spread, PhysicsStaticGrid grid)
    {
        var particles = _particles;
        var previous = _previous;
        var pairs = _pairs;
        var gaps = _pairGaps;
        var normals = _pairNormals;
        float most = diameter * SETTLE_RATE;
        float inverseRange = 1.0f / range;

        // The maths of the loops over the pairs is written out per axis. They are by far the hottest ones there are,
        // and in a build without optimisations every operator of a vector is a call
        for (int pair = 0, count = _pairCount; pair < count; pair += 2)
        {
            int a = pairs[pair], b = pairs[pair + 1];
            ref Vector2 first = ref previous[a];
            ref Vector2 second = ref previous[b];

            float x = second.X - first.X, y = second.Y - first.Y;
            float distance = MathF.Sqrt(x * x + y * y);

            // In a slope there are more neighbours on the uphill side, so the nudges add up to one going downhill.
            // Being pressed into each other (at the bottom of a deep pile) doesn't make the nudge any harder.
            float closeness = MathF.Max(1.0f - MathF.Max(distance, diameter) * inverseRange, 0.0f);
            float amount = MathF.Min(MathF.Max(diameter - distance, 0.0f) * 0.5f, most) + spread * closeness * closeness;

            if (distance > 0.0001f)
            {
                x /= distance;
                y /= distance;
            }
            else
            {
                GetAnyDirection(a, b, out x, out y);
            }

            // However far they are apart after this is as far as the passes have to keep them
            gaps[pair >> 1] = MathF.Min(distance + amount * 2.0f, diameter);
            normals[pair] = x;
            normals[pair + 1] = y;
            if (amount <= 0.0f) continue;

            x *= amount;
            y *= amount;

            first.X -= x; first.Y -= y;
            second.X += x; second.Y += y;

            ref ParticleState2D firstParticle = ref particles[a];
            ref ParticleState2D secondParticle = ref particles[b];
            firstParticle.Position.X -= x; firstParticle.Position.Y -= y;
            secondParticle.Position.X += x; secondParticle.Position.Y += y;
        }

        // Which must not end with anyone inside of the map
        for (int i = 0; i < live; i++)
        {
            if (!_nearMap[i]) continue;

            Vector2 before = previous[i], contact = default;
            SeparateFromMap(ref previous[i], ref contact, grid);

            // Getting out of one tile can end inside of the one next to it (in a corner). The map always wins,
            // rather than that, the particle isn't put anywhere at all this time
            if (contact != Vector2.Zero && grid.Contains(previous[i]) && !grid.Contains(_origins[i]))
            {
                previous[i] = _origins[i];
            }

            particles[i].Position += previous[i] - before;
        }
    }

    /// <summary>
    /// Helper method to push every two particles that are closer than they are to be kept apart, each going half of the way.
    /// </summary>
    private void SeparatePairs(float diameter)
    {
        var particles = _particles;
        var pairs = _pairs;
        var gaps = _pairGaps;
        var normals = _pairNormals;
        float diameterSquared = diameter * diameter;

        for (int pair = 0, count = _pairCount; pair < count; pair += 2)
        {
            ref ParticleState2D first = ref particles[pairs[pair]];
            ref ParticleState2D second = ref particles[pairs[pair + 1]];

            float x = second.Position.X - first.Position.X, y = second.Position.Y - first.Position.Y;
            if (x * x + y * y >= diameterSquared) continue;

            // How far apart they are is measured along the way they were apart to begin with, one that has ended up
            // on top of (or just past) the other is then still put back on the side it came from
            float normalX = normals[pair], normalY = normals[pair + 1];
            float amount = (gaps[pair >> 1] - (x * normalX + y * normalY)) * 0.5f;
            if (amount <= 0.0f) continue;

            x = normalX * amount;
            y = normalY * amount;
            first.Position.X -= x; first.Position.Y -= y;
            second.Position.X += x; second.Position.Y += y;
        }
    }

    /// <summary>
    /// Helper method to get a particle out of the bodies it is inside of. Where it was is moved along with it, being put
    /// out of the way of a body (which can cover a lot of ground in a step) is not to be taken for speed.
    /// </summary>
    /// <param name="shove">Set to the speed the hardest pushing body sends the particle off at.</param>
    private void SeparateFromBodies(ref Vector2 position, ref Vector2 previous, ref Vector2 shove, ReadOnlySpan<PhysicsShape> bodies)
    {
        for (int i = 0; i < bodies.Length; i++)
        {
            ref readonly PhysicsShape shape = ref bodies[i];
            if (!shape.Overlaps(position, Radius, out Vector2 normal, out float depth)) continue;

            // A body that covers more ground in a step than that goes through the particles more than it moves them,
            // the ones it leaves behind are let out over the next steps
            depth = MathF.Min(depth, BODY_STEP);
            position += normal * depth;
            previous += normal * depth;

            // Only a body coming at the particle shoves it, one that is leaving doesn't drag it along
            float push = Vector2.Dot(GetBodyPush(shape), normal);
            if (push * push > shove.LengthSquared() && push > 0.0f)
            {
                shove = normal * push;
            }
        }
    }

    /// <summary>
    /// Helper method to tell if anything of the map is within reach of a position, going by the boxes around its shapes.
    /// </summary>
    private static bool IsNearMap(Vector2 position, float reach, PhysicsStaticGrid grid)
    {
        Vector2 min = position - new Vector2(reach), max = position + new Vector2(reach);
        if (!grid.TryGetCells(min, max, out int x0, out int y0, out int x1, out int y1)) return false;

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                foreach (int index in grid.GetShapes(x, y))
                {
                    ref readonly PhysicsShape shape = ref grid[index];
                    if (max.X >= shape.Min.X && min.X <= shape.Max.X && max.Y >= shape.Min.Y && min.Y <= shape.Max.Y) return true;
                }
            }
        }

        return false;
    }

    /// <param name="contact">Set to the way the map pushed, if it did.</param>
    private void SeparateFromMap(ref Vector2 position, ref Vector2 contact, PhysicsStaticGrid grid)
    {
        Vector2 reach = new(Radius);
        if (!grid.TryGetCells(position - reach, position + reach, out int x0, out int y0, out int x1, out int y1)) return;

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                foreach (int index in grid.GetShapes(x, y))
                {
                    ref readonly PhysicsShape shape = ref grid[index];
                    if (!shape.Overlaps(position, Radius, out Vector2 normal, out float depth)) continue;

                    position += normal * depth;
                    contact = normal;
                }
            }
        }
    }
}
