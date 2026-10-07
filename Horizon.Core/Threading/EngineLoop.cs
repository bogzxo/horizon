using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Horizon.Core.Threading;

/// <summary>
/// One of the loops an engine runs (logic, physics): a thread of its own that calls something a set number of
/// times a second, keeps to that as closely as the system lets it, and keeps count of how it is doing
/// (<see cref="Statistics"/>).
/// <para>
/// Between two turns the thread sleeps for as long as it safely can and only spins for the last stretch, where a
/// sleep can't be trusted to end on time. So a loop that has little to do costs little, rather than a whole core
/// spent on asking what time it is.
/// </para>
/// <para>
/// Loops that share what they work on are given the same gate: only one of them is in its turn at a time, the
/// others wait for it to be done. That is how the logic and the physics of a game get a thread and a rate each
/// without either ever seeing the other halfway through a step.
/// </para>
/// </summary>
public sealed class EngineLoop : IDisposable
{
    // How close to the turn (in seconds) the thread stops sleeping and starts spinning. A sleep of a millisecond
    // is the shortest there is, and it can run a little over
    private const double SPIN_MARGIN = 0.0018;

    // The most steps a fixed loop makes in a row to catch up after falling behind, what is left after that is let go
    private const int MAX_CATCH_UP = 4;

    private static int timerUsers;

    private readonly Action<float> turn;
    private readonly object? gate;
    private readonly Thread thread;
    private volatile bool running;

    /// <summary>What the loop is called, which is also the name of its thread.</summary>
    public string Name { get; }

    /// <summary>How many turns a second the loop is meant to take.</summary>
    public double Rate { get; }

    /// <summary>
    /// Whether every turn is told the same length of time (one over the rate) whatever really passed, and turns
    /// that were missed are made up for. What physics wants: the same step every time gives the same result every time.
    /// Otherwise a turn is told how long it has been since the last one, and a missed turn is simply a longer one.
    /// </summary>
    public bool FixedStep { get; }

    /// <summary>How the loop has been doing lately.</summary>
    public LoopStatistics Statistics { get; }

    // The longest a turn is ever told it stands for when it is told how long it has really been: a loop that was
    // kept waiting (something loading) doesn't get to make up for all of it in one go
    private const double LONGEST_TURN = 1.0 / 30.0;

    private readonly SemaphoreSlim? pulse;
    private readonly Action? afterTurn;

    /// <param name="rate">How many turns a second.</param>
    /// <param name="turn">What a turn is, told how long (in seconds) it stands for.</param>
    /// <param name="gate">Held for the length of every turn. Loops that are given the same one never overlap.</param>
    /// <param name="pulse">
    /// For a loop that doesn't keep time by itself but takes a turn whenever it is told to, by this being released:
    /// once for every frame that is drawn, say, which is what has a game move as evenly as its frames come. The rate
    /// is then only what it falls back on while nobody tells it (the window is hidden and draws nothing), and every
    /// turn is told how long it has really been, fixed step or not.
    /// </param>
    /// <param name="afterTurn">Called after every turn, with the gate let go of again.</param>
    public EngineLoop(string name, double rate, Action<float> turn, bool fixedStep = false, object? gate = null, ThreadPriority priority = ThreadPriority.AboveNormal, SemaphoreSlim? pulse = null, Action? afterTurn = null)
    {
        Name = name;
        Rate = Math.Max(1.0, rate);
        FixedStep = fixedStep && pulse is null;
        Statistics = new LoopStatistics(name, pulse is null ? Rate : 0.0);

        this.turn = turn;
        this.gate = gate;
        this.pulse = pulse;
        this.afterTurn = afterTurn;

        thread = new Thread(Run)
        {
            Name = name,
            Priority = priority,
            IsBackground = true
        };
    }

    public void Start()
    {
        if (running) return;

        running = true;
        thread.Start();
    }

    /// <summary>Has the loop finish the turn it is in and stop. Waits for that, but not forever.</summary>
    public void Stop()
    {
        if (!running) return;

        running = false;
        if (Thread.CurrentThread != thread) thread.Join(2000);
    }

    private void Run()
    {
        SharpenTimer(true);

        double period = 1.0 / Rate;
        long frequency = Stopwatch.Frequency;
        long previous = Stopwatch.GetTimestamp();
        double next = previous / (double)frequency + period;

        try
        {
            while (running)
            {
                // Told when, or by the clock. Somebody who stops telling is not waited on for long
                if (pulse is not null) pulse.Wait(TimeSpan.FromSeconds(period * 2.0));
                else WaitUntil(next);

                long now = Stopwatch.GetTimestamp();
                double elapsed = (now - previous) / (double)frequency;
                previous = now;

                // A turn that was kept waiting (a scene being loaded holds the gate for as long as that takes)
                // doesn't get told all of it: tweens and timers would jump straight past whatever they were at
                if (!FixedStep) elapsed = Math.Min(elapsed, LONGEST_TURN);

                // How many turns are due. More than one only for a fixed loop that fell behind
                int turns = 1;
                if (FixedStep)
                {
                    double behind = now / (double)frequency - next;
                    turns += Math.Clamp((int)(behind / period), 0, MAX_CATCH_UP - 1);
                    if (behind > period * MAX_CATCH_UP) Statistics.NoteDropped((int)(behind / period) - (MAX_CATCH_UP - 1));
                }

                long started = Stopwatch.GetTimestamp();
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                long waited = 0;

                for (int i = 0; i < turns && running; i++)
                {
                    float dt = FixedStep ? (float)period : (float)elapsed;

                    if (gate is null)
                    {
                        turn(dt);
                        continue;
                    }

                    // The wait for whoever else has the gate is not our work. A turn gate lets us in in the order we asked,
                    // so whoever draws can't keep walking in ahead of us
                    long asked = Stopwatch.GetTimestamp();

                    if (gate is TurnGate fair)
                    {
                        fair.Enter();
                        try
                        {
                            waited += Stopwatch.GetTimestamp() - asked;
                            turn(dt);
                        }
                        finally
                        {
                            fair.Exit();
                        }

                        continue;
                    }

                    lock (gate)
                    {
                        waited += Stopwatch.GetTimestamp() - asked;
                        turn(dt);
                    }
                }

                afterTurn?.Invoke();

                long ended = Stopwatch.GetTimestamp();
                Statistics.Record(
                    (ended - started - waited) / (double)frequency,
                    waited / (double)frequency,
                    elapsed,
                    turns,
                    GC.GetAllocatedBytesForCurrentThread() - allocated);

                // The next turn is one period after this one was due. A loop that has fallen hopelessly behind
                // starts counting from now instead of trying to make up for all of it
                next += period * turns;
                double current = ended / (double)frequency;
                if (next < current - period) next = current;
            }
        }
        finally
        {
            SharpenTimer(false);
        }
    }

    /// <summary>
    /// Helper method to wait for a moment (in seconds of the stopwatch): asleep for most of it, awake for the end.
    /// </summary>
    internal static void WaitUntil(double moment)
    {
        double frequency = Stopwatch.Frequency;

        while (true)
        {
            double left = moment - Stopwatch.GetTimestamp() / frequency;
            if (left <= 0.0) return;

            if (left > SPIN_MARGIN) Thread.Sleep(1);
            else Thread.SpinWait(32);
        }
    }

    /// <summary>
    /// Helper method to have the system wake sleeping threads to the millisecond for as long as any loop runs.
    /// Windows otherwise only looks every 15 or so, which no loop could keep time by.
    /// </summary>
    internal static void SharpenTimer(bool on)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            if (on && Interlocked.Increment(ref timerUsers) == 1) timeBeginPeriod(1);
            if (!on && Interlocked.Decrement(ref timerUsers) == 0) timeEndPeriod(1);
        }
        catch (Exception)
        {
            // Without it the loops still run, they spin for more of their wait
        }
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint milliseconds);

    public void Dispose() => Stop();
}