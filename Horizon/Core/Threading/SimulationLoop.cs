using System.Diagnostics;

namespace Horizon.Core.Threading;

/// <summary>
/// What a <see cref="SimulationLoop"/> runs. Every call is made on the loop's thread, one tick at a time.
/// </summary>
public interface ISimulationHost
{
    /// <summary>Called before anything of a tick is done. The place to wait for whoever else has to be kept out.</summary>
    void BeginTick();

    /// <summary>Moves the state of the game on, see <c>Entity.UpdateState</c>.</summary>
    void UpdateState(float dt);

    /// <summary>Steps the physics, always by the same length of time, see <c>Entity.UpdatePhysics</c>.</summary>
    void UpdatePhysics(float dt);

    /// <summary>
    /// Called once everything of a tick is done, the moment to publish what the game looks like now.
    /// </summary>
    /// <param name="tick">Which tick it was, counting from 1.</param>
    /// <param name="stamp">The moment (stopwatch ticks) the game is at now, which is when this tick was due.</param>
    /// <param name="time">How long (in seconds) the game has been simulated for.</param>
    void EndTick(long tick, long stamp, double time);
}

/// <summary>
/// The thread the game is simulated on, the logic and the physics of the game, one tick after another, on a clock of
/// their own, whatever the frames are doing.
/// <para>
/// The loop ticks as often as the faster of the two wants to go. Each of them keeps its own time. The physics steps by
/// the same length of time every time, as often as its rate says (what it comes to then doesn't depend on how busy the
/// machine was), the logic is told how long it has been since its last turn, which at the same rate as the ticks is
/// one tick every time. A tick is logic first, then physics, then whatever the host does at the end of it (publish a
/// snapshot), so what is read from the input in a tick has moved things by the end of the same tick.
/// </para>
/// <para>
/// Between two ticks the thread sleeps for as long as it safely can and only spins for the last stretch, see
/// <see cref="LoopTiming.WaitUntil"/>. A loop that falls behind makes up for up to a few ticks in a row, and lets the
/// rest go rather than racing to catch up with something it never will.
/// </para>
/// </summary>
public sealed class SimulationLoop : IDisposable
{
    // The most ticks made in a row to catch up after falling behind, what is left after that is let go
    private const int MAX_CATCH_UP = 4;

    // Leeway when deciding whether the logic or the physics is due, so the rounding of adding up ticks never skips a turn
    private const double DUE_SLACK = 1e-9;

    private readonly ISimulationHost host;
    private readonly Func<long> clock;
    private readonly Thread? thread;
    private volatile bool running;

    private readonly double tickPeriod, logicPeriod, physicsPeriod;
    private readonly double frequency = Stopwatch.Frequency;

    // When the next tick is due, in stopwatch ticks (kept as a double so the periods add up without drifting)
    private double next = double.NaN;
    private double logicOwed, physicsOwed;

    // When each of them last had a turn (stopwatch ticks), which is how often they really come round is known
    private long lastTickAt, lastLogicAt, lastPhysicsAt;

    /// <summary>How many ticks a second the loop makes, as many as the faster of the logic and the physics wants.</summary>
    public double TickRate { get; }

    /// <summary>How many times a second the logic is meant to take its turn.</summary>
    public double LogicRate { get; }

    /// <summary>How many times a second the physics is meant to step.</summary>
    public double PhysicsRate { get; }

    /// <summary>How many ticks there have been.</summary>
    public long Tick { get; private set; }

    /// <summary>How long (in seconds) the game has been simulated for. A tick at a time, so it stands still when the loop does.</summary>
    public double Time { get; private set; }

    /// <summary>
    /// Where whatever is to run on the simulation thread goes, at the start of the next tick. What an <c>await</c> on
    /// that thread carries on with, and whatever anybody else hands it (see <see cref="SimulationContext"/>).
    /// </summary>
    public SimulationContext Context { get; } = new();

    /// <summary>How the logic, the physics and the ticks as a whole have been doing lately.</summary>
    public LoopStatistics Logic { get; }

    /// <inheritdoc cref="Logic"/>
    public LoopStatistics Physics { get; }

    /// <inheritdoc cref="Logic"/>
    public LoopStatistics Ticks { get; }

    /// <param name="name">What the loop is called, which is also the name of its thread.</param>
    /// <param name="logicRate">How many turns a second the logic takes.</param>
    /// <param name="physicsRate">How many steps a second the physics takes.</param>
    public SimulationLoop(string name, double logicRate, double physicsRate, ISimulationHost host, ThreadPriority priority = ThreadPriority.AboveNormal)
        : this(logicRate, physicsRate, host, Stopwatch.GetTimestamp)
    {
        thread = new Thread(Run)
        {
            Name = name,
            Priority = priority,
            IsBackground = true
        };
    }

    /// <summary>
    /// A loop without a thread of its own, moved on by hand with <see cref="Step"/> against a clock of somebody's choosing. For tests.
    /// </summary>
    internal SimulationLoop(double logicRate, double physicsRate, ISimulationHost host, Func<long> clock)
    {
        this.host = host;
        this.clock = clock;

        LogicRate = Math.Max(1.0, logicRate);
        PhysicsRate = Math.Max(1.0, physicsRate);
        TickRate = Math.Max(LogicRate, PhysicsRate);

        tickPeriod = 1.0 / TickRate;
        logicPeriod = 1.0 / LogicRate;
        physicsPeriod = 1.0 / PhysicsRate;

        Logic = new LoopStatistics("Logic", LogicRate);
        Physics = new LoopStatistics("Physics", PhysicsRate);
        Ticks = new LoopStatistics("Simulation", TickRate);
    }

    public void Start()
    {
        if (running || thread is null) return;

        running = true;
        thread.Start();
    }

    /// <summary>Has the loop finish the tick it is in and stop. Waits for that, but not forever.</summary>
    public void Stop()
    {
        if (!running) return;

        running = false;
        if (thread is not null && Thread.CurrentThread != thread) thread.Join(2000);
    }

    /// <summary>
    /// Forgets how far behind the loop is. For when it was held up on purpose (a scene being set up) and is to carry on
    /// from now rather than make up for the time it stood still. Loop thread, also from inside of a tick, whatever
    /// ticks were still to be made to catch up are let go, and the next one is made straight away.
    /// </summary>
    public void Resynchronize() => next = double.NaN;

    private void Run()
    {
        Diagnostics.AllocationLog.NameThisThread(thread?.Name ?? "Simulation");
        LoopTiming.SharpenTimer(true);

        // Whatever is awaited on this thread carries on here, see SimulationContext
        SynchronizationContext.SetSynchronizationContext(Context);

        try
        {
            while (running)
            {
                if (!double.IsNaN(next))
                {
                    Ticks.SetState(LoopStatistics.LoopState.Sleeping);
                    LoopTiming.WaitUntil(next / frequency);
                }

                Step();
            }
        }
        finally
        {
            Ticks.SetState(LoopStatistics.LoopState.Stopped);
            LoopTiming.SharpenTimer(false);
        }
    }

    /// <summary>
    /// Makes every tick that is due by now (up to a few to catch up), and works out when the next one is.
    /// </summary>
    internal void Step()
    {
        long now = clock();
        double period = tickPeriod * frequency;

        // The first tick, or the first after being held up, from now on
        if (double.IsNaN(next))
            next = now;

        double behind = now - next;
        int due = 1 + (int)Math.Clamp(Math.Floor(behind / period), 0, MAX_CATCH_UP - 1);

        if (behind > period * MAX_CATCH_UP)
        {
            long dropped = (long)(behind / period) - (MAX_CATCH_UP - 1);
            Ticks.NoteDropped((int)Math.Min(dropped, int.MaxValue));

            // Carry on from now, the ticks made below lead up to it
            next = now - period * (MAX_CATCH_UP - 1);
        }

        double first = next;
        for (int i = 0; i < due && (running || thread is null); i++)
        {
            MakeTick((long)first + (long)(period * i));

            // Held up on purpose at the end of that one (a scene being set up), so the rest isn't made up for and the next
            // tick is from now
            if (double.IsNaN(next))
                return;
        }

        next += period * due;
    }

    /// <summary>
    /// Helper method to make one tick, the logic if it is due, the physics as many steps as are due, then the end of it.
    /// </summary>
    private void MakeTick(long stamp)
    {
        long started = clock();
        long allocated = GC.GetAllocatedBytesForCurrentThread();

        Ticks.SetState(LoopStatistics.LoopState.Blocked);
        host.BeginTick();
        long waited = clock() - started;
        Ticks.SetState(LoopStatistics.LoopState.Working);

        // What was waiting for this thread picks up where it left off, before anything of the tick is updated
        Context.RunPending();

        Tick++;
        Time += tickPeriod;
        logicOwed += tickPeriod;
        physicsOwed += tickPeriod;

        if (logicOwed + DUE_SLACK >= logicPeriod)
        {
            // Everything since the last turn, which at the rate of the ticks is one tick
            float dt = (float)logicOwed;
            logicOwed = 0.0;

            long before = clock();
            long logicAllocated = GC.GetAllocatedBytesForCurrentThread();
            host.UpdateState(dt);
            Logic.Record((clock() - before) / frequency, 0.0, SinceLast(ref lastLogicAt, before, dt), 1, GC.GetAllocatedBytesForCurrentThread() - logicAllocated);
        }

        int steps = 0;
        long physicsStarted = clock();
        long physicsAllocated = GC.GetAllocatedBytesForCurrentThread();
        while (physicsOwed + DUE_SLACK >= physicsPeriod)
        {
            physicsOwed -= physicsPeriod;
            host.UpdatePhysics((float)physicsPeriod);
            steps++;
        }

        if (steps > 0)
            Physics.Record((clock() - physicsStarted) / frequency, 0.0, SinceLast(ref lastPhysicsAt, physicsStarted, physicsPeriod), steps, GC.GetAllocatedBytesForCurrentThread() - physicsAllocated);

        host.EndTick(Tick, stamp, Time);

        long ended = clock();
        Ticks.Record((ended - started - waited) / frequency, waited / frequency, SinceLast(ref lastTickAt, started, tickPeriod), 1, GC.GetAllocatedBytesForCurrentThread() - allocated);
    }

    /// <summary>
    /// Helper method for how long (in real seconds) it has been since the last turn of something, and to note this one.
    /// </summary>
    private double SinceLast(ref long last, long now, double first)
    {
        double since = last == 0 ? first : (now - last) / frequency;
        last = now;
        return since;
    }

    public void Dispose() => Stop();
}
