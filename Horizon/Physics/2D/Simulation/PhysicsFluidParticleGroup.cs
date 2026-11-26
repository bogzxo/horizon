using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

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
    private const float SQUEEZE_RATE = 80.0f;   // Maximum newly acquired squeeze speed (in diameters a second, matching SPREAD_RATE) so sub-120Hz steps stay consistent
    private const float FLOW_TIME = 0.08f;      // How long (in seconds) the speed of a particle is averaged over for Flow
    private const int MAX_DENSE_CELLS = 65536;  // Flat 2D dense grid cap; falls back to spatial hash when particles span more cells

    private struct FluidPair
    {
        public int A, B;
        public float Gap, Nx, Ny;
    }

    // Compact SoA position buffers kept in persistent _particles[0..live) order for L1-resident pair passes
    private Vector2[] _pos = [];
    private Vector2[] _prev = [];

    // Where every particle was before anything at all was done to it this step, to go back to if all else fails
    private Vector2[] _origins = [];

    // The way the map pushed a particle this step (zero if it didn't), sliding along it is slowed by friction
    private Vector2[] _contacts = [];

    // The shove a body gave a particle this step (zero if none did), the speed it is sent off at, see BodyPush
    private Vector2[] _shoves = [];

    // Whether a particle is close enough to the map or to a body to run into it this step
    private bool[] _nearMap = [], _nearBodies = [];

    // How fast every particle has been going lately (see Flow), which the first _flowCount of them have been
    // around for long enough to have (in original _particles order)
    private Vector2[] _flow = [];
    private int _flowCount;

    // Spatial grid buffers: bounded flat 2D dense grid when span <= MAX_DENSE_CELLS, spatial hash fallback otherwise.
    private int[] _denseCellStart = [];
    private int[] _cellStart = [0];
    private int[] _cellParticles = [];
    private int[] _cellOf = [];
    private int _cellMask;
    private int _denseMinCellX, _denseMinCellY, _denseGridW, _denseGridH;
    private bool _usedDenseGrid;

    // Single-stream interleaved pair list (indices in [0..live) matching Pass 5 pair order)
    private FluidPair[] _pairs = [];
    private int _pairCount;

    internal bool LastStepUsedDenseGrid => _usedDenseGrid;
    internal int LastPairCount => _pairCount;

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

    [SkipLocalsInit]
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
        float safeDistanceSq = safeDistance * safeDistance;
        float maxSpeed = safeDistance / dt;

        // Grown by how far a particle can reach in a step, so testing one against the box is just testing its centre
        bodiesMin -= new Vector2(Radius + safeDistance);
        bodiesMax += new Vector2(Radius + safeDistance);
        bool hasBodies = bodies.Length > 0;

        Vector2 gravityStep = Gravity * dt;
        float airDrag = MathF.Max(0.0f, 1.0f - LinearDrag * dt);
        float slide = MathF.Max(0.0f, 1.0f - Friction * dt);
        float range = diameter * (Spread > 0.0f ? SPREAD_RANGE : CONTACT_RANGE);
        int iterations = Math.Max(Iterations, 1);

        // How far a particle can end up from where it is headed, put there by its neighbours or by a body
        // (which gets to move it once every other pass)
        float mapReach = Radius * 3.0f + safeDistance + (hasBodies ? BODY_STEP * ((iterations + 1) / 2) : 0.0f);

        // The nudge is measured against the size of the particles, so piles of big and of small ones flatten out alike
        float spread = Spread * SPREAD_RATE * diameter * dt;

        // 1. Move everyone to where they would end up if nothing was in their way, and populate compact SoA arrays
        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
        float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;

        ref ParticleState2D particleBase = ref MemoryMarshal.GetArrayDataReference(particles);
        ref Vector2 posBase = ref MemoryMarshal.GetArrayDataReference(_pos);
        ref Vector2 prevBase = ref MemoryMarshal.GetArrayDataReference(_prev);
        ref Vector2 originsBase = ref MemoryMarshal.GetArrayDataReference(_origins);

        for (int i = 0; i < live; i++)
        {
            ref ParticleState2D p = ref Unsafe.Add(ref particleBase, i);
            Vector2 velocity = (p.Velocity + gravityStep) * airDrag;
            p.Velocity = velocity;

            Vector2 move = velocity * dt;
            float distanceSquared = move.LengthSquared();
            if (distanceSquared > safeDistanceSq)
            {
                move *= safeDistance / MathF.Sqrt(distanceSquared);
            }

            Vector2 origin = p.Position;
            Unsafe.Add(ref prevBase, i) = origin;
            Unsafe.Add(ref originsBase, i) = origin;
            Vector2 predicted = origin + move;
            p.Position = predicted;
            Unsafe.Add(ref posBase, i) = predicted;

            if (predicted.X < minX) minX = predicted.X;
            if (predicted.X > maxX) maxX = predicted.X;
            if (predicted.Y < minY) minY = predicted.Y;
            if (predicted.Y > maxY) maxY = predicted.Y;
        }

        // 2. Sort particles into cells and build the pair list once
        SortIntoCells(live, range, minX, maxX, minY, maxY, mapReach, bodiesMin, bodiesMax, hasBodies, grid);
        if (_usedDenseGrid)
            FindPairsDense(live, _denseMinCellX, _denseMinCellY, _denseGridW, _denseGridH, range);
        else
            FindPairsHash(live, range);

        // 3. Whatever was overlapping before anyone moved (let go on top of each other, or squeezed further than
        // the passes could undo) is put apart a little at a time, and what is merely close is nudged apart (the spread).
        // Where it was is moved along with it, so that none of this is taken for speed. It is what keeps a deep pool
        // from boiling, a crowded spot from firing particles off and what is poured from spraying all over.
        Settle(live, diameter, range, spread, mapReach, grid);

        // 4. Then push them out of each other, the map and the bodies. Getting out of one thing can mean ending up in
        // the next, so it is done over and over, every pass gets the whole lot a bit closer to where nothing overlaps.
        ref Vector2 contactsBase = ref MemoryMarshal.GetArrayDataReference(_contacts);
        ref Vector2 shovesBase = ref MemoryMarshal.GetArrayDataReference(_shoves);
        ref bool nearMapBase = ref MemoryMarshal.GetArrayDataReference(_nearMap);
        ref bool nearBodiesBase = ref MemoryMarshal.GetArrayDataReference(_nearBodies);

        float safeReach = MathF.Max(0f, mapReach - Radius);
        float safeReachSq = safeReach * safeReach;
        for (int pass = 0; pass < iterations; pass++)
        {
            SeparatePairs(diameter);

            // The few that are near something solid only get out of it every other pass, which is plenty
            // as long as the last pass is one of them, nothing may be left inside of the map
            if (((iterations - 1 - pass) & 1) != 0) continue;

            bool finalPass = pass == iterations - 1;
            for (int i = 0; i < live; i++)
            {
                if (hasBodies && Unsafe.Add(ref nearBodiesBase, i))
                {
                    SeparateFromBodies(
                        ref Unsafe.Add(ref posBase, i),
                        ref Unsafe.Add(ref prevBase, i),
                        ref Unsafe.Add(ref shovesBase, i),
                        bodies);
                }

                ref bool nearMap = ref Unsafe.Add(ref nearMapBase, i);
                if (!nearMap && finalPass &&
                    (grid.IsNearAnyShape(Unsafe.Add(ref posBase, i), Radius) ||
                     Vector2.DistanceSquared(Unsafe.Add(ref posBase, i), Unsafe.Add(ref originsBase, i)) > safeReachSq))
                {
                    nearMap = true;
                }

                // The map goes last as it is the one thing which never gives way
                if (nearMap)
                {
                    SeparateFromMap(
                        ref Unsafe.Add(ref posBase, i),
                        Unsafe.Add(ref originsBase, i),
                        ref Unsafe.Add(ref contactsBase, i),
                        grid);
                }
            }
        }

        // 5. How far everyone got in the end is how fast they are going
        float flowBlend = 1.0f - MathF.Exp(-dt / FLOW_TIME);
        float invDt = 1.0f / dt;
        float bodyLimit = MathF.Min(maxSpeed, BodyPushLimit);
        float squeezeLimit = MathF.Min(maxSpeed, diameter * SQUEEZE_RATE);
        float frictionFactor = 1.0f - slide;

        ref Vector2 flowBase = ref MemoryMarshal.GetArrayDataReference(_flow);

        for (int i = 0; i < live; i++)
        {
            ref ParticleState2D p = ref Unsafe.Add(ref particleBase, i);
            ref Vector2 flow = ref Unsafe.Add(ref flowBase, i);
            Vector2 position = Unsafe.Add(ref posBase, i);
            Vector2 origin = Unsafe.Add(ref originsBase, i);
            Vector2 contact = Unsafe.Add(ref contactsBase, i);

            // The weight of a pile (or a body) can press a particle into or across a thin wall harder than the map
            // pushes back. The map always wins, rather than end up inside of it or on the far side of a wall the
            // particle stays where it was. Unless it was inside already (spawned there), then it has to be let out.
            if ((Unsafe.Add(ref nearMapBase, i) || contact != Vector2.Zero) &&
                (grid.Contains(position) || grid.IntersectsSegment(origin, position)) &&
                !grid.Contains(origin))
            {
                p.Position = origin;
                p.Velocity = Vector2.Zero;
                flow -= flow * flowBlend;
                continue;
            }

            p.Position = position;
            Vector2 velocity = (position - Unsafe.Add(ref prevBase, i)) * invDt;

            // Being squeezed out from between the others can be very sudden. Around a body more so, everything it
            // ploughs through is pressed into its neighbours at once, which would spray the lot into the air.
            // What is near a body comes away no faster than a body could have sent it, unless it already was.
            float baseLimit = (hasBodies && Unsafe.Add(ref nearBodiesBase, i)) ? bodyLimit : squeezeLimit;
            float speedSquared = velocity.LengthSquared();
            if (speedSquared > baseLimit * baseLimit)
            {
                float limit = MathF.Min(maxSpeed, MathF.Max(baseLimit, p.Velocity.Length()));
                if (speedSquared > limit * limit)
                {
                    velocity *= limit / MathF.Sqrt(speedSquared);
                }
            }

            // The shove of a body is a speed to be brought up to, not one to add to every step it is leant on
            Vector2 shove = Unsafe.Add(ref shovesBase, i);
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
                velocity -= tangent * frictionFactor;
            }

            p.Velocity = velocity;
            flow += (velocity - flow) * flowBlend;
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
        if (_pos.Length == capacity) return;

        _pos = new Vector2[capacity];
        _prev = new Vector2[capacity];
        _origins = new Vector2[capacity];
        _contacts = new Vector2[capacity];
        _shoves = new Vector2[capacity];
        _nearMap = new bool[capacity];
        _nearBodies = new bool[capacity];
        _cellParticles = new int[capacity];
        _cellOf = new int[capacity];

        // Twice as many slots as there can be particles keeps most cells in a slot of their own (hash fallback)
        int slots = 16;
        while (slots < capacity * 2) slots <<= 1;

        _cellStart = new int[slots + 1];
        _cellMask = slots - 1;

        // Pre-size interleaved pair buffer up front for this capacity so FindPairs does not repeatedly resize
        int pairCapacity = capacity > 0 ? Math.Max(capacity * 8, 1024) : 0;
        if (pairCapacity > _pairs.Length)
        {
            _pairs = new FluidPair[pairCapacity];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetCell(int x, int y) => ((x * 73856093) ^ (y * 19349663)) & _cellMask;

    /// <summary>
    /// Sorts particle indices into a bounded flat 2D dense grid (or spatial hash fallback when unbounded) via counting sort,
    /// keeping <see cref="_pos"/> and <see cref="_prev"/> in persistent spawn order.
    /// </summary>
    [SkipLocalsInit]
    private void SortIntoCells(
        int live, float cellSize,
        float minX, float maxX, float minY, float maxY,
        float mapReach, Vector2 bodiesMin, Vector2 bodiesMax, bool hasBodies,
        PhysicsStaticGrid grid)
    {
        float scale = 1.0f / cellSize;

        ref Vector2 posBase = ref MemoryMarshal.GetArrayDataReference(_pos);
        ref int cellParticlesBase = ref MemoryMarshal.GetArrayDataReference(_cellParticles);
        ref int cellOfBase = ref MemoryMarshal.GetArrayDataReference(_cellOf);

        bool useDense = false;
        int minCellX = 0, minCellY = 0, gridW = 0, gridH = 0;

        if (float.IsFinite(minX) && float.IsFinite(maxX) && float.IsFinite(minY) && float.IsFinite(maxY))
        {
            minCellX = (int)MathF.Floor(minX * scale);
            int maxCellX = (int)MathF.Floor(maxX * scale);
            minCellY = (int)MathF.Floor(minY * scale);
            int maxCellY = (int)MathF.Floor(maxY * scale);

            long spanX = (long)maxCellX - minCellX + 1;
            long spanY = (long)maxCellY - minCellY + 1;
            if (spanX > 0 && spanY > 0 && spanX <= MAX_DENSE_CELLS && spanY <= MAX_DENSE_CELLS && spanX * spanY <= MAX_DENSE_CELLS)
            {
                useDense = true;
                gridW = (int)spanX;
                gridH = (int)spanY;
            }
        }

        _usedDenseGrid = useDense;

        if (useDense)
        {
            _denseMinCellX = minCellX;
            _denseMinCellY = minCellY;
            _denseGridW = gridW;
            _denseGridH = gridH;
            int cellCount = gridW * gridH;
            if (_denseCellStart.Length < cellCount + 1)
            {
                int nextLen = Math.Max(cellCount + 1, Math.Min(MAX_DENSE_CELLS + 1, Math.Max(_denseCellStart.Length * 2, 256)));
                _denseCellStart = new int[nextLen];
            }

            var cellStart = _denseCellStart;
            Array.Clear(cellStart, 0, cellCount + 1);
            ref int cellStartBase = ref MemoryMarshal.GetArrayDataReference(cellStart);

            for (int i = 0; i < live; i++)
            {
                Vector2 pos = Unsafe.Add(ref posBase, i);
                int cx = Math.Clamp((int)MathF.Floor(pos.X * scale) - minCellX, 0, gridW - 1);
                int cy = Math.Clamp((int)MathF.Floor(pos.Y * scale) - minCellY, 0, gridH - 1);
                int cell = cy * gridW + cx;

                Unsafe.Add(ref cellOfBase, i) = cell;
                Unsafe.Add(ref cellStartBase, cell)++;
            }

            int total = 0;
            for (int c = 0; c <= cellCount; c++)
            {
                ref int slot = ref Unsafe.Add(ref cellStartBase, c);
                total += slot;
                slot = total;
            }

            for (int i = 0; i < live; i++)
            {
                Unsafe.Add(ref cellParticlesBase, --Unsafe.Add(ref cellStartBase, Unsafe.Add(ref cellOfBase, i))) = i;
            }
        }
        else
        {
            var cellStart = _cellStart;
            Array.Clear(cellStart);
            ref int cellStartBase = ref MemoryMarshal.GetArrayDataReference(cellStart);

            for (int i = 0; i < live; i++)
            {
                Vector2 pos = Unsafe.Add(ref posBase, i);
                int cell = GetCell((int)MathF.Floor(pos.X * scale), (int)MathF.Floor(pos.Y * scale));

                Unsafe.Add(ref cellOfBase, i) = cell;
                Unsafe.Add(ref cellStartBase, cell)++;
            }

            int total = 0;
            for (int c = 0; c < cellStart.Length; c++)
            {
                ref int slot = ref Unsafe.Add(ref cellStartBase, c);
                total += slot;
                slot = total;
            }

            for (int i = 0; i < live; i++)
            {
                Unsafe.Add(ref cellParticlesBase, --Unsafe.Add(ref cellStartBase, Unsafe.Add(ref cellOfBase, i))) = i;
            }
        }

        // Clear per-step contact/shove buffers and classify static/body proximity once per step
        Array.Clear(_contacts, 0, live);
        Array.Clear(_shoves, 0, live);

        ref bool nearMapBase = ref MemoryMarshal.GetArrayDataReference(_nearMap);
        ref bool nearBodiesBase = ref MemoryMarshal.GetArrayDataReference(_nearBodies);

        if (hasBodies)
        {
            for (int i = 0; i < live; i++)
            {
                Vector2 pos = Unsafe.Add(ref posBase, i);
                Unsafe.Add(ref nearMapBase, i) = grid.IsNearAnyShape(pos, mapReach);
                Unsafe.Add(ref nearBodiesBase, i) =
                    pos.X >= bodiesMin.X && pos.X <= bodiesMax.X &&
                    pos.Y >= bodiesMin.Y && pos.Y <= bodiesMax.Y;
            }
        }
        else
        {
            for (int i = 0; i < live; i++)
            {
                Unsafe.Add(ref nearMapBase, i) = grid.IsNearAnyShape(Unsafe.Add(ref posBase, i), mapReach);
            }
        }
    }

    /// <summary>
    /// Dense flat 2D 3-row-slice pair finder: scans the contiguous horizontal slices across rows [cy-1..cy+1]
    /// and [cx-1..cx+1] in spawn-order <see cref="_pos"/> with zero hash collisions and zero 9-cell deduplication.
    /// </summary>
    [SkipLocalsInit]
    private void FindPairsDense(int live, int minCellX, int minCellY, int gridW, int gridH, float range)
    {
        float rangeSquared = range * range;
        float scale = 1.0f / range;
        int count = 0;
        var pairs = _pairs;
        int pairCapacity = pairs.Length;

        ref int cellStartBase = ref MemoryMarshal.GetArrayDataReference(_denseCellStart);
        ref int cellParticlesBase = ref MemoryMarshal.GetArrayDataReference(_cellParticles);
        ref Vector2 posBase = ref MemoryMarshal.GetArrayDataReference(_pos);
        ref FluidPair pairBase = ref MemoryMarshal.GetArrayDataReference(pairs);

        for (int i = 0; i < live; i++)
        {
            Vector2 posI = Unsafe.Add(ref posBase, i);
            int cx = Math.Clamp((int)MathF.Floor(posI.X * scale) - minCellX, 0, gridW - 1);
            int cy = Math.Clamp((int)MathF.Floor(posI.Y * scale) - minCellY, 0, gridH - 1);

            int ny0 = cy > 0 ? cy - 1 : 0;
            int ny1 = cy + 1 < gridH ? cy + 1 : gridH - 1;
            int nx0 = cx > 0 ? cx - 1 : 0;
            int nx1 = cx + 1 < gridW ? cx + 2 : gridW;

            for (int ry = ny0; ry <= ny1; ry++)
            {
                int row = ry * gridW;
                int start = Unsafe.Add(ref cellStartBase, row + nx0);
                int end = Unsafe.Add(ref cellStartBase, row + nx1);

                for (int k = start; k < end; k++)
                {
                    int j = Unsafe.Add(ref cellParticlesBase, k);
                    if (j <= i) continue;

                    Vector2 posJ = Unsafe.Add(ref posBase, j);
                    float dx = posJ.X - posI.X, dy = posJ.Y - posI.Y;
                    if (dx * dx + dy * dy >= rangeSquared) continue;

                    if (count == pairCapacity)
                    {
                        pairs = GrowPairs(ref pairCapacity);
                        pairBase = ref MemoryMarshal.GetArrayDataReference(pairs);
                    }

                    ref FluidPair dst = ref Unsafe.Add(ref pairBase, count++);
                    dst.A = i;
                    dst.B = j;
                }
            }
        }

        _pairCount = count;
    }

    /// <summary>
    /// Spatial-hash fallback pair finder when particles span more than <see cref="MAX_DENSE_CELLS"/> cells.
    /// </summary>
    [SkipLocalsInit]
    private void FindPairsHash(int live, float range)
    {
        float rangeSquared = range * range;
        float scale = 1.0f / range;
        int count = 0;
        var pairs = _pairs;
        int pairCapacity = pairs.Length;

        ref int cellStartBase = ref MemoryMarshal.GetArrayDataReference(_cellStart);
        ref int cellParticlesBase = ref MemoryMarshal.GetArrayDataReference(_cellParticles);
        ref Vector2 posBase = ref MemoryMarshal.GetArrayDataReference(_pos);
        ref FluidPair pairBase = ref MemoryMarshal.GetArrayDataReference(pairs);

        Span<int> cells = stackalloc int[9];

        for (int i = 0; i < live; i++)
        {
            Vector2 position = Unsafe.Add(ref posBase, i);
            int cellX = (int)MathF.Floor(position.X * scale);
            int cellY = (int)MathF.Floor(position.Y * scale);

            int cellCount = 0;
            for (int y = cellY - 1; y <= cellY + 1; y++)
            {
                for (int x = cellX - 1; x <= cellX + 1; x++)
                {
                    int cell = GetCell(x, y);

                    // Two of the cells around us sharing a slot would have us find everyone in it twice
                    bool seen = false;
                    for (int k = 0; k < cellCount; k++)
                    {
                        if (cells[k] == cell)
                        {
                            seen = true;
                            break;
                        }
                    }
                    if (seen) continue;
                    cells[cellCount++] = cell;

                    int end = Unsafe.Add(ref cellStartBase, cell + 1);
                    for (int entry = Unsafe.Add(ref cellStartBase, cell); entry < end; entry++)
                    {
                        // Every pair is only listed from the side of the one that comes first
                        int j = Unsafe.Add(ref cellParticlesBase, entry);
                        if (j <= i) continue;

                        Vector2 other = Unsafe.Add(ref posBase, j);
                        float awayX = other.X - position.X, awayY = other.Y - position.Y;
                        if (awayX * awayX + awayY * awayY >= rangeSquared) continue;

                        if (count == pairCapacity)
                        {
                            pairs = GrowPairs(ref pairCapacity);
                            pairBase = ref MemoryMarshal.GetArrayDataReference(pairs);
                        }

                        ref FluidPair dst = ref Unsafe.Add(ref pairBase, count++);
                        dst.A = i;
                        dst.B = j;
                    }
                }
            }
        }

        _pairCount = count;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private FluidPair[] GrowPairs(ref int capacity)
    {
        int newCapacity = Math.Max(_pairs.Length * 2, 1024);
        Array.Resize(ref _pairs, newCapacity);
        capacity = newCapacity;
        return _pairs;
    }

    /// <summary>
    /// Helper method to give two particles in the very same spot (let go by the same emitter) a way to go apart,
    /// whichever, as long as it is the same one every time.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
    [SkipLocalsInit]
    private void Settle(int live, float diameter, float range, float spread, float mapReach, PhysicsStaticGrid grid)
    {
        float most = diameter * SETTLE_RATE;
        float inverseRange = 1.0f / range;
        int pairCount = _pairCount;

        ref FluidPair pairBase = ref MemoryMarshal.GetArrayDataReference(_pairs);
        ref Vector2 prevBase = ref MemoryMarshal.GetArrayDataReference(_prev);

        // Mutate only _prev[] in the pair loop; the accumulated delta (_prev - _origins) is applied to _pos[] once below
        for (int i = 0; i < pairCount; i++)
        {
            ref FluidPair pair = ref Unsafe.Add(ref pairBase, i);
            int a = pair.A, b = pair.B;
            ref Vector2 first = ref Unsafe.Add(ref prevBase, a);
            ref Vector2 second = ref Unsafe.Add(ref prevBase, b);

            float x = second.X - first.X, y = second.Y - first.Y;
            float distance = MathF.Sqrt(x * x + y * y);

            // In a slope there are more neighbours on the uphill side, so the nudges add up to one going downhill.
            // Being pressed into each other (at the bottom of a deep pile) doesn't make the nudge any harder.
            float closeness = MathF.Max(1.0f - MathF.Max(distance, diameter) * inverseRange, 0.0f);
            float amount = MathF.Min(MathF.Max(diameter - distance, 0.0f) * 0.5f, most) + spread * closeness * closeness;

            if (distance > 0.0001f)
            {
                float invDist = 1.0f / distance;
                x *= invDist;
                y *= invDist;
            }
            else
            {
                GetAnyDirection(a, b, out x, out y);
            }

            // However far they are apart after this is as far as the passes have to keep them
            pair.Gap = MathF.Min(distance + amount * 2.0f, diameter);
            pair.Nx = x;
            pair.Ny = y;
            if (amount <= 0.0f) continue;

            x *= amount;
            y *= amount;

            first.X -= x; first.Y -= y;
            second.X += x; second.Y += y;
        }

        // Apply accumulated settle shift (_prev[i] - _origins[i]) to _pos[i] in one vectorized pass
        ApplySettleShift(_pos, _prev, _origins, live);

        // Which must not end with anyone inside of the map or pushed across a thin wall
        ref Vector2 posBase = ref MemoryMarshal.GetArrayDataReference(_pos);
        ref Vector2 originsBase = ref MemoryMarshal.GetArrayDataReference(_origins);
        ref bool nearMapBase = ref MemoryMarshal.GetArrayDataReference(_nearMap);

        for (int i = 0; i < live; i++)
        {
            ref bool nearMap = ref Unsafe.Add(ref nearMapBase, i);
            ref Vector2 prev = ref Unsafe.Add(ref prevBase, i);
            ref Vector2 pos = ref Unsafe.Add(ref posBase, i);

            if (!nearMap)
            {
                nearMap = grid.IsNearAnyShape(prev, mapReach) || grid.IsNearAnyShape(pos, mapReach);
                if (!nearMap) continue;
            }

            Vector2 origin = Unsafe.Add(ref originsBase, i);
            Vector2 before = prev, contact = default;
            SeparateFromMap(ref prev, origin, ref contact, grid);

            // Getting out of one tile can end inside of the one next to it (in a corner) or across a thin wall.
            // The map always wins, rather than that, the particle isn't put anywhere at all this time
            if ((grid.Contains(prev) || grid.IntersectsSegment(origin, prev)) && !grid.Contains(origin))
            {
                prev = origin;
            }

            pos += prev - before;
        }
    }

    /// <summary>
    /// Vectorized linear pass: pos[i] += prev[i] - origins[i] for i in [0..count).
    /// </summary>
    [SkipLocalsInit]
    private static void ApplySettleShift(Vector2[] pos, Vector2[] prev, Vector2[] origins, int count)
    {
        ref float posF = ref Unsafe.As<Vector2, float>(ref MemoryMarshal.GetArrayDataReference(pos));
        ref float prevF = ref Unsafe.As<Vector2, float>(ref MemoryMarshal.GetArrayDataReference(prev));
        ref float origF = ref Unsafe.As<Vector2, float>(ref MemoryMarshal.GetArrayDataReference(origins));

        int totalFloats = count * 2;
        int i = 0;

        if (Vector256.IsHardwareAccelerated && totalFloats >= Vector256<float>.Count)
        {
            int limit = totalFloats - Vector256<float>.Count;
            for (; i <= limit; i += Vector256<float>.Count)
            {
                Vector256<float> p = Vector256.LoadUnsafe(ref posF, (nuint)i);
                Vector256<float> pr = Vector256.LoadUnsafe(ref prevF, (nuint)i);
                Vector256<float> o = Vector256.LoadUnsafe(ref origF, (nuint)i);
                Vector256.StoreUnsafe(p + (pr - o), ref posF, (nuint)i);
            }
        }
        else if (Vector128.IsHardwareAccelerated && totalFloats >= Vector128<float>.Count)
        {
            int limit = totalFloats - Vector128<float>.Count;
            for (; i <= limit; i += Vector128<float>.Count)
            {
                Vector128<float> p = Vector128.LoadUnsafe(ref posF, (nuint)i);
                Vector128<float> pr = Vector128.LoadUnsafe(ref prevF, (nuint)i);
                Vector128<float> o = Vector128.LoadUnsafe(ref origF, (nuint)i);
                Vector128.StoreUnsafe(p + (pr - o), ref posF, (nuint)i);
            }
        }

        for (; i < totalFloats; i++)
        {
            Unsafe.Add(ref posF, i) += Unsafe.Add(ref prevF, i) - Unsafe.Add(ref origF, i);
        }
    }

    /// <summary>
    /// Helper method to push every two particles that are closer than they are to be kept apart, each going half of the way.
    /// </summary>
    [SkipLocalsInit]
    private void SeparatePairs(float diameter)
    {
        float diameterSquared = diameter * diameter;
        int pairCount = _pairCount;

        ref FluidPair pairBase = ref MemoryMarshal.GetArrayDataReference(_pairs);
        ref Vector2 posBase = ref MemoryMarshal.GetArrayDataReference(_pos);

        for (int i = 0; i < pairCount; i++)
        {
            ref readonly FluidPair pair = ref Unsafe.Add(ref pairBase, i);
            ref Vector2 first = ref Unsafe.Add(ref posBase, pair.A);
            ref Vector2 second = ref Unsafe.Add(ref posBase, pair.B);

            float x = second.X - first.X, y = second.Y - first.Y;
            if (x * x + y * y >= diameterSquared) continue;

            // How far apart they are is measured along the way they were apart to begin with, one that has ended up
            // on top of (or just past) the other is then still put back on the side it came from
            float normalX = pair.Nx, normalY = pair.Ny;
            float amount = (pair.Gap - (x * normalX + y * normalY)) * 0.5f;
            if (amount <= 0.0f) continue;

            x = normalX * amount;
            y = normalY * amount;
            first.X -= x; first.Y -= y;
            second.X += x; second.Y += y;
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

    /// <param name="contact">Set to the way the map pushed, if it did.</param>
    private void SeparateFromMap(ref Vector2 position, Vector2 reference, ref Vector2 contact, PhysicsStaticGrid grid)
    {
        // If neighbours pushed the particle past the midpoint (or all the way across) a thin wall, resolve it back
        // to the entry face along the segment from where it started rather than letting Overlaps eject it out the far side.
        if (position != reference)
        {
            for (int sweep = 0; sweep < 2; sweep++)
            {
                if (!grid.TryIntersectSegment(reference, position, out float hitT, out Vector2 hitNormal)) break;

                Vector2 hitPoint = reference + (position - reference) * hitT;
                float signedDistance = Vector2.Dot(position - hitPoint, hitNormal);
                if (signedDistance < Radius)
                {
                    position += hitNormal * (Radius - signedDistance);
                }

                contact = hitNormal;
            }
        }

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
