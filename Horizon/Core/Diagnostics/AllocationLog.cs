using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Runtime.InteropServices;
using System.Text;

namespace Horizon.Core.Diagnostics;

/// <summary>
/// Keeps count of what the game allocates, by thread and by type, for finding out where the garbage comes from without
/// a profiler. It listens to the runtime saying it allocated another hundred or so KB and what it was allocating when it
/// did, which is a sample rather than every allocation. Over a few seconds it adds up to the real thing, near enough.
/// Switched on with <c>HORIZON_LOG_ALLOCATIONS</c> set to a number of seconds, see <see cref="WindowManager"/>.
/// <para>
/// The runtime only says what was allocated, a string, a delegate, and never who by. So while this is on every
/// entity and component is also measured as it is updated, to the byte, by what its thread had allocated before
/// and after (see <see cref="Begin"/> and <see cref="End"/>, which <see cref="Entity"/> calls around each of its
/// children). What a thing allocated itself is what was allocated while it was updated less what the things
/// inside of it allocated, and that is what the report says under whose update it was. "A string, four hundred
/// bytes a tick" finds nobody. "The input display, four hundred bytes a tick" does.
/// </para>
/// </summary>
internal sealed class AllocationLog : EventListener
{
    /// <summary>Whether anybody is counting. Read on every update of every entity, so it is a field and nothing cleverer.</summary>
    internal static bool Tracking;

    // What the thread has put down to somebody already, so whoever is around them doesn't count it again
    [ThreadStatic]
    private static long attributed;

    // What every kind of entity and component allocated in its own updates, by the thread it was on
    private static readonly Lock ownersGate = new();
    private static Dictionary<(string Thread, Type Owner), long> owners = [];

    /// <summary>
    /// Starts measuring what something allocates while it is updated. Only when <see cref="Tracking"/>.
    /// </summary>
    /// <param name="nested">What had been put down to others before, to be handed back to <see cref="End"/>.</param>
    /// <returns>What the thread had allocated so far, likewise.</returns>
    internal static long Begin(out long nested)
    {
        nested = attributed;
        return GC.GetAllocatedBytesForCurrentThread();
    }

    /// <summary>Stops measuring and puts what was allocated, less what the things inside of it were put down for, to its kind.</summary>
    internal static void End(object owner, long before, long nested)
    {
        long all = GC.GetAllocatedBytesForCurrentThread() - before;
        long own = all - (attributed - nested);
        attributed = nested + all;

        if (own <= 0)
            return;

        string thread = threadNames.TryGetValue(CurrentThread(), out string? known) ? known : "other";
        lock (ownersGate)
        {
            ref long total = ref CollectionsMarshal.GetValueRefOrAddDefault(owners, (thread, owner.GetType()), out _);
            total += own;
        }
    }

    private static long CurrentThread()
    {
        try
        {
            return OperatingSystem.IsWindows() ? GetCurrentThreadId() : OperatingSystem.IsLinux() ? gettid() : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    public AllocationLog()
    {
        Tracking = true;
    }

    public override void Dispose()
    {
        Tracking = false;
        base.Dispose();
    }

    private const string RUNTIME = "Microsoft-Windows-DotNETRuntime";
    private const EventKeywords GC_KEYWORD = (EventKeywords)0x1;

    // Threads by the id the system knows them by, which is what the runtime says an allocation was made on
    private static readonly ConcurrentDictionary<long, string> threadNames = new();

    private readonly Lock gate = new();
    private Dictionary<(string Thread, string Type), long> counted = [];
    private DateTime since = DateTime.UtcNow;

    /// <summary>Gives the calling thread a name in the log, rather than having it lumped in with the rest.</summary>
    public static void NameThisThread(string name)
    {
        try
        {
            long id = OperatingSystem.IsWindows() ? GetCurrentThreadId() : OperatingSystem.IsLinux() ? gettid() : 0;
            if (id != 0)
                threadNames[id] = name;
        }
        catch (Exception)
        {
            // Not on this system, that thread is just "other"
        }
    }

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == RUNTIME)
            EnableEvents(source, EventLevel.Verbose, GC_KEYWORD);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventName is not { } name || !name.StartsWith("GCAllocationTick", StringComparison.Ordinal) || e.PayloadNames is not { } names || e.Payload is not { } payload)
            return;

        int typeAt = names.IndexOf("TypeName"), amountAt = names.IndexOf("AllocationAmount64");
        if (amountAt < 0) amountAt = names.IndexOf("AllocationAmount");
        if (typeAt < 0 || amountAt < 0)
            return;

        string type = payload[typeAt] as string ?? "?";
        long amount = Convert.ToInt64(payload[amountAt]);
        string thread = threadNames.TryGetValue(e.OSThreadId, out string? known) ? known : "other";

        lock (gate)
        {
            ref long total = ref CollectionsMarshal.GetValueRefOrAddDefault(counted, (thread, type), out _);
            total += amount;
        }
    }

    /// <summary>
    /// What was allocated since the last report, so much a second on every thread, and the types most of it went on.
    /// Starts counting again.
    /// </summary>
    public string Report(int topTypes = 6)
    {
        Dictionary<(string Thread, string Type), long> taken;
        double seconds;
        lock (gate)
        {
            (taken, counted) = (counted, []);
            DateTime now = DateTime.UtcNow;
            seconds = Math.Max(0.001, (now - since).TotalSeconds);
            since = now;
        }

        var text = new StringBuilder("Allocating (sampled):");
        foreach (var thread in taken.GroupBy(pair => pair.Key.Thread).OrderByDescending(group => group.Sum(pair => pair.Value)))
        {
            text.Append($" {thread.Key} {thread.Sum(pair => pair.Value) / seconds / 1024.0:0} KB/s [");
            text.AppendJoin(", ", thread
                .OrderByDescending(pair => pair.Value)
                .Take(topTypes)
                .Select(pair => $"{pair.Key.Type} {pair.Value / seconds / 1024.0:0}"));
            text.Append(']');
        }

        // And whose update it was allocated in, which is exact
        Dictionary<(string Thread, Type Owner), long> by;
        lock (ownersGate)
            (by, owners) = (owners, []);

        if (by.Count > 0)
        {
            text.Append(taken.Count == 0 ? " Nothing sampled." : string.Empty).Append(" In whose update, to the byte:");
            foreach (var thread in by.GroupBy(pair => pair.Key.Thread).OrderByDescending(group => group.Sum(pair => pair.Value)))
            {
                text.Append($" {thread.Key} {thread.Sum(pair => pair.Value) / seconds / 1024.0:0.0} KB/s [");
                text.AppendJoin(", ", thread
                    .OrderByDescending(pair => pair.Value)
                    .Take(12)
                    .Select(pair => $"{pair.Key.Owner.Name} {pair.Value / seconds / 1024.0:0.0}"));
                text.Append(']');
            }
        }

        return taken.Count == 0 && by.Count == 0 ? "Allocating next to nothing." : text.ToString();
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("libc", SetLastError = false)]
    private static extern int gettid();
}
