using System.Collections.Concurrent;

namespace Horizon.Core.Threading;

/// <summary>
/// What has the simulation thread pick up where an <c>await</c> left off. Whatever is posted to it runs on that
/// thread at the start of the next tick, before anything of that tick is updated. So game code can wait on a tween,
/// a timer or a download with <c>await</c> and carry on as if it never left, without anything of it ever running
/// in the middle of a tick or on some other thread. It is the <see cref="SynchronizationContext"/> of the
/// simulation thread, and <see cref="SimulationLoop.Context"/> for whoever wants to hand it work from elsewhere.
/// </summary>
public sealed class SimulationContext : SynchronizationContext
{
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> posted = new();
    private int ownerThread = -1;

    /// <summary>Has something run on the simulation thread at the start of the next tick. From any thread.</summary>
    public override void Post(SendOrPostCallback d, object? state) => posted.Enqueue((d, state));

    /// <summary>
    /// Has something run on the simulation thread and waits for it, straight away when called on that thread. The
    /// wait is for the start of the next tick, so never from anything the simulation itself is waiting on.
    /// </summary>
    public override void Send(SendOrPostCallback d, object? state)
    {
        if (Environment.CurrentManagedThreadId == Volatile.Read(ref ownerThread))
        {
            d(state);
            return;
        }

        using var done = new ManualResetEventSlim(false);
        Exception? failed = null;

        Post(_ =>
        {
            try
            {
                d(state);
            }
            catch (Exception e)
            {
                failed = e;
            }
            finally
            {
                done.Set();
            }
        }, null);

        done.Wait();
        if (failed is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failed);
    }

    public override SynchronizationContext CreateCopy() => this;

    /// <summary>
    /// Runs whatever was posted so far, in the order it was. What that posts in turn waits for the next time, so a
    /// loop of awaits can't keep a tick from ever starting. Simulation thread, at the start of a tick.
    /// </summary>
    internal void RunPending()
    {
        Volatile.Write(ref ownerThread, Environment.CurrentManagedThreadId);

        SynchronizationContext? before = Current;
        SetSynchronizationContext(this);
        try
        {
            for (int left = posted.Count; left > 0 && posted.TryDequeue(out var work); left--)
                work.Callback(work.State);
        }
        finally
        {
            SetSynchronizationContext(before);
        }
    }
}
