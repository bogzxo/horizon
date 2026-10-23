using System.Collections.Concurrent;

namespace Horizon.Core.Threading;

/// <summary>
/// Runs work off the UI's thread, on the thread pool, so many at a time and the rest waiting their turn: installs,
/// downloads, looks at GitHub, anything that would freeze a frame. Each piece is known by a key, and a key that is
/// already waiting or running isn't started again, whoever asks gets the one there is (an update pressed twice is one
/// update). The work says how far it got through its <see cref="WorkContext"/>, and the UI reads that off the
/// <see cref="WorkItem"/> as it draws. What's to be done once a piece is over (its <c>done</c>) is done on the UI's thread,
/// in <see cref="Pump"/>, which the UI calls every tick, never on the pool's.
/// </summary>
public sealed class WorkScheduler
{
    // The work that ended is kept a while for whoever lists it, this many of them
    private const int KEPT_ENDED = 30;

    private readonly Lock gate = new();
    private readonly Dictionary<string, WorkItem> byKey = new(StringComparer.Ordinal);
    private readonly Queue<(WorkItem Item, Func<WorkContext, Task> Work)> waiting = new();
    private readonly List<WorkItem> items = [];
    private readonly Dictionary<WorkItem, List<Action<WorkItem>>> whenDone = [];
    private readonly ConcurrentQueue<Action> forTheUi = new();
    private int running;

    /// <param name="mostAtOnce">How many run at once, the rest wait, 0 for as many as there are cores (two to eight).</param>
    public WorkScheduler(int mostAtOnce = 0)
    {
        MostAtOnce = mostAtOnce > 0 ? mostAtOnce : Math.Clamp(Environment.ProcessorCount, 2, 8);
    }

    /// <summary>How many run at once.</summary>
    public int MostAtOnce { get; }

    /// <summary>Called on the pool's thread when something ended, for a UI that sleeps between frames to be woken.</summary>
    public Action? Woken { get; set; }

    /// <summary>A piece of work was started, on the UI's thread in <see cref="Pump"/>, for whoever shows it.</summary>
    public event Action<WorkItem>? Added;

    /// <summary>A piece of work ended, on the UI's thread in <see cref="Pump"/>, after its own <c>done</c>.</summary>
    public event Action<WorkItem>? Ended;

    /// <summary>What's waiting and going, then what ended lately, the newest of each first. A copy.</summary>
    public IReadOnlyList<WorkItem> Items
    {
        get
        {
            lock (gate)
                return [.. items.Where(item => item.IsActive).Reverse(), .. items.Where(item => !item.IsActive).Reverse()];
        }
    }

    /// <summary>
    /// The newest piece of work waiting or going that a test lets through (every one without it), null for none. What the
    /// status bar shows every tick while something goes, Items made a copy of the lot for it.
    /// </summary>
    public WorkItem? NewestActive(Func<WorkItem, bool>? listed = null)
    {
        lock (gate)
        {
            for (int i = items.Count - 1; i >= 0; i--)
            {
                if (items[i].IsActive && (listed?.Invoke(items[i]) ?? true))
                    return items[i];
            }
        }

        return null;
    }

    /// <summary>How many are waiting or going.</summary>
    public int Active
    {
        get
        {
            lock (gate)
                return byKey.Count;
        }
    }

    /// <summary>The work of a key that's waiting or going, null when there's none.</summary>
    public WorkItem? Find(string key)
    {
        lock (gate)
            return byKey.GetValueOrDefault(key);
    }

    /// <summary>Whether the work of a key is waiting or going.</summary>
    public bool IsActive(string key) => Find(key) is not null;

    /// <summary>
    /// Starts a piece of work, or hands back the one of its key that's already waiting or going (its <paramref name="done"/>
    /// then waits on that one). <paramref name="done"/> is called on the UI's thread once it's over, however it ended.
    /// </summary>
    public WorkItem Start(string key, string title, Func<WorkContext, Task> work, Action<WorkItem>? done = null, bool cancellable = true)
    {
        WorkItem item;
        bool fresh;
        lock (gate)
        {
            fresh = !byKey.TryGetValue(key, out item!);
            if (fresh)
            {
                item = new WorkItem(key, title, cancellable);
                byKey[key] = item;
                items.Add(item);
                Trim();
            }

            if (done is not null)
            {
                if (!whenDone.TryGetValue(item, out var listeners))
                    whenDone[item] = listeners = [];
                listeners.Add(done);
            }

            if (fresh)
            {
                if (running < MostAtOnce)
                {
                    running++;
                    Launch(item, work);
                }
                else
                {
                    waiting.Enqueue((item, work));
                }
            }
        }

        if (fresh)
            forTheUi.Enqueue(() => Added?.Invoke(item));
        return item;
    }

    /// <summary>Work that is done on a thread of the pool from start to end, a blocking call and no awaiting.</summary>
    public WorkItem Start(string key, string title, Action<WorkContext> work, Action<WorkItem>? done = null, bool cancellable = true) =>
        Start(key, title, context => Task.Run(() => work(context), context.Token), done, cancellable);

    /// <summary>Work with a result, handed to <paramref name="done"/> on the UI's thread, default when it didn't succeed.</summary>
    public WorkItem Start<T>(string key, string title, Func<WorkContext, Task<T>> work, Action<WorkItem, T?> done, bool cancellable = true)
    {
        T? result = default;
        return Start(key, title, async context => result = await work(context).ConfigureAwait(false), item => done(item, item.State == WorkState.Succeeded ? result : default), cancellable);
    }

    /// <summary>Asks everything waiting or going to stop.</summary>
    public void CancelAll()
    {
        WorkItem[] active;
        lock (gate)
            active = [.. byKey.Values];
        foreach (WorkItem item in active)
            item.Cancel();
    }

    /// <summary>Does on the UI's thread what the work that ended left for it. Every tick, from the UI's thread.</summary>
    public void Pump()
    {
        while (forTheUi.TryDequeue(out Action? action))
            action();
    }

    // Helper method to run a piece of work on the pool, and whatever is waiting once it's done. Under the lock
    private void Launch(WorkItem item, Func<WorkContext, Task> work)
    {
        _ = Task.Run(async () =>
        {
            var context = new WorkContext(item);
            WorkState how;
            string? error = null;
            if (item.Cancelling.IsCancellationRequested)
            {
                how = WorkState.Cancelled;
            }
            else
            {
                item.Begin();
                try
                {
                    await work(context).ConfigureAwait(false);
                    how = item.Cancelling.IsCancellationRequested ? WorkState.Cancelled : WorkState.Succeeded;
                }
                catch (OperationCanceledException) when (item.Cancelling.IsCancellationRequested)
                {
                    how = WorkState.Cancelled;
                }
                catch (Exception e)
                {
                    how = WorkState.Failed;
                    error = e.Message;
                }
            }

            Finish(item, how, error);
        });
    }

    private void Finish(WorkItem item, WorkState how, string? error)
    {
        List<Action<WorkItem>>? listeners;
        lock (gate)
        {
            byKey.Remove(item.Key);
            whenDone.Remove(item, out listeners);
            running--;

            // The next waiting its turn, one that was cancelled while it waited ends without running
            while (waiting.TryDequeue(out var next))
            {
                running++;
                Launch(next.Item, next.Work);
                break;
            }
        }

        item.End(how, error);
        forTheUi.Enqueue(() =>
        {
            foreach (Action<WorkItem> listener in listeners ?? [])
                listener(item);
            Ended?.Invoke(item);
        });
        Woken?.Invoke();
        item.Cancelling.Dispose();
    }

    // Helper method to let go of the oldest of what ended. Under the lock
    private void Trim()
    {
        int ended = items.Count(item => !item.IsActive);
        for (int i = 0; i < items.Count && ended > KEPT_ENDED; i++)
        {
            if (!items[i].IsActive)
            {
                items.RemoveAt(i--);
                ended--;
            }
        }
    }
}
