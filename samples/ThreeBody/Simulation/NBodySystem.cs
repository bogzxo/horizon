using System.Numerics;

namespace ThreeBody.Simulation;

/// <summary>Time integration scheme. Both are symplectic, Yoshida is 4th order (3 force evaluations per step).</summary>
public enum Integrator { VelocityVerlet, Yoshida4 }

/// <summary>What happens when two bodies touch.</summary>
public enum CollisionMode { None, Merge, Bounce }

/// <summary>A contact: where, how big the bodies were (contact radius sum), and whether they merged or bounced.</summary>
public readonly record struct CollisionEvent(double X, double Y, double Size, bool Merged);

/// <summary>Gravitational N-body system (G = 1) integrated in fixed sub-steps, with optional collisions.</summary>
public sealed class NBodySystem
{
    private const double Softening = 1e-3;
    private const double BaseStep = 0.0005;
    private const int MaxStepsPerUpdate = 6000;   // per Update call, a frame never stalls on more than this

    /// <summary>Steps taken by the last Update.</summary>
    public int LastSteps { get; private set; }
    /// <summary>Mean step size of the last Update (sim time units).</summary>
    public double LastStepDt { get; private set; }
    /// <summary>True when the last Update hit the step cap and dropped sim time, so the real speed is below TimeScale.</summary>
    public bool SpeedLimited { get; private set; }

    /// <summary>Contacts since the last drain, thread safe. The renderer drains it to draw flashes.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<CollisionEvent> CollisionEvents { get; } = new();

    // Yoshida 4th order triple-jump coefficients
    private static readonly double YoshidaW1 = 1.0 / (2.0 - Math.Pow(2.0, 1.0 / 3.0));
    private static readonly double YoshidaW0 = -Math.Pow(2.0, 1.0 / 3.0) / (2.0 - Math.Pow(2.0, 1.0 / 3.0));

    private readonly List<Body> _bodies = new();
    private double[] _ax = [], _ay = [];
    private double _carry;       // sim time not yet stepped
    private int _seed = 1;

    public NBodySystem(Preset preset = Preset.FigureEight)
    {
        Reset(preset);
    }

    public IReadOnlyList<Body> Bodies => _bodies;
    public Preset CurrentPreset { get; private set; }
    public double Time { get; private set; }
    public double Energy { get; private set; }
    public double InitialEnergy { get; private set; }
    public double EnergyDrift => InitialEnergy == 0 ? 0 : (Energy - InitialEnergy) / Math.Abs(InitialEnergy);
    public bool Paused { get; set; }
    public int ResetCount { get; private set; }

    public Integrator Integrator { get; set; } = Integrator.VelocityVerlet;
    public CollisionMode Collisions { get; set; } = CollisionMode.None;
    /// <summary>Merges so far since the last reset.</summary>
    public int MergeCount { get; private set; }
    /// <summary>How many times the energy baseline was re-taken (body added, removed or merged) since the last reset.</summary>
    public int Rebaselines { get; private set; }

    private double _timeScale = 1.0;
    public double TimeScale
    {
        get => _timeScale;
        set => _timeScale = Math.Clamp(value, 0.05, 256.0);
    }

    public void Reset() => Reset(CurrentPreset);

    public void Reset(Preset preset)
    {
        CurrentPreset = preset;
        _seed = preset == Preset.Chaotic ? Environment.TickCount : 1;
        _bodies.Clear();
        _bodies.AddRange(Presets.Create(preset, new Random(_seed)));
        Time = 0;
        _carry = 0;
        MergeCount = 0;
        Rebaselines = 0;
        ResetCount++;
        CenterOfMassFrame();
        Rebaseline();
    }

    /// <summary>Replace the bodies and settings with a saved scenario. Positions are taken as stored, not recentred.</summary>
    public void LoadScenario(ScenarioData data)
    {
        _bodies.Clear();
        foreach (var d in data.Bodies)
        {
            var c = d.Color;
            _bodies.Add(new Body
            {
                Name = d.Name, Mass = d.Mass, X = d.X, Y = d.Y, VX = d.VX, VY = d.VY, Radius = d.Radius,
                Color = c is { Length: >= 4 } ? new Vector4(c[0], c[1], c[2], c[3]) : Vector4.One,
            });
        }
        // Unknown names in a hand edited file keep the current setting
        if (Enum.TryParse<Integrator>(data.Integrator, true, out var integrator) && Enum.IsDefined(integrator)) Integrator = integrator;
        if (Enum.TryParse<CollisionMode>(data.Collisions, true, out var collisions) && Enum.IsDefined(collisions)) Collisions = collisions;
        TimeScale = data.TimeScale;
        Time = 0;
        _carry = 0;
        MergeCount = 0;
        Rebaselines = 0;
        ResetCount++;
        Rebaseline();
    }

    /// <summary>Shift to the centre of mass frame: centre of mass at the origin, total momentum zero.</summary>
    public void CenterOfMassFrame()
    {
        double mt = 0, cx = 0, cy = 0, px = 0, py = 0;
        foreach (var b in _bodies) { mt += b.Mass; cx += b.Mass * b.X; cy += b.Mass * b.Y; px += b.Mass * b.VX; py += b.Mass * b.VY; }
        if (mt <= 0) return;
        cx /= mt; cy /= mt; px /= mt; py /= mt;
        foreach (var b in _bodies) { b.X -= cx; b.Y -= cy; b.VX -= px; b.VY -= py; }
        Rebaseline();
    }

    /// <summary>Negate every velocity: a time reversible integrator then retraces the path back to the start.</summary>
    public void ReverseTime()
    {
        foreach (var b in _bodies) { b.VX = -b.VX; b.VY = -b.VY; }
    }

    /// <summary>Take the current energy as the reference, after anything that legitimately changes it.</summary>
    private void Rebaseline()
    {
        Resize();
        ComputeAccelerations();
        Energy = ComputeEnergy();
        InitialEnergy = Energy;
    }

    private void Resize()
    {
        if (_ax.Length != _bodies.Count) { _ax = new double[_bodies.Count]; _ay = new double[_bodies.Count]; }
    }

    /// <summary>Physical contact radius, same size the body is drawn at.</summary>
    public static double ContactRadius(Body b) => Math.Max(0.03, 0.05 * Math.Cbrt(b.Mass));

    public void AddBody(double x, double y, double vx, double vy, double mass = 1.0)
    {
        Vector4[] cols = [new(1.0f, 0.35f, 0.3f, 1f), new(0.4f, 1.0f, 0.5f, 1f), new(0.4f, 0.6f, 1.0f, 1f), new(1.0f, 0.9f, 0.4f, 1f), new(0.85f, 0.5f, 1.0f, 1f), new(0.4f, 0.95f, 0.95f, 1f)];
        _bodies.Add(new Body
        {
            Name = ((char)('A' + (_bodies.Count % 26))).ToString(),
            Mass = mass, X = x, Y = y, VX = vx, VY = vy,
            Color = cols[_bodies.Count % cols.Length], Radius = 0.04,
        });
        Structure();
    }

    public void RemoveAt(int index)
    {
        if (index < 0 || index >= _bodies.Count) return;
        _bodies.RemoveAt(index);
        Structure();
    }

    /// <summary>Index of the body whose disc (padded by padding world units) holds the point, nearest first, or -1.</summary>
    public int FindBody(double x, double y, double padding)
    {
        int best = -1; double bestD = double.MaxValue;
        for (int i = 0; i < _bodies.Count; i++)
        {
            double dx = _bodies[i].X - x, dy = _bodies[i].Y - y;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d <= ContactRadius(_bodies[i]) + padding && d < bestD) { best = i; bestD = d; }
        }
        return best;
    }

    // Count or masses changed: trails are indexed by body so the renderer clears on ResetCount, energy gets a new baseline
    private void Structure()
    {
        ResetCount++;
        Rebaselines++;
        Rebaseline();
    }

    /// <summary>One fixed tick while paused (or any time).</summary>
    public void StepOnce()
    {
        Advance(BaseStep * 200);
        Energy = ComputeEnergy();
    }

    /// <summary>Advance by real seconds. One sim time unit per real second times TimeScale.</summary>
    public void Update(double dtSeconds)
    {
        if (Paused || dtSeconds <= 0) return;
        // Sub-stepping: the step never exceeds BaseStep (the accurate size) whatever the speed, so a higher
        // TimeScale means more steps per tick, not bigger ones. Close encounters shrink it further.
        _carry += Math.Min(dtSeconds, 0.1) * _timeScale;
        double start = _carry;
        int steps = 0;
        while (_carry > 1e-9 && steps < MaxStepsPerUpdate)
        {
            double dt = Math.Min(Math.Min(BaseStep * Math.Clamp(Math.Pow(MinSeparation() / 0.3, 1.5), 0.0005, 1.0), _carry), BaseStep);
            Step(dt);
            _carry -= dt;
            steps++;
        }
        LastSteps = steps;
        LastStepDt = steps > 0 ? (start - _carry) / steps : 0;
        SpeedLimited = _carry > 1e-9;
        if (SpeedLimited) _carry = 0;   // dropped, never stepped coarser
        Energy = ComputeEnergy();
    }

    // Used for single stepping: many small sub-steps covering the interval
    private void Advance(double interval)
    {
        double left = interval;
        int steps = 0;
        while (left > 1e-9 && steps++ < MaxStepsPerUpdate)
        {
            double dt = Math.Min(Math.Min(BaseStep * Math.Clamp(Math.Pow(MinSeparation() / 0.3, 1.5), 0.0005, 1.0), left), BaseStep);
            Step(dt);
            left -= dt;
        }
    }

    public (double minX, double minY, double maxX, double maxY) Bounds()
    {
        if (_bodies.Count == 0) return (0, 0, 0, 0);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var b in _bodies)
        {
            minX = Math.Min(minX, b.X); maxX = Math.Max(maxX, b.X);
            minY = Math.Min(minY, b.Y); maxY = Math.Max(maxY, b.Y);
        }
        return (minX, minY, maxX, maxY);
    }

    private double MinSeparation()
    {
        double m = double.MaxValue;
        for (int i = 0; i < _bodies.Count; i++)
            for (int j = i + 1; j < _bodies.Count; j++)
            {
                double dx = _bodies[j].X - _bodies[i].X, dy = _bodies[j].Y - _bodies[i].Y;
                m = Math.Min(m, Math.Sqrt(dx * dx + dy * dy));
            }
        return m == double.MaxValue ? 1.0 : m;
    }

    private void Step(double dt)
    {
        Resize();
        if (Integrator == Integrator.Yoshida4)
        {
            VerletStep(dt * YoshidaW1);
            VerletStep(dt * YoshidaW0);
            VerletStep(dt * YoshidaW1);
        }
        else
        {
            VerletStep(dt);
        }
        Time += dt;
        if (Collisions != CollisionMode.None) ResolveCollisions();
    }

    // Velocity Verlet: half kick, drift, new forces, half kick. Leaves _ax/_ay current for the next step.
    private void VerletStep(double dt)
    {
        int n = _bodies.Count;
        for (int i = 0; i < n; i++)
        {
            var b = _bodies[i];
            b.VX += 0.5 * dt * _ax[i];
            b.VY += 0.5 * dt * _ay[i];
            b.X += dt * b.VX;
            b.Y += dt * b.VY;
        }
        ComputeAccelerations();
        for (int i = 0; i < n; i++)
        {
            var b = _bodies[i];
            b.VX += 0.5 * dt * _ax[i];
            b.VY += 0.5 * dt * _ay[i];
        }
    }

    private void ResolveCollisions()
    {
        bool merged = false;
        for (int i = 0; i < _bodies.Count; i++)
        {
            for (int j = i + 1; j < _bodies.Count; j++)
            {
                var a = _bodies[i]; var b = _bodies[j];
                double dx = b.X - a.X, dy = b.Y - a.Y;
                double d2 = dx * dx + dy * dy;
                double rr = ContactRadius(a) + ContactRadius(b);
                if (d2 >= rr * rr) continue;

                if (CollisionEvents.Count < 256)
                    CollisionEvents.Enqueue(new CollisionEvent((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5, rr, Collisions == CollisionMode.Merge));

                if (Collisions == CollisionMode.Merge)
                {
                    // Perfectly inelastic: mass, momentum and centre of mass conserved
                    double m = a.Mass + b.Mass;
                    var big = a.Mass >= b.Mass ? a : b;
                    a.X = (a.Mass * a.X + b.Mass * b.X) / m;
                    a.Y = (a.Mass * a.Y + b.Mass * b.Y) / m;
                    a.VX = (a.Mass * a.VX + b.Mass * b.VX) / m;
                    a.VY = (a.Mass * a.VY + b.Mass * b.VY) / m;
                    a.Color = big.Color;
                    a.Name = big.Name;
                    a.Mass = m;
                    _bodies.RemoveAt(j);
                    MergeCount++;
                    merged = true;
                    j--;
                }
                else
                {
                    // Elastic bounce along the contact normal, then push the pair apart
                    double d = Math.Sqrt(d2);
                    double nx = d > 1e-12 ? dx / d : 1.0, ny = d > 1e-12 ? dy / d : 0.0;
                    double vn = (b.VX - a.VX) * nx + (b.VY - a.VY) * ny;
                    if (vn < 0)
                    {
                        double k = 2.0 * vn / (a.Mass + b.Mass);
                        a.VX += k * b.Mass * nx; a.VY += k * b.Mass * ny;
                        b.VX -= k * a.Mass * nx; b.VY -= k * a.Mass * ny;
                    }
                    double push = rr - d, mt = a.Mass + b.Mass;
                    a.X -= nx * push * b.Mass / mt; a.Y -= ny * push * b.Mass / mt;
                    b.X += nx * push * a.Mass / mt; b.Y += ny * push * a.Mass / mt;
                }
            }
        }
        if (merged) Structure();
    }

    private void ComputeAccelerations()
    {
        int n = _bodies.Count;
        Resize();
        Array.Clear(_ax); Array.Clear(_ay);
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                double dx = _bodies[j].X - _bodies[i].X, dy = _bodies[j].Y - _bodies[i].Y;
                double d2 = dx * dx + dy * dy + Softening * Softening;
                double inv = 1.0 / (d2 * Math.Sqrt(d2));
                _ax[i] += _bodies[j].Mass * dx * inv; _ay[i] += _bodies[j].Mass * dy * inv;
                _ax[j] -= _bodies[i].Mass * dx * inv; _ay[j] -= _bodies[i].Mass * dy * inv;
            }
        }
    }

    private double ComputeEnergy()
    {
        double e = 0;
        int n = _bodies.Count;
        for (int i = 0; i < n; i++)
        {
            var b = _bodies[i];
            e += 0.5 * b.Mass * (b.VX * b.VX + b.VY * b.VY);
            for (int j = i + 1; j < n; j++)
            {
                double dx = _bodies[j].X - b.X, dy = _bodies[j].Y - b.Y;
                e -= b.Mass * _bodies[j].Mass / Math.Sqrt(dx * dx + dy * dy + Softening * Softening);
            }
        }
        return e;
    }
}
