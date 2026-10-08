using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Runtime.InteropServices;
using System.Text;

namespace Horizon.Core.Diagnostics;

/// <summary>
/// Keeps count of what the game allocates, by thread and by type, for finding out where the garbage comes from without
/// a profiler. It listens to the runtime saying it allocated another hundred or so KB and what it was allocating when it
/// did, which is a sample rather than every allocation: over a few seconds it adds up to the real thing, near enough.
/// Switched on with <c>HORIZON_LOG_ALLOCATIONS</c> set to a number of seconds, see <see cref="WindowManager"/>.
/// </summary>
internal sealed class AllocationLog : EventListener
{
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
    /// What was allocated since the last report: so much a second on every thread, and the types most of it went on.
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

        return taken.Count == 0 ? "Allocating next to nothing." : text.ToString();
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("libc", SetLastError = false)]
    private static extern int gettid();
}
