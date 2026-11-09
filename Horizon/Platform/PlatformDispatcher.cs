using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace Horizon.Platform;

/// <summary>
/// The queue of work for a platform's thread, for a host to build its Post and Invoke on. Knows which thread is the
/// platform's (the one that bound it), runs work posted from anywhere when that thread drains it, and wakes it with
/// whatever the host waits on. Invoke from the platform's thread is done right there, from any other it waits.
/// </summary>
public sealed class PlatformDispatcher
{
    private readonly ConcurrentQueue<Action> work = new();
    private readonly Action wake;
    private int owner;
    private volatile bool closed;

    /// <param name="wake">Wakes the platform's thread out of whatever it waits on. From any thread.</param>
    public PlatformDispatcher(Action wake) => this.wake = wake;

    /// <summary>Makes the calling thread the platform's.</summary>
    public void BindToCurrentThread() => Volatile.Write(ref owner, Environment.CurrentManagedThreadId);

    /// <summary>Whether a thread was made the platform's yet.</summary>
    public bool IsBound => Volatile.Read(ref owner) != 0;

    /// <summary>Whether the calling thread is the platform's.</summary>
    public bool IsPlatformThread => Volatile.Read(ref owner) == Environment.CurrentManagedThreadId;

    /// <summary>Whether anything is waiting to be done.</summary>
    public bool IsEmpty => work.IsEmpty;

    /// <summary>Queues work for the platform's thread and wakes it. Dropped once the dispatcher is closed.</summary>
    public void Post(Action action)
    {
        if (closed) return;
        work.Enqueue(action);
        wake();
    }

    /// <summary>
    /// Does work on the platform's thread and waits for it, throwing whatever it threw. Right away on that thread, and
    /// right away before any thread was made the platform's (nothing is running yet to be in the way of).
    /// </summary>
    public void Invoke(Action action)
    {
        if (!IsBound || IsPlatformThread)
        {
            action();
            return;
        }

        ObjectDisposedException.ThrowIf(closed, this);

        var call = new Call(action);
        work.Enqueue(call.Run);
        wake();

        // Closed while it waited and nobody will ever get to it
        while (!call.Done.Wait(50))
        {
            if (closed && call.Abandon())
            {
                call.Done.Dispose();
                throw new ObjectDisposedException(nameof(PlatformDispatcher), "The platform stopped before it got to the work.");
            }
        }

        call.Done.Dispose();
        call.Failure?.Throw();
    }

    /// <summary>Does everything that was queued, in order. The platform's thread. How many there were.</summary>
    public int Drain()
    {
        int done = 0;
        while (work.TryDequeue(out Action? action))
        {
            action();
            done++;
        }

        return done;
    }

    /// <summary>
    /// Stops taking work. Whatever still waits is let go of with an exception rather than left to hang. The platform's
    /// thread, as the host goes.
    /// </summary>
    public void Close()
    {
        closed = true;
        work.Clear();
    }

    private sealed class Call(Action action)
    {
        private int state;
        public ManualResetEventSlim Done { get; } = new(false);
        public ExceptionDispatchInfo? Failure { get; private set; }

        public void Run()
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0) return;
            try
            {
                action();
            }
            catch (Exception e)
            {
                Failure = ExceptionDispatchInfo.Capture(e);
            }
            finally
            {
                Done.Set();
            }
        }

        // True when it never ran and now never will
        public bool Abandon() => Interlocked.CompareExchange(ref state, 2, 0) == 0;
    }
}
