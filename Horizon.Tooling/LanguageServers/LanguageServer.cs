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
/// It is started with a root folder, told about a file as it is opened (<see cref="Open"/>), synchronized as
/// it changes (<see cref="Change"/>, using range updates when the server supports them) and told when it
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

    /// <summary>The language id of a file by its extension where it isn't <see cref="LanguageId"/>, null for none (see <see cref="LanguageServers.Registration"/>).</summary>
    public IReadOnlyDictionary<string, string>? LanguageIds { get; }

    /// <summary>What it was told as it was initialized, null for nothing.</summary>
    public JsonObject? InitializationOptions { get; }

    /// <summary>Whether it was told the editor asks for what's wrong rather than being told (see <see cref="LanguageServers.Registration"/>).</summary>
    public bool PullDiagnosticsWanted { get; }

    /// <summary>The language id a file is opened with, by its extension, <see cref="LanguageId"/> when nothing says otherwise.</summary>
    public string LanguageIdOf(string path)
    {
        if (LanguageIds is { Count: > 0 } ids)
        {
            string extension = Path.GetExtension(path);
            foreach (var (known, id) in ids)
                if (known.Equals(extension, StringComparison.OrdinalIgnoreCase))
                    return id;
        }

        return LanguageId;
    }

    /// <summary>The folder it was started in, the workspace as far as the server knows.</summary>
    public string RootDirectory { get; }

    private string? failure;
    private int disposed;

    /// <summary>Why it isn't running any more, null while it is or before it was started.</summary>
    public string? Failure => Volatile.Read(ref failure);

    private bool IsDisposed => Volatile.Read(ref disposed) != 0;

    /// <summary>The last 8192 characters of the server's error output, including startup failures.</summary>
    public string ErrorOutput => errorOutput?.Text ?? string.Empty;

    private readonly LanguageServerOutput? errorOutput;
    private readonly bool allowWorkspaceEdits;

    /// <summary>Whether its process is up and the connection open.</summary>
    public bool IsRunning => rpc is { IsClosed: false } && process is { HasExited: false };

    /// <summary>
    /// What it's told to be, its settings by their sections the way an editor's settings have them
    /// (<c>{ d: { lintOnFileOpen: "always" } }</c> for serve-d), said once it's up and answered whenever it asks for a
    /// section. Null for a server left to its own, which isn't told the editor has any.
    /// </summary>
    public JsonObject? Settings { get; private set; }

    /// <summary>
    /// What it's told to be changed while it runs: said to it as a change of configuration, and what it's answered from
    /// now on when it asks for a section. A server that was started with no settings said it can't be asked for them,
    /// it's still told.
    /// </summary>
    public void ChangeSettings(JsonObject? settings)
    {
        Settings = settings;
        Later(() => rpc.Notify("workspace/didChangeConfiguration", new JsonObject { ["settings"] = settings?.DeepClone() ?? new JsonObject() }));
    }

    /// <summary>Whether initialize has been answered, after which what was asked goes straight out.</summary>
    public bool IsReady => ready;
    private volatile bool ready;

    /// <summary>Called on the server's reading thread when diagnostics arrive, for the editor's thread to be woken.</summary>
    public Action? Woken { get; set; }

    private readonly Process? process;
    private readonly JsonRpcConnection rpc;
    // A shared sequence prevents a reopened document from reusing an earlier protocol version.
    private int nextDocumentVersion;
    private readonly Dictionary<string, int> versions = new(Horizon.Core.Platform.PlatformPaths.Comparer);
    private readonly ConcurrentQueue<(string Path, int? Version, IReadOnlyList<UICodeDiagnostic> Found)> arrived = new();
    private readonly Lock queue = new();

    // Held from a document's version and text being worked out to its notification being written, so two threads (the
    // editor's and the one that sends what waited for the server to start) can't send them the other way round: an
    // incremental change put on text it wasn't worked out from garbles the server's copy of the file
    private readonly Lock sending = new();
    private List<Action>? waiting = [];

    // What has been asked of the server and not answered, answered with nothing when it stops
    private readonly ConcurrentDictionary<TaskCompletionSource<JsonNode?>, byte> asking = new();

    private LanguageServer(string name, string languageId, string rootDirectory, Process? process, JsonRpcConnection rpc, JsonObject? settings = null, LanguageServerOutput? errorOutput = null,
        JsonObject? initializationOptions = null, IReadOnlyDictionary<string, string>? languageIds = null, bool pullDiagnostics = false, bool allowWorkspaceEdits = true)
    {
        Name = name;
        LanguageId = languageId;
        LanguageIds = languageIds;
        InitializationOptions = initializationOptions;
        PullDiagnosticsWanted = pullDiagnostics;
        this.allowWorkspaceEdits = allowWorkspaceEdits;
        RootDirectory = rootDirectory;
        Settings = settings;
        this.errorOutput = errorOutput;
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
    /// <param name="initializationOptions">What it's told as it's initialized, null for nothing.</param>
    /// <param name="languageIds">The language id of a file by its extension where it isn't <paramref name="languageId"/>.</param>
    public static LanguageServer? Start(string name, string command, IReadOnlyList<string> arguments, string languageId, string rootDirectory, JsonObject? settings = null,
        JsonObject? initializationOptions = null, IReadOnlyDictionary<string, string>? languageIds = null, bool pullDiagnostics = false, bool allowWorkspaceEdits = true)
    {
        Process? process;
        try
        {
            var info = LanguageServerProgram.StartInfo(command, arguments);
            // csharp-ls runs the analyzers referenced by MSBuild. Include the SDK's IDE code-style analyzers in its
            // design-time projects as well as the regular .NET analyzers, without changing the project's build settings.
            if (languageId == "csharp" && settings?["csharp"]?["analyzersEnabled"] is JsonValue analyzers
                && analyzers.TryGetValue<bool>(out bool enabled) && enabled)
                info.Environment.TryAdd("EnforceCodeStyleInBuild", "true");
            info.WorkingDirectory = Directory.Exists(rootDirectory) ? rootDirectory : Environment.CurrentDirectory;
            process = Process.Start(info);

            // Gone with this program however it goes, serve-d and its dcd-server outlived every Hex that was killed
            if (process is not null)
                Horizon.Core.Platform.ChildProcesses.Adopt(process);
        }
        catch (Exception)
        {
            return null;
        }

        if (process is null)
            return null;

        var errors = new LanguageServerOutput();
        _ = errors.Read(process.StandardError);

        return new LanguageServer(name, languageId, rootDirectory, process, new JsonRpcConnection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, name), settings, errors,
            initializationOptions, languageIds, pullDiagnostics, allowWorkspaceEdits);
    }

    /// <summary>A server over streams that are already open, for a test, or a server in the same process.</summary>
    public static LanguageServer Over(string name, string languageId, string rootDirectory, Stream input, Stream output, JsonObject? settings = null,
        JsonObject? initializationOptions = null, IReadOnlyDictionary<string, string>? languageIds = null, bool pullDiagnostics = false, bool allowWorkspaceEdits = true) =>
        new(name, languageId, rootDirectory, null, new JsonRpcConnection(input, output), settings, null, initializationOptions, languageIds, pullDiagnostics, allowWorkspaceEdits);

    private async void Initialize()
    {
        var capabilities = new JsonObject
        {
            ["textDocument"] = new JsonObject
            {
                ["synchronization"] = new JsonObject { ["dynamicRegistration"] = false, ["willSave"] = false, ["willSaveWaitUntil"] = false, ["didSave"] = true },
                ["publishDiagnostics"] = new JsonObject { ["relatedInformation"] = true, ["codeDescriptionSupport"] = true, ["versionSupport"] = true, ["tagSupport"] = new JsonObject { ["valueSet"] = new JsonArray(1, 2) } },

                // What is inserted is plain text, the editor has no placeholders to tab between
                ["completion"] = new JsonObject
                {
                    ["dynamicRegistration"] = false,
                    ["contextSupport"] = true,
                    ["completionItem"] = new JsonObject
                    {
                        ["snippetSupport"] = false,
                        ["insertReplaceSupport"] = true,
                        ["labelDetailsSupport"] = false,
                        ["resolveSupport"] = new JsonObject { ["properties"] = new JsonArray("detail", "documentation") },
                        ["documentationFormat"] = new JsonArray("markdown", "plaintext")
                    }
                },

                // Asking about the code, and asking for it changed (LanguageServer.Navigation and Edits)
                ["hover"] = new JsonObject { ["dynamicRegistration"] = false, ["contentFormat"] = new JsonArray("markdown", "plaintext") },
                ["signatureHelp"] = new JsonObject
                {
                    ["dynamicRegistration"] = false,
                    ["signatureInformation"] = new JsonObject
                    {
                        ["documentationFormat"] = new JsonArray("markdown", "plaintext"),
                        ["parameterInformation"] = new JsonObject { ["labelOffsetSupport"] = true },
                        ["activeParameterSupport"] = true
                    }
                },
                ["definition"] = new JsonObject { ["dynamicRegistration"] = false, ["linkSupport"] = true },
                ["typeDefinition"] = new JsonObject { ["dynamicRegistration"] = false, ["linkSupport"] = true },
                ["selectionRange"] = new JsonObject { ["dynamicRegistration"] = false },
                ["foldingRange"] = new JsonObject { ["dynamicRegistration"] = false, ["lineFoldingOnly"] = true },
                ["references"] = new JsonObject { ["dynamicRegistration"] = false },
                ["implementation"] = new JsonObject { ["dynamicRegistration"] = false, ["linkSupport"] = true },
                ["callHierarchy"] = new JsonObject { ["dynamicRegistration"] = false },
                ["typeHierarchy"] = new JsonObject { ["dynamicRegistration"] = false },
                ["codeLens"] = new JsonObject { ["dynamicRegistration"] = false },
                ["inlayHint"] = new JsonObject { ["dynamicRegistration"] = false },
                ["semanticTokens"] = SemanticCapabilities(),
                ["documentSymbol"] = new JsonObject { ["dynamicRegistration"] = false, ["hierarchicalDocumentSymbolSupport"] = true },
                ["documentHighlight"] = new JsonObject { ["dynamicRegistration"] = false },
                ["rename"] = new JsonObject { ["dynamicRegistration"] = false, ["prepareSupport"] = true, ["prepareSupportDefaultBehavior"] = 1 },
                ["formatting"] = new JsonObject { ["dynamicRegistration"] = false },
                ["rangeFormatting"] = new JsonObject { ["dynamicRegistration"] = false },
                ["onTypeFormatting"] = new JsonObject { ["dynamicRegistration"] = false },
                ["codeAction"] = new JsonObject
                {
                    ["dynamicRegistration"] = false,
                    ["dataSupport"] = true,
                    ["resolveSupport"] = new JsonObject { ["properties"] = new JsonArray("edit") },
                    ["isPreferredSupport"] = true,
                    ["disabledSupport"] = true,
                    ["codeActionLiteralSupport"] = new JsonObject
                    {
                        ["codeActionKind"] = new JsonObject { ["valueSet"] = new JsonArray("", "quickfix", "refactor", "refactor.extract", "refactor.inline", "refactor.rewrite", "source", "source.organizeImports") }
                    }
                }
            },
            // csharp-ls says where a name of the framework is declared (a csharp:/ place, its source decompiled) only to a
            // client that says it can open one (LanguageServer.Navigation's Source)
            ["experimental"] = new JsonObject { ["csharp"] = new JsonObject { ["metadataUris"] = true } },

            // The files of the workspace that change on the disk are said (FilesChanged), a project added, a branch checked
            // out. A server asks for the ones it wants watched and is told of those alone, one that never asks of all of them
            ["workspace"] = new JsonObject
            {
                ["workspaceFolders"] = false,
                ["configuration"] = Settings is not null,
                ["applyEdit"] = allowWorkspaceEdits,
                ["workspaceEdit"] = new JsonObject { ["documentChanges"] = true },
                ["didChangeWatchedFiles"] = new JsonObject { ["dynamicRegistration"] = true },
                ["semanticTokens"] = new JsonObject { ["refreshSupport"] = true },
                ["codeLens"] = new JsonObject { ["refreshSupport"] = true },
                ["inlayHint"] = new JsonObject { ["refreshSupport"] = true }
            }
        };

        // Asking what's wrong, said only to a server that has to be asked (LanguageServer.PullDiagnostics)
        if (PullDiagnosticsWanted)
        {
            ((JsonObject)capabilities["textDocument"]!)["diagnostic"] = new JsonObject { ["dynamicRegistration"] = false, ["relatedDocumentSupport"] = false };
            ((JsonObject)capabilities["workspace"]!)["diagnostics"] = new JsonObject { ["refreshSupport"] = true };
        }

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
        if (InitializationOptions is not null)
            parameters["initializationOptions"] = InitializationOptions.DeepClone();

        try
        {
            JsonNode? started = await rpc.Request("initialize", parameters).ConfigureAwait(false);
            serverCapabilities = started?["capabilities"] as JsonObject;
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

        // What waited goes out first, in order, and only then is it ready: set ready before, a didChange from the
        // editor's thread went straight out ahead of the didOpen still waiting here and the server never had the file
        // the way the editor did. What's asked while these go out waits behind them
        while (true)
        {
            List<Action> held;
            lock (queue)
            {
                if (waiting is null or { Count: 0 })
                {
                    waiting = null;
                    ready = true;
                    break;
                }
                held = waiting;
                waiting = [];
            }

            foreach (Action action in held)
                Run(action);
        }
        Woken?.Invoke();
    }

    // Helper method to do something now if the server is ready, or once it is, in the order things were asked
    private void Later(Action action)
    {
        lock (queue)
        {
            if (waiting is not null)
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
        Interlocked.CompareExchange(ref failure, why, null);
        DisposePullTimers();
        Woken?.Invoke();
        foreach (var answer in asking.Keys)
        {
            asking.TryRemove(answer, out _);
            answer.TrySetResult(null);
        }
    }

    // Helper method to ask the server something once it is ready, behind everything sent before. Null for an error, or
    // for a server that stopped before answering, nothing asked here throws
    private Task<JsonNode?> Ask(string method, JsonNode parameters, bool propagateErrors = false, bool urgent = false)
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

        void Send() => rpc.Request(method, parameters).ContinueWith(asked =>
        {
            asking.TryRemove(answer, out _);
            if (propagateErrors && !asked.IsCompletedSuccessfully)
                answer.TrySetException(asked.Exception?.GetBaseException() ?? new IOException("The language-server request was cancelled."));
            else
                answer.TrySetResult(asked.IsCompletedSuccessfully ? asked.Result : null);
        }, TaskScheduler.Default);

        if (!urgent && BACKGROUND_METHODS.Contains(method) && HoldBackground(Send))
            return answer.Task;

        Later(Send);
        return answer.Task;
    }

    /* Keeping typing quick */

    // What the editor asks for by itself after every change, the colours, the hints, the lenses, the folds. csharp-ls
    // answers in turn, so a completion asked while typing waited behind all of them, 0.3 to 2 seconds a key on the
    // engine's solution against 0.07 with nothing in front of it. They wait until typing stops instead.
    private static readonly HashSet<string> BACKGROUND_METHODS = new(StringComparer.Ordinal)
    {
        "textDocument/semanticTokens/full", "textDocument/semanticTokens/range", "textDocument/inlayHint",
        "textDocument/codeLens", "textDocument/foldingRange", "textDocument/documentSymbol", "textDocument/documentHighlight",
        // What's wrong, for a server that's asked (LanguageServer.PullDiagnostics), a whole file's worth of work
        "textDocument/diagnostic"
    };

    // How long after the last change or completion the held requests go out, and the longest they're held for
    private const long QUIET_MS = 600, LONGEST_HOLD_MS = 4000;

    private readonly Lock holding = new();
    private readonly List<Action> held = [];
    private Timer? releaseTimer;
    private long busyAt, heldSince;
    private int completing;

    // Whether the user is typing or waiting on a completion, the moment of the last of either
    private bool Busy => Volatile.Read(ref completing) > 0 || Environment.TickCount64 - Interlocked.Read(ref busyAt) < QUIET_MS;

    // Helper method to say the user did something that wants answering quickly
    private void Typing() => Interlocked.Exchange(ref busyAt, Environment.TickCount64);

    // Helper method to keep a background request back while typing goes on, false to send it now
    private bool HoldBackground(Action send)
    {
        lock (holding)
        {
            if (IsDisposed || Failure is not null)
                return false;
            if (held.Count == 0 && !Busy)
                return false;

            if (held.Count == 0)
                heldSince = Environment.TickCount64;
            held.Add(send);
            releaseTimer ??= new Timer(_ => ReleaseHeld());
            releaseTimer.Change(100, 100);
            return true;
        }
    }

    // On the timer, what was held goes out in the order it was asked once typing has stopped (or it waited long enough)
    private void ReleaseHeld()
    {
        Action[] going;
        lock (holding)
        {
            if (held.Count == 0 || (Busy && Environment.TickCount64 - heldSince < LONGEST_HOLD_MS && Failure is null))
                return;

            going = [.. held];
            held.Clear();
            releaseTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        foreach (Action send in going)
            Later(send);
    }

    /// <summary>Tells the server a file is open, with its text as it is in the editor.</summary>
    public void Open(string path, string text)
    {
        string full = Path.GetFullPath(path);
        Later(() =>
        {
            lock (sending)
            {
                int version;
                lock (versions)
                {
                    versions[full] = version = ++nextDocumentVersion;
                    if (IncrementalChanges) synchronizedText[full] = text;
                }
                rpc.Notify("textDocument/didOpen", new JsonObject
                {
                    ["textDocument"] = new JsonObject { ["uri"] = UriOf(full), ["languageId"] = LanguageIdOf(full), ["version"] = version, ["text"] = text }
                });
            }
            PullSoon(full);
        });
    }

    /// <summary>Synchronizes a changed file, sending a range update when the server supports incremental changes.</summary>
    public void Change(string path, string text)
    {
        string full = Path.GetFullPath(path);
        Typing();
        Later(() =>
        {
            lock (sending)
            {
                int version;
                JsonObject change;
                lock (versions)
                {
                    if (!versions.ContainsKey(full))
                        return;
                    versions[full] = version = ++nextDocumentVersion;
                    change = ContentChange(full, text);
                }

                rpc.Notify("textDocument/didChange", new JsonObject
                {
                    ["textDocument"] = new JsonObject { ["uri"] = UriOf(full), ["version"] = version },
                    ["contentChanges"] = new JsonArray(change)
                });
            }
            PullSoon(full);
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

        Later(() =>
        {
            // A file open in the server is the client's, its text goes by didChange alone. csharp-ls reads one that was
            // created or changed on the disk again (Handlers/Workspace.fs, tryReloadDocumentOnUri) whether it's open or
            // not, and the incremental didChange for the same write that came next was put on top of the new text: an
            // edit made twice ("sealed sealed class"), a line taken out twice ("arentOf"), errors at lines that are fine
            var list = new JsonArray();
            lock (sending)
            {
                foreach (var (path, change) in changes)
                {
                    if (change != LanguageServerFileChange.Deleted && DocumentVersion(path) is not null)
                        continue;

                    // Only what it asked for, when it asked (LanguageServer.Watchers)
                    if (!Watches(path, change))
                        continue;
                    list.Add((JsonNode)new JsonObject { ["uri"] = UriOf(path), ["type"] = (int)change });
                }

                if (list.Count > 0)
                    rpc.Notify("workspace/didChangeWatchedFiles", new JsonObject { ["changes"] = list });
            }
        });
    }

    /// <summary>Tells the server a file was closed, it says nothing more about it.</summary>
    public void Close(string path)
    {
        string full = Path.GetFullPath(path);
        Later(() =>
        {
            lock (sending)
            {
                lock (versions)
                {
                    if (!versions.Remove(full))
                        return;
                    synchronizedText.Remove(full);
                }

                rpc.Notify("textDocument/didClose", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = UriOf(full) } });
            }
            ForgetPulls(full);
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

        // What the editor asks for by itself waits while this is asked (HoldBackground)
        Typing();
        Interlocked.Increment(ref completing);
        JsonNode? answered;
        try
        {
            answered = await Ask("textDocument/completion", new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = UriOf(path) },
                ["position"] = new JsonObject { ["line"] = line, ["character"] = character },
                ["context"] = context
            }).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref completing);
            Typing();
        }

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
        bool resolves = serverCapabilities?["completionProvider"] is JsonObject provider && provider["resolveProvider"]?.GetValue<bool>() == true;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] is JsonObject item && CompletionOf(item, line, character, start) is { } completion)
            {
                if (resolves)
                {
                    // Detach the server's item so Lazy resolve can send it once without DeepClone.
                    items[i] = null;
                    var resolved = new Lazy<Task<UICompletionDocumentation?>>(() => ResolveCompletionDocumentation(item));
                    completion = completion with { ResolveDocumentation = () => resolved.Value };
                }
                offered.Add((item["sortText"]?.GetValue<string>() ?? completion.Text, completion));
            }
        }

        // In the order the server ranks them, which the editor keeps (a stable sort, ties stay as they came)
        return new LanguageServerCompletions([.. offered.OrderBy(pair => pair.Sort, StringComparer.Ordinal).Select(pair => pair.Item)], incomplete);
    }

    private async Task<UICompletionDocumentation?> ResolveCompletionDocumentation(JsonObject item)
    {
        if (await Ask("completionItem/resolve", item).ConfigureAwait(false) is not JsonObject resolved)
            return null;
        string label = item["label"]?.GetValue<string>() ?? "";
        string detail = resolved["detail"]?.GetValue<string>() is { Length: > 0 } said
            ? DetailWithout(said.Split('\n')[0].Trim(), label) : "";
        if (detail.Length > 60) detail = string.Concat(detail.AsSpan(0, 59), "\u2026");
        return new(detail, PlainDocs(Markdown(resolved["documentation"])));
    }

    // Helper method to make one of the server's completion items into one of the editor's, null for one with no label
    private static UICompletion? CompletionOf(JsonObject item, int line, int character, int start)
    {
        if (item["label"]?.GetValue<string>() is not { Length: > 0 } label)
            return null;

        // A snippet a server sends whether it was asked for snippets or not, which it sorts with a space after its name.
        // Roslyn's for and forr after shelf. came out as properties, and picked they were text that broke the line
        if (item["sortText"]?.GetValue<string>() is { Length: > 0 } sort && sort.EndsWith(' '))
            return null;

        // The native range can cover punctuation within a name or path, beyond the editor's word boundary.
        string? insert = item["insertText"]?.GetValue<string>();
        UITextPosition? replacementStart = null, replacementEnd = null;
        if (item["textEdit"] is { } nativeEdit)
        {
            if (nativeEdit is not JsonObject edit || edit["newText"]?.GetValue<string>() is not { } newText
                || (edit["range"] ?? edit["replace"] ?? edit["insert"]) is not JsonObject range
                || range["start"] is not JsonObject from || range["end"] is not JsonObject to
                || JsonNumbers.Int(from["line"], -1) != line || JsonNumbers.Int(to["line"], -1) != line)
                return null;
            int first = JsonNumbers.Int(from["character"], -1), last = JsonNumbers.Int(to["character"], -1);
            if (first < 0 || first > character || last < character) return null;
            insert = newText;
            // Ordinary prefix replacements remain reusable as the word grows. Other ranges belong to this version.
            if (first != start) replacementStart = new(line, first);
            if (replacementStart is not null || last > character) replacementEnd = new(line, last);
        }

        // A snippet anyway (some servers send them asked or not), its placeholders become what they stand for
        if (insert is not null && JsonNumbers.Int(item["insertTextFormat"], int.MinValue) == 2)
            insert = PlainSnippet(insert);

        // A detail of one line, serve-d says every overload of a name on a line of its own and they were drawn over each other
        string detail = item["detail"]?.GetValue<string>() is { Length: > 0 } said ? DetailWithout(said.Split('\n')[0].Trim(), label) : KindOf(JsonNumbers.Int(item["kind"], 0));
        if (detail.Length > 60)
            detail = string.Concat(detail.AsSpan(0, 59), "\u2026");

        // serve-d's label says how many overloads in markdown's italics, "(*2 overloads*)", which read with the stars
        string shown = label.Replace("(*", "(", StringComparison.Ordinal).Replace("*)", ")", StringComparison.Ordinal);
        List<UITextEdit>? additional = null;
        if (item["additionalTextEdits"] is JsonArray extras)
        {
            additional = [];
            foreach (JsonNode? extra in extras)
            {
                if (extra is not JsonObject replacement || replacement["range"] is not JsonObject span
                    || span["start"] is not JsonObject first || span["end"] is not JsonObject last
                    || replacement["newText"]?.GetValue<string>() is not { } text)
                    return null;
                var extraStart = new UITextPosition(JsonNumbers.Int(first["line"], -1), JsonNumbers.Int(first["character"], -1));
                var to = new UITextPosition(JsonNumbers.Int(last["line"], -1), JsonNumbers.Int(last["character"], -1));
                if (extraStart.Line < 0 || extraStart.Column < 0 || to.Line < 0 || to.Column < 0 || to < extraStart)
                    return null;
                additional.Add(new(extraStart, to, text));
            }
        }
        return new UICompletion(shown, detail, insert == shown ? null : insert ?? (shown == label ? null : label), PlainDocs(Markdown(item["documentation"])), KindOf(JsonNumbers.Int(item["kind"], 0)), additional, ReplacementEnd: replacementEnd, ReplacementStart: replacementStart);
    }

    // How much of what an item does goes beside the list
    private const int MOST_DOCS = 600;

    /// <summary>
    /// What a server's markdown documentation of something says as plain text for a box beside a list, its prose up to
    /// the first example, the marks of the markdown taken out (the code, the bold, a link's target) and the blank lines
    /// between paragraphs kept as one.
    /// </summary>
    public static string PlainDocs(string markdown)
    {
        if (markdown.Length == 0)
            return string.Empty;

        // Up to the first example, its heading with it, or the first block of code
        int cut = markdown.Length;
        foreach (string example in (string[])["```", "**Example"])
        {
            if (markdown.IndexOf(example, StringComparison.Ordinal) is var at and >= 0)
                cut = Math.Min(cut, at);
        }

        string prose = markdown[..cut];
        // A picture says nothing in text, VS Code's CSS docs started a line with "!Baseline icon"
        prose = Pictures().Replace(prose, string.Empty);
        prose = Links().Replace(prose, "$1");
        prose = prose.Replace("**", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal);
        // _Emphasis_ the way the bold is, and a mark written escaped as itself, "elements\." was said with its backslash
        prose = Emphasis().Replace(prose, "$1");
        prose = EscapedMarks().Replace(prose, "$1");
        // And what's written as an HTML entity as its character, CSS's syntax said "&lt;color&gt;"
        prose = System.Net.WebUtility.HtmlDecode(prose);

        // A dash the fonts have, serve-d's long one came out as a question mark
        prose = prose.Replace('—', '-').Replace('–', '-');

        var paragraphs = BlankLines().Split(prose.ReplaceLineEndings("\n"))
            .Select(static paragraph => WhiteSpace().Replace(paragraph, " ").Trim())
            .Where(paragraph => paragraph.Length > 0);
        string plain = string.Join("\n", paragraphs);
        return plain.Length > MOST_DOCS ? string.Concat(plain.AsSpan(0, MOST_DOCS - 3), "...") : plain;
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

            // A name said with what it's a member of goes with it, csharp-ls's "Voice Mixer.Add(float ...)" was
            // "Voice Mixer. (float ...)" and TypeScript's "(property) Track.title: string" was "Track. : string"
            int from = at;
            while (from > 0 && detail[from - 1] == '.')
            {
                from--;
                while (from > 0 && (Word(detail[from - 1]) || detail[from - 1] is '<' or '>' or ','))
                    from--;
            }

            string left = string.Join(' ', (detail[..from] + " " + detail[after..]).Split(' ', StringSplitOptions.RemoveEmptyEntries));
            // What kind it is in brackets in front, TypeScript's "(property)", is the icon's to say, and what's left of
            // "x: string" is the type
            left = KindInBrackets().Replace(left, string.Empty);
            left = left.Replace(" :", ":", StringComparison.Ordinal);
            if (left.StartsWith(':'))
                left = left[1..].TrimStart();
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
        1 => "text",
        2 => "method",
        3 => "function",
        4 => "constructor",
        5 => "field",
        6 => "variable",
        7 => "class",
        8 => "interface",
        9 => "module",
        10 => "property",
        11 => "unit",
        12 => "value",
        13 => "enum",
        14 => "keyword",
        15 => "snippet",
        16 => "colour",
        17 => "file",
        18 => "reference",
        19 => "folder",
        20 => "enum member",
        21 => "constant",
        22 => "struct",
        23 => "event",
        24 => "operator",
        25 => "type parameter",
        _ => string.Empty
    };

    /// <summary>Takes what the server last said about a file, oldest first, false when there is nothing waiting.</summary>
    public bool TryTake(out string path, out IReadOnlyList<UICodeDiagnostic> found)
    {
        while (arrived.TryDequeue(out var next))
        {
            if (next.Version is { } version && DocumentVersion(next.Path) is { } current && version < current) continue;
            (path, found) = (next.Path, next.Found);
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

        if (PathOf(uri) is not { } path)
            return;

        int? version = published["version"] is JsonValue value && value.TryGetValue<int>(out int number) ? number : null;
        if (version is { } sent && DocumentVersion(path) is { } current && sent < current) return;

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
                // Roslyn also sends empty marker diagnostics for code fixes. Keep their raw data for Actions,
                // but they have nothing to underline or list in Problems.
                if (string.IsNullOrWhiteSpace(message)) continue;
                if (diagnostic["code"] is { } code && code.GetValueKind() is System.Text.Json.JsonValueKind.String or System.Text.Json.JsonValueKind.Number
                    && code.ToString() is { Length: > 0 } said)
                    message = $"{message} ({said})";

                // Tagged 2 is about something obsolete, which is struck through as well. csharp-ls doesn't tag them, so C#'s
                // own codes for using something [Obsolete] count too
                bool deprecated = diagnostic["tags"] is JsonArray tags && tags.Any(tag => JsonNumbers.Int(tag, 0) == 2)
                    || diagnostic["code"]?.ToString() is "CS0612" or "CS0618" or "CS0619";
                found.Add(new UICodeDiagnostic(line, column, length, message, Warning: severity != 1, Hint: severity >= 3, Deprecated: deprecated)
                {
                    RelatedLocations = DiagnosticLocations(diagnostic["relatedInformation"]),
                    DocumentationUrl = diagnostic["codeDescription"]?["href"] is JsonValue href && href.TryGetValue<string>(out var link)
                        && Uri.TryCreate(link, UriKind.Absolute, out var documentation) && documentation.Scheme is "https" or "http" ? link : null
                });
            }
        }

        published.Remove("diagnostics", out JsonNode? keptDiagnostics);
        KeepDiagnostics(path, keptDiagnostics as JsonArray);
        arrived.Enqueue((path, version, found));
        Woken?.Invoke();
    }

    /// <summary>Asks the server to shut down and ends its process, waiting a moment for it to go by itself.</summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref disposed, 1);
        DisposePullTimers();

        lock (holding)
        {
            releaseTimer?.Dispose();
            releaseTimer = null;
            held.Clear();
        }

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

    // What the docs and details of completions are made plain with, made when Hex is built rather than read from their
    // pattern at the first completion
    [System.Text.RegularExpressions.GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial System.Text.RegularExpressions.Regex Pictures();

    [System.Text.RegularExpressions.GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial System.Text.RegularExpressions.Regex Links();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<![\w\\])_(\S(?:[^_]*\S)?)_(?!\w)")]
    private static partial System.Text.RegularExpressions.Regex Emphasis();

    [System.Text.RegularExpressions.GeneratedRegex(@"\\([\\`*_{}\[\]()#+\-.!|~<>])")]
    private static partial System.Text.RegularExpressions.Regex EscapedMarks();

    [System.Text.RegularExpressions.GeneratedRegex(@"\n\s*\n")]
    private static partial System.Text.RegularExpressions.Regex BlankLines();

    [System.Text.RegularExpressions.GeneratedRegex(@"\s+")]
    private static partial System.Text.RegularExpressions.Regex WhiteSpace();

    [System.Text.RegularExpressions.GeneratedRegex(@"^\((?:[a-z]+ ?)+\)\s*")]
    private static partial System.Text.RegularExpressions.Regex KindInBrackets();
}
