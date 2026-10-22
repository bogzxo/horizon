using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;

using Horizon.UI.Components;

namespace Horizon.UI.Scripting;

/// <summary>What a <see cref="LanguageServer"/> offered to complete at a place, in the order it would have them.</summary>
/// <param name="Items">What can go there, ready for a <see cref="UICompletionRequest"/>.</param>
/// <param name="Incomplete">Whether the server only sent some of it, and wants asking again as more is typed.</param>
public readonly record struct LanguageServerCompletions(IReadOnlyList<UICompletion> Items, bool Incomplete);

/// <summary>What happened to a file on the disk, as the protocol numbers it (workspace/didChangeWatchedFiles).</summary>
public enum LanguageServerFileChange
{
    Created = 1,
    Changed = 2,
    Deleted = 3
}

/// <summary>
/// A language server (the protocol VS Code's linting, completion and hovers come through, microsoft.github.io/language-server-protocol)
/// run as a process and talked to over its stdin and stdout, so far for what it has to say about the files open in it.
/// It is started with a root folder, told about a file as it is opened (<see cref="Open"/>), sent the whole text again as
/// it changes (<see cref="Change"/>, the server is asked for full sync, which every one of them does) and told when it
/// is closed, and whatever it publishes about a file queues up here (<see cref="TryTake"/>) for the editor's thread to
/// take on its next tick, as <see cref="UICodeDiagnostic"/>s ready for <see cref="CodeEditor.Diagnostics"/>.
/// <see cref="Woken"/> is called on the reading thread when anything arrives, for an application that sleeps
/// between frames to be woken for it. Anything asked of the server before it has answered initialize waits in order
/// until it has, so a file can be opened the moment the server is started. Completions, hovers and signatures go
/// through the same connection when they come, onto the editor's CompletionSource, HoverSource and SignatureSource.
/// </summary>
public sealed partial class LanguageServer : IDisposable
{
    /// <summary>What it is called in a configuration, csharp-ls, pyright, what a status bar says.</summary>
    public string Name { get; }

    /// <summary>The language id the protocol knows the files by, csharp, python, json.</summary>
    public string LanguageId { get; }

    /// <summary>The folder it was started in, the workspace as far as the server knows.</summary>
    public string RootDirectory { get; }

    /// <summary>Why it isn't running any more, null while it is or before it was started.</summary>
    public string? Failure { get; private set; }

    /// <summary>Whether its process is up and the connection open.</summary>
    public bool IsRunning => rpc is { IsClosed: false } && process is { HasExited: false };

    /// <summary>
    /// What it's told to be, its settings by their sections the way an editor's settings have them
    /// (<c>{ d: { lintOnFileOpen: "always" } }</c> for serve-d), said once it's up and answered whenever it asks for a
    /// section. Null for a server left to its own, which isn't told the editor has any.
    /// </summary>
    public JsonObject? Settings { get; }

    /// <summary>Whether initialize has been answered, after which what was asked goes straight out.</summary>
    public bool IsReady { get; private set; }

    /// <summary>Called on the server's reading thread when diagnostics arrive, for the editor's thread to be woken.</summary>
    public Action? Woken { get; set; }

    private readonly Process? process;
    private readonly JsonRpcConnection rpc;
    private readonly Dictionary<string, int> versions = new(Horizon.Core.Platform.PlatformPaths.Comparer);
    private readonly ConcurrentQueue<(string Path, IReadOnlyList<UICodeDiagnostic> Found)> arrived = new();
    private readonly Lock queue = new();
    private List<Action>? waiting = [];

    // What has been asked of the server and not answered, answered with nothing when it stops
    private readonly ConcurrentDictionary<TaskCompletionSource<JsonNode?>, byte> asking = new();

    private LanguageServer(string name, string languageId, string rootDirectory, Process? process, JsonRpcConnection rpc, JsonObject? settings = null)
    {
        Name = name;
        LanguageId = languageId;
        RootDirectory = rootDirectory;
        Settings = settings;
        this.process = process;
        this.rpc = rpc;
        rpc.OnNotification("textDocument/publishDiagnostics", Published);
        ListenForTalk();
        rpc.Closed = why => Fail(why?.Message ?? "the server closed the connection");
        Initialize();
    }

    /// <summary>
    /// Starts a server's process in a folder and begins initializing it. Null when the process can't be started
    /// (the command isn't there), see <see cref="LanguageServers.IsAvailable"/> for asking first.
    /// </summary>
    /// <param name="settings">What it's told to be (see <see cref="Settings"/>), null for whatever it is by itself.</param>
    public static LanguageServer? Start(string name, string command, IReadOnlyList<string> arguments, string languageId, string rootDirectory, JsonObject? settings = null)
    {
        var info = new ProcessStartInfo(command)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Directory.Exists(rootDirectory) ? rootDirectory : Environment.CurrentDirectory
        };
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);

        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (Exception)
        {
            return null;
        }

        if (process is null)
            return null;

        // What it writes to its error stream is read and dropped, or a chatty server fills the pipe and stalls
        process.ErrorDataReceived += static (_, _) => { };
        process.BeginErrorReadLine();

        return new LanguageServer(name, languageId, rootDirectory, process, new JsonRpcConnection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream), settings);
    }

    /// <summary>A server over streams that are already open, for a test, or a server in the same process.</summary>
    public static LanguageServer Over(string name, string languageId, string rootDirectory, Stream input, Stream output, JsonObject? settings = null) =>
        new(name, languageId, rootDirectory, null, new JsonRpcConnection(input, output), settings);

    private async void Initialize()
    {
        var capabilities = new JsonObject
        {
            ["textDocument"] = new JsonObject
            {
                ["synchronization"] = new JsonObject { ["dynamicRegistration"] = false, ["willSave"] = false, ["willSaveWaitUntil"] = false, ["didSave"] = true },
                ["publishDiagnostics"] = new JsonObject { ["relatedInformation"] = false, ["versionSupport"] = false },

                // What is inserted is plain text, the editor has no placeholders to tab between
                ["completion"] = new JsonObject
                {
                    ["dynamicRegistration"] = false,
                    ["contextSupport"] = true,
                    ["completionItem"] = new JsonObject
                    {
                        ["snippetSupport"] = false,
                        ["insertReplaceSupport"] = false,
                        ["labelDetailsSupport"] = false,
                        ["documentationFormat"] = new JsonArray("markdown", "plaintext")
                    }
                }
            },
            // The files of the workspace that change on the disk are said (FilesChanged), a project added, a branch checked
            // out. A server asks for the ones it wants watched, and is told of every one, it takes what it wants of them
            ["workspace"] = new JsonObject
            {
                ["workspaceFolders"] = false,
                ["configuration"] = Settings is not null,
                ["didChangeWatchedFiles"] = new JsonObject { ["dynamicRegistration"] = true }
            }
        };

        string rootUri = UriOf(RootDirectory);
        var parameters = new JsonObject
        {
            ["processId"] = Environment.ProcessId,
            ["clientInfo"] = new JsonObject { ["name"] = "Horizon", ["version"] = "1.0" },
            ["rootPath"] = RootDirectory,
            ["rootUri"] = rootUri,
            ["capabilities"] = capabilities,
            ["workspaceFolders"] = new JsonArray(new JsonObject { ["uri"] = rootUri, ["name"] = Path.GetFileName(RootDirectory) })
        };

        try
        {
            await rpc.Request("initialize", parameters).ConfigureAwait(false);
            rpc.Notify("initialized", new JsonObject());

            // What it's told to be, for a server that has settings, said once it's up and asked for again as it wants
            if (Settings is not null)
                rpc.Notify("workspace/didChangeConfiguration", new JsonObject { ["settings"] = Settings.DeepClone() });
        }
        catch (Exception e)
        {
            lock (queue)
                waiting = null;
            Fail($"initialize failed, {e.Message}");
            return;
        }

        List<Action>? held;
        lock (queue)
        {
            IsReady = true;
            held = waiting;
            waiting = null;
        }

        foreach (Action action in held ?? [])
            Run(action);
    }

    // Helper method to do something now if the server is ready, or once it is, in the order things were asked
    private void Later(Action action)
    {
        lock (queue)
        {
            if (waiting is not null && !IsReady)
            {
                waiting.Add(action);
                return;
            }
        }

        Run(action);
    }

    private void Run(Action action)
    {
        if (Failure is not null)
            return;

        try
        {
            action();
        }
        catch (Exception e)
        {
            Fail(e.Message);
        }
    }

    // Helper method to say why it stopped, the first reason being the one kept, and answer what is still asked with nothing
    private void Fail(string why)
    {
        Failure ??= why;
        foreach (var answer in asking.Keys)
        {
            asking.TryRemove(answer, out _);
            answer.TrySetResult(null);
        }
    }

    // Helper method to ask the server something once it is ready, behind everything sent before. Null for an error, or
    // for a server that stopped before answering, nothing asked here throws
    private Task<JsonNode?> Ask(string method, JsonNode parameters)
    {
        var answer = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        asking[answer] = 0;

        // It stopped before this was put down, so nothing would answer it
        if (Failure is not null)
        {
            asking.TryRemove(answer, out _);
            answer.TrySetResult(null);
            return answer.Task;
        }

        Later(() => rpc.Request(method, parameters).ContinueWith(asked =>
        {
            asking.TryRemove(answer, out _);
            answer.TrySetResult(asked.IsCompletedSuccessfully ? asked.Result : null);
        }, TaskScheduler.Default));

        return answer.Task;
    }

    /// <summary>Tells the server a file is open, with its text as it is in the editor.</summary>
    public void Open(string path, string text)
    {
        string full = Path.GetFullPath(path);
        Later(() =>
        {
            lock (versions)
                versions[full] = 1;
            rpc.Notify("textDocument/didOpen", new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = UriOf(full), ["languageId"] = LanguageId, ["version"] = 1, ["text"] = text }
            });
        });
    }

    /// <summary>Sends the server the whole text of a file again, after it changed.</summary>
    public void Change(string path, string text)
    {
        string full = Path.GetFullPath(path);
        Later(() =>
        {
            int version;
            lock (versions)
            {
                if (!versions.TryGetValue(full, out version))
                    return;
                versions[full] = ++version;
            }

            rpc.Notify("textDocument/didChange", new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = UriOf(full), ["version"] = version },
                ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = text })
            });
        });
    }

    /// <summary>
    /// Tells the server what changed on the disk in its workspace that isn't open in it (a project added, files a
    /// checkout or a build wrote), which a server that has the project loaded goes by. Nothing for nothing.
    /// </summary>
    public void FilesChanged(IReadOnlyList<(string Path, LanguageServerFileChange Change)> changes)
    {
        if (changes.Count == 0)
            return;

        var list = new JsonArray();
        foreach (var (path, change) in changes)
            list.Add((JsonNode)new JsonObject { ["uri"] = UriOf(path), ["type"] = (int)change });

        Later(() => rpc.Notify("workspace/didChangeWatchedFiles", new JsonObject { ["changes"] = list }));
    }

    /// <summary>Tells the server a file was closed, it says nothing more about it.</summary>
    public void Close(string path)
    {
        string full = Path.GetFullPath(path);
        Later(() =>
        {
            lock (versions)
            {
                if (!versions.Remove(full))
                    return;
            }

            rpc.Notify("textDocument/didClose", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = UriOf(full) } });
        });
    }

    /// <summary>
    /// Asks what can be written at a place in a file (line and character from 0, the caret), with what is offered made
    /// to replace from <paramref name="start"/>, the start of the word being typed on that line. <paramref name="trigger"/>
    /// is the character that asked for it (a dot) and null for being asked (control and space), <paramref name="again"/>
    /// is for asking again after an answer that was <see cref="LanguageServerCompletions.Incomplete"/>. Null for nothing
    /// to offer, or a server that couldn't say. The file has to be up to date in the server first (<see cref="Change"/>).
    /// </summary>
    public async Task<LanguageServerCompletions?> Complete(string path, int line, int character, int start, char? trigger = null, bool again = false)
    {
        // 1 is asked for, 2 a character the server said it completes after, 3 asking again for what was incomplete
        var context = new JsonObject { ["triggerKind"] = again ? 3 : trigger is null ? 1 : 2 };
        if (trigger is { } typed && !again)
            context["triggerCharacter"] = typed.ToString();

        JsonNode? answered = await Ask("textDocument/completion", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = UriOf(path) },
            ["position"] = new JsonObject { ["line"] = line, ["character"] = character },
            ["context"] = context
        }).ConfigureAwait(false);

        // Either a list of what can go there, or the list and whether that's all of it
        bool incomplete = false;
        JsonArray? items = answered as JsonArray;
        if (answered is JsonObject list)
        {
            incomplete = list["isIncomplete"]?.GetValue<bool>() ?? false;
            items = list["items"] as JsonArray;
        }

        if (items is null)
            return null;

        var offered = new List<(string Sort, UICompletion Item)>(items.Count);
        foreach (JsonNode? node in items)
        {
            if (node is JsonObject item && CompletionOf(item, line, start) is { } completion)
                offered.Add((item["sortText"]?.GetValue<string>() ?? completion.Text, completion));
        }

        // In the order the server ranks them, which the editor keeps (a stable sort, ties stay as they came)
        return new LanguageServerCompletions([.. offered.OrderBy(pair => pair.Sort, StringComparer.Ordinal).Select(pair => pair.Item)], incomplete);
    }

    // Helper method to make one of the server's completion items into one of the editor's, null for one with no label
    private static UICompletion? CompletionOf(JsonObject item, int line, int start)
    {
        if (item["label"]?.GetValue<string>() is not { Length: > 0 } label)
            return null;

        // An edit the server worked out wins, as long as it replaces from where the editor will, otherwise what it says
        // to insert, otherwise the label
        string? insert = item["insertText"]?.GetValue<string>();
        if (item["textEdit"] is JsonObject edit && edit["newText"]?.GetValue<string>() is { } newText
            && (edit["range"] ?? edit["insert"]) is JsonObject range && range["start"] is JsonObject from
            && JsonNumbers.Int(from["line"], int.MinValue) == line && JsonNumbers.Int(from["character"], int.MinValue) == start)
            insert = newText;

        // A snippet anyway (some servers send them asked or not), its placeholders become what they stand for
        if (insert is not null && JsonNumbers.Int(item["insertTextFormat"], int.MinValue) == 2)
            insert = PlainSnippet(insert);

        string detail = item["detail"]?.GetValue<string>() is { Length: > 0 } said ? DetailWithout(said, label) : KindOf(JsonNumbers.Int(item["kind"], 0));
        if (detail.Length > 60)
            detail = string.Concat(detail.AsSpan(0, 59), "\u2026");

        return new UICompletion(label, detail, insert == label ? null : insert);
    }

    /// <summary>
    /// What a server says about an item without the item's own name in it, its name being right beside it in the list.
    /// serve-d says "int x" of x, and the list said x twice. All of it when that's all there is.
    /// </summary>
    public static string DetailWithout(string detail, string label)
    {
        string name = label.Split('(', ' ')[0];
        if (name.Length == 0)
            return detail;

        static bool Word(char character) => char.IsLetterOrDigit(character) || character == '_';
        for (int at = detail.IndexOf(name, StringComparison.Ordinal); at >= 0; at = detail.IndexOf(name, at + 1, StringComparison.Ordinal))
        {
            int after = at + name.Length;
            if ((at > 0 && Word(detail[at - 1])) || (after < detail.Length && Word(detail[after])))
                continue;

            string left = string.Join(' ', (detail[..at] + " " + detail[after..]).Split(' ', StringSplitOptions.RemoveEmptyEntries));
            return left.Length > 0 ? left : detail;
        }

        return detail;
    }

    /// <summary>A snippet's text without its placeholders, ${1:name} as name, $1 and $0 as nothing, \$ as $.</summary>
    public static string PlainSnippet(string snippet)
    {
        var plain = new System.Text.StringBuilder(snippet.Length);
        for (int i = 0; i < snippet.Length; i++)
        {
            char character = snippet[i];
            if (character == '\\' && i + 1 < snippet.Length)
            {
                plain.Append(snippet[++i]);
                continue;
            }

            if (character != '$' || i + 1 >= snippet.Length)
            {
                plain.Append(character);
                continue;
            }

            // $1, $0
            int at = i + 1;
            if (char.IsAsciiDigit(snippet[at]))
            {
                while (at < snippet.Length && char.IsAsciiDigit(snippet[at])) at++;
                i = at - 1;
                continue;
            }

            // ${1:name}, ${1|one,two|}, ${1}, as the name, the first choice, nothing
            if (snippet[at] == '{' && ClosingBrace(snippet, at) is var close and >= 0)
            {
                string inside = snippet[(at + 1)..close];
                int after = 0;
                while (after < inside.Length && char.IsAsciiDigit(inside[after])) after++;
                if (after < inside.Length && inside[after] == ':')
                    plain.Append(PlainSnippet(inside[(after + 1)..]));
                else if (after < inside.Length && inside[after] == '|')
                    plain.Append(inside[(after + 1)..].TrimEnd('|').Split(',')[0]);
                i = close;
                continue;
            }

            plain.Append(character);
        }

        return plain.ToString();
    }

    // Helper method to find the brace closing the one at an index, past the placeholders inside it, -1 for none
    private static int ClosingBrace(string snippet, int open)
    {
        int depth = 0;
        for (int i = open; i < snippet.Length; i++)
        {
            if (snippet[i] == '\\')
                i++;
            else if (snippet[i] == '{')
                depth++;
            else if (snippet[i] == '}' && --depth == 0)
                return i;
        }

        return -1;
    }

    // What sort of thing a completion item is, by the protocol's numbers, said when the server gives no detail
    private static string KindOf(int kind) => kind switch
    {
        1 => "text", 2 => "method", 3 => "function", 4 => "constructor", 5 => "field", 6 => "variable", 7 => "class",
        8 => "interface", 9 => "module", 10 => "property", 11 => "unit", 12 => "value", 13 => "enum", 14 => "keyword",
        15 => "snippet", 16 => "colour", 17 => "file", 18 => "reference", 19 => "folder", 20 => "enum member",
        21 => "constant", 22 => "struct", 23 => "event", 24 => "operator", 25 => "type parameter", _ => string.Empty
    };

    /// <summary>Takes what the server last said about a file, oldest first, false when there is nothing waiting.</summary>
    public bool TryTake(out string path, out IReadOnlyList<UICodeDiagnostic> found)
    {
        if (arrived.TryDequeue(out var next))
        {
            (path, found) = next;
            return true;
        }

        path = string.Empty;
        found = [];
        return false;
    }

    private void Published(JsonNode? parameters)
    {
        if (parameters is not JsonObject published || published["uri"]?.GetValue<string>() is not { } uri)
            return;

        string path;
        try
        {
            path = new Uri(uri).LocalPath;
        }
        catch (UriFormatException)
        {
            return;
        }

        var found = new List<UICodeDiagnostic>();
        if (published["diagnostics"] is JsonArray diagnostics)
        {
            foreach (JsonNode? node in diagnostics)
            {
                if (node is not JsonObject diagnostic || diagnostic["range"] is not JsonObject range
                    || range["start"] is not JsonObject start || range["end"] is not JsonObject end)
                    continue;

                int line = JsonNumbers.Int(start["line"], 0), column = JsonNumbers.Int(start["character"], 0);
                int endLine = JsonNumbers.Int(end["line"], line), endColumn = JsonNumbers.Int(end["character"], column);
                int length = endLine == line ? Math.Max(1, endColumn - column) : Math.Max(1, 200 - column);

                // 1 is an error, 2 a warning, 3 information and 4 a hint, the last two drawn quieter than a warning, nothing
                // is red that isn't wrong. A code goes after what it says when there is one, an empty one said "()"
                int severity = JsonNumbers.Int(diagnostic["severity"], 1);
                string message = diagnostic["message"]?.GetValue<string>() ?? string.Empty;
                if (diagnostic["code"] is { } code && code.GetValueKind() is System.Text.Json.JsonValueKind.String or System.Text.Json.JsonValueKind.Number
                    && code.ToString() is { Length: > 0 } said)
                    message = $"{message} ({said})";

                found.Add(new UICodeDiagnostic(line, column, length, message, Warning: severity != 1, Hint: severity >= 3));
            }
        }

        arrived.Enqueue((path, found));
        Woken?.Invoke();
    }

    private static string UriOf(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    /// <summary>Asks the server to shut down and ends its process, waiting a moment for it to go by itself.</summary>
    public void Dispose()
    {
        try
        {
            if (rpc is { IsClosed: false })
            {
                Task done = rpc.Request("shutdown", null);
                done.Wait(TimeSpan.FromSeconds(2));
                rpc.Notify("exit", null);
            }
        }
        catch (Exception)
        {
            // It is going either way
        }

        rpc.Dispose();
        if (process is not null)
        {
            try
            {
                if (!process.WaitForExit(2000))
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }

            process.Dispose();
        }
    }
}
