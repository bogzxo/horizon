namespace Horizon.Core.Threading;

/// <summary>Where a <see cref="WorkItem"/> is, waiting its turn, going, or done one way or another.</summary>
public enum WorkState
{
    Waiting,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

/// <summary>
/// One piece of work a <see cref="WorkScheduler"/> runs off the UI's thread: an install, a download, a look at GitHub.
/// What it is (<see cref="Key"/>, <see cref="Title"/>), how far it got (<see cref="Progress"/>, <see cref="Message"/>),
/// what it wrote (<see cref="Output"/>), and how it ended. Everything here is safe to read from any thread, the UI's
/// reads it every frame to draw it. The work itself only ever sees a <see cref="WorkContext"/>.
/// </summary>
public sealed class WorkItem
{
    private readonly Lock gate = new();
    private readonly List<string> output = [];
    private float? progress;
    private string message = string.Empty;
    private volatile WorkState state = WorkState.Waiting;

    internal readonly CancellationTokenSource Cancelling = new();
    internal readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Bumped whenever anything about it changes, so whoever draws it can tell without comparing it all
    private int version;

    internal WorkItem(string key, string title, bool cancellable)
    {
        Key = key;
        Title = title;
        Cancellable = cancellable;
    }

    /// <summary>What it is known by, the same key started again while it runs is this one and not another.</summary>
    public string Key { get; }

    /// <summary>What it is, said to whoever is watching, "Updating csharp-ls".</summary>
    public string Title { get; }

    /// <summary>Whether it can be stopped part way through.</summary>
    public bool Cancellable { get; }

    public WorkState State => state;

    /// <summary>Whether it is waiting or going, not done.</summary>
    public bool IsActive => state is WorkState.Waiting or WorkState.Running;

    /// <summary>How far through it is, 0 to 1, null while that can't be said (a download of no known size, a command running).</summary>
    public float? Progress
    {
        get
        {
            lock (gate)
                return progress;
        }
    }

    /// <summary>What it is doing right now, a line, empty for nothing in particular.</summary>
    public string Message
    {
        get
        {
            lock (gate)
                return message;
        }
    }

    /// <summary>Why it failed, null unless it did.</summary>
    public string? Error { get; private set; }

    /// <summary>When it started running and when it ended, null until then.</summary>
    public DateTime? Started { get; private set; }

    public DateTime? Ended { get; private set; }

    /// <summary>Goes up with every change to it (its state, progress, message, output), for whoever draws it to tell it changed.</summary>
    public int Version => Volatile.Read(ref version);

    /// <summary>What it wrote, a line at a time (a command's output), a copy.</summary>
    public IReadOnlyList<string> Output
    {
        get
        {
            lock (gate)
                return [.. output];
        }
    }

    /// <summary>Done, one way or another, for awaiting from wherever.</summary>
    public Task Completion => Finished.Task;

    /// <summary>Asks it to stop. Work that looks at its token stops when it next does, waiting work never starts. Any thread.</summary>
    public void Cancel()
    {
        if (!Cancellable || !IsActive)
            return;

        try
        {
            Cancelling.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // Most of the output kept, the start of a command's is less use than its end
    private const int MOST_OUTPUT = 2000;

    internal void Report(float? at, string? said)
    {
        lock (gate)
        {
            if (at is { } fraction)
                progress = Math.Clamp(fraction, 0.0f, 1.0f);
            else if (said is null)
                progress = null;
            if (said is not null)
                message = said;
        }

        Interlocked.Increment(ref version);
    }

    internal void Write(string line)
    {
        lock (gate)
        {
            output.Add(line);
            if (output.Count > MOST_OUTPUT)
                output.RemoveRange(0, output.Count - MOST_OUTPUT);
        }

        Interlocked.Increment(ref version);
    }

    internal void Begin()
    {
        Started = DateTime.Now;
        state = WorkState.Running;
        Interlocked.Increment(ref version);
    }

    internal void End(WorkState how, string? error)
    {
        Error = error;
        Ended = DateTime.Now;
        if (how == WorkState.Succeeded)
            Report(1.0f, null);
        state = how;
        Interlocked.Increment(ref version);
    }
}

/// <summary>
/// What a piece of work is given to say how it is going and to hear that it should stop. From the work's own thread,
/// any of it, as often as it likes: progress is kept, not queued, whoever draws it takes the latest.
/// </summary>
public sealed class WorkContext
{
    private readonly WorkItem item;

    internal WorkContext(WorkItem item) => this.item = item;

    /// <summary>Set when the work was asked to stop, for passing on to whatever it awaits.</summary>
    public CancellationToken Token => item.Cancelling.Token;

    /// <summary>How far through it is, 0 to 1, with what it is doing (null leaves that as it was).</summary>
    public void Report(float progress, string? message = null) => item.Report(progress, message);

    /// <summary>What it is doing, with no telling how far through it is (a spinner rather than a bar).</summary>
    public void Say(string message) => item.Report(null, message);

    /// <summary>A line of what it did, kept with it (a command's output), the last of them shown as what it's doing.</summary>
    public void Write(string line, bool show = true)
    {
        item.Write(line);
        if (show && !string.IsNullOrWhiteSpace(line))
            item.Report(item.Progress, line.Trim());
    }

    /// <summary>Throws if it was asked to stop, for a loop to look at between steps.</summary>
    public void ThrowIfCancelled() => Token.ThrowIfCancellationRequested();
}
