using System.Text.Json.Nodes;

namespace Horizon.UI.Scripting;

// What's wrong with a file asked for, for a server that waits to be asked rather than saying it (the protocol's pull
// diagnostics, textDocument/diagnostic). TypeScript 7's server is one, it opened files and said nothing of them. The
// answer goes the way a server's own word does (Published), with the version it was asked about so an answer about older
// text is let go.
//
// Asked once a file has stopped changing, never a request a keystroke: csharp-ls offers it too and answers requests in
// turn, and a whole file's diagnostics asked after every key (and every other open file's, its files depending on each
// other) put minutes of work in front of the completion being waited on. One request a file at a time; a change while
// it's out asks once more when it's back. A server that says what it knows changed (workspace/diagnostic/refresh) has
// everything open asked again the same way.
public sealed partial class LanguageServer
{
    // How long after a file's last change it's asked about
    private const int PULL_AFTER_MS = 400;

    // The files asked about now, and those that changed since they were asked
    private readonly HashSet<string> pulling = new(Horizon.Core.Platform.PlatformPaths.Comparer);
    private readonly HashSet<string> pullAgain = new(Horizon.Core.Platform.PlatformPaths.Comparer);
    private readonly Dictionary<string, Timer> pullTimers = new(Horizon.Core.Platform.PlatformPaths.Comparer);

    // Whether the server waits to be asked
    private bool PullsDiagnostics => PullDiagnosticsWanted && Offers("diagnosticProvider");

    // A file's asking put off until it's been still a moment, the timer starting again with every change
    private void PullSoon(string full)
    {
        if (!PullsDiagnostics)
            return;

        lock (pulling)
        {
            if (!pullTimers.TryGetValue(full, out Timer? timer))
                pullTimers[full] = timer = new Timer(_ => PullDiagnostics(full));
            timer.Change(PULL_AFTER_MS, Timeout.Infinite);
        }
    }

    // Asks about one file at the version it's at, behind what the editor's waiting on (HoldBackground)
    private void PullDiagnostics(string full)
    {
        if (!PullsDiagnostics || Failure is not null)
            return;

        int version;
        lock (versions)
        {
            if (!versions.TryGetValue(full, out version))
                return;
        }

        lock (pulling)
        {
            if (!pulling.Add(full))
            {
                pullAgain.Add(full);
                return;
            }
        }

        string uri = UriOf(full);
        _ = Ask("textDocument/diagnostic", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } }).ContinueWith(asked =>
        {
            // "unchanged" only comes back to a client that sent a previous result's id, which this never does
            if (asked.Result is JsonObject { } report && report["kind"]?.GetValue<string>() == "full")
                Published(new JsonObject { ["uri"] = uri, ["version"] = version, ["diagnostics"] = report["items"]?.DeepClone() ?? new JsonArray() });

            bool again;
            lock (pulling)
            {
                pulling.Remove(full);
                again = pullAgain.Remove(full);
            }
            if (again)
                PullSoon(full);
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// What's wrong with an open file asked at once, for a server that offers it whether or not it's one that has to be
    /// asked: what it says goes the way its own word does (TryTake), so a tool waiting on what a server says of a change
    /// it just sent hears it as soon as the server has looked, rather than whenever its own timer publishes. False when
    /// the server doesn't offer it or hasn't the file open.
    /// </summary>
    public bool AskDiagnosticsNow(string path)
    {
        string full = Path.GetFullPath(path);
        if (!Offers("diagnosticProvider") || Failure is not null || DocumentVersion(full) is not { } version)
            return false;

        string uri = UriOf(full);
        _ = Ask("textDocument/diagnostic", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } }, urgent: true).ContinueWith(asked =>
        {
            if (asked.Result is JsonObject { } report && report["kind"]?.GetValue<string>() == "full")
                Published(new JsonObject { ["uri"] = uri, ["version"] = version, ["diagnostics"] = report["items"]?.DeepClone() ?? new JsonArray() });
        }, TaskScheduler.Default);
        return true;
    }

    // The server says what it knows changed, everything open asked again once things are still
    private void DiagnosticsRefreshed()
    {
        if (!PullsDiagnostics)
            return;

        string[] open;
        lock (versions)
            open = [.. versions.Keys];
        foreach (string path in open)
            PullSoon(path);
    }

    // A file closed, nothing more asked of it
    private void ForgetPulls(string full)
    {
        lock (pulling)
        {
            if (pullTimers.Remove(full, out Timer? timer))
                timer.Dispose();
            pullAgain.Remove(full);
        }
    }
}
