using System.Diagnostics;

namespace Horizon.Core.Threading;

/// <summary>
/// A gate threads take turns at, in the order they came: whoever asked first gets in first.
/// <para>
/// A plain lock doesn't care who has been waiting the longest. Whoever lets go of it and asks for it again straight
/// away (drawing as many frames as there is time for does exactly that) gets it again, and a thread that was woken to
/// take its turn finds the lock taken by the time it runs. This hands out tickets instead: <see cref="Enter"/> takes
/// the next one and waits until it is served, <see cref="Exit"/> serves the next ticket. Nobody can walk in ahead of
/// somebody who was there before them, however fast they are.
/// </para>
/// <para>
/// Whoever is in can step out for a while to let somebody else do something (<see cref="Pause"/>) and is let back in
/// when they say it is done (<see cref="Signal"/>), or after a while regardless. The same thread can go in again while it
/// is in, it is let through and has to come out as often as it went in.
/// </para>
/// </summary>
public sealed class TurnGate
{
    // How many times a thread asks again before it goes to sleep until its turn. A turn that is about to end is
    // usually over before a thread could be woken from sleep
    private const int SPINS = 24;

    private readonly object sleepers = new();

    // The next ticket to hand out, and the one whose turn it is
    private long nextTicket, serving;

    // Who is in and how many times over, and how often somebody said they are done with whatever was paused for
    private int owner, depth;
    private long signals;

    /// <summary>Whether a thread is waiting for its turn right now.</summary>
    public bool HasWaiters => Volatile.Read(ref nextTicket) - Volatile.Read(ref serving) > 1;

    /// <summary>Whether this thread is in.</summary>
    public bool IsHeldByCurrentThread => Volatile.Read(ref owner) == Environment.CurrentManagedThreadId;

    /// <summary>
    /// Takes the next ticket and waits for it to be served. Returns with this thread in.
    /// </summary>
    public void Enter()
    {
        int me = Environment.CurrentManagedThreadId;
        if (owner == me)
        {
            depth++;
            return;
        }

        long ticket = Interlocked.Increment(ref nextTicket) - 1;
        if (Volatile.Read(ref serving) != ticket)
            WaitFor(ticket);

        owner = me;
        depth = 1;
    }

    /// <summary>
    /// Lets the next ticket in, once this thread has come out as often as it went in.
    /// </summary>
    public void Exit()
    {
        if (owner != Environment.CurrentManagedThreadId)
            throw new SynchronizationLockException("Only the thread that is in can come out.");

        if (--depth > 0)
            return;

        owner = 0;

        // Under the lock the sleepers check under, so none of them can miss its turn between looking and going to sleep
        lock (sleepers)
        {
            Interlocked.Increment(ref serving);
            Monitor.PulseAll(sleepers);
        }
    }

    /// <summary>
    /// Steps out for whoever is next and waits until somebody calls <see cref="Signal"/> (or for as long as it is allowed
    /// to), then gets back in line. Returns with this thread in again, as many times over as it was before.
    /// </summary>
    /// <returns>Whether it was signalled, rather than given up on waiting.</returns>
    public bool Pause(TimeSpan longest)
    {
        if (owner != Environment.CurrentManagedThreadId)
            throw new SynchronizationLockException("Only the thread that is in can step out.");

        int times = depth;
        long seen = Volatile.Read(ref signals);

        depth = 1;
        Exit();

        bool signalled;
        long until = Stopwatch.GetTimestamp() + (long)(longest.TotalSeconds * Stopwatch.Frequency);

        lock (sleepers)
        {
            while (!(signalled = Volatile.Read(ref signals) != seen))
            {
                long left = until - Stopwatch.GetTimestamp();
                if (left <= 0)
                    break;

                Monitor.Wait(sleepers, TimeSpan.FromSeconds(left / (double)Stopwatch.Frequency));
            }
        }

        Enter();
        depth = times;
        return signalled;
    }

    /// <summary>
    /// Tells whoever stepped out with <see cref="Pause"/> that what they were waiting for has happened. From any thread.
    /// </summary>
    public void Signal()
    {
        lock (sleepers)
        {
            Interlocked.Increment(ref signals);
            Monitor.PulseAll(sleepers);
        }
    }

    /// <summary>
    /// Helper method to wait for a ticket to be served: asking again for a moment, then asleep until it is.
    /// </summary>
    private void WaitFor(long ticket)
    {
        for (int spin = 0; spin < SPINS; spin++)
        {
            Thread.SpinWait(16 << Math.Min(spin, 6));
            if (Volatile.Read(ref serving) == ticket)
                return;
        }

        lock (sleepers)
        {
            while (Volatile.Read(ref serving) != ticket)
                Monitor.Wait(sleepers);
        }
    }
}
