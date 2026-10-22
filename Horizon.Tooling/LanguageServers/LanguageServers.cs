namespace Horizon.UI.Scripting;

/// <summary>
/// The language servers an editor knows of, by the files they lint, and the ones that are running. A tool registers
/// what it has a configuration for (<see cref="Register"/>, csharp-ls for .cs, pyright for .py), asks for the one a
/// file wants (<see cref="For"/>) and gets it started on the first file of its kind, or null when the command isn't on
/// this machine, which a tool takes quietly, a file nobody can lint is still a file. One server runs per registration
/// and root folder (a server knows the projects of the folder it was started in and nothing else, so a file of another
/// folder gets a server of its own there), and <see cref="StopAll"/> ends them.
/// </summary>
public static class LanguageServers
{
    /// <summary>A server an editor can run, as a configuration names it.</summary>
    /// <param name="Name">What it is called, a status bar says it.</param>
    /// <param name="Command">The program, on the PATH or a full path.</param>
    /// <param name="Arguments">What the program is started with, --stdio for the ones that want asking.</param>
    /// <param name="LanguageId">The language id of the protocol, csharp, python.</param>
    /// <param name="Extensions">The files it lints, ".cs" and the like.</param>
    /// <param name="Settings">
    /// What it's told to be, see <see cref="LanguageServer.Settings"/>, null for nothing. A text in it that says {root}
    /// is the folder the server is started in, for a server that wants its import folders said (serve-d's
    /// d.projectImportPaths, a lone file's folder it knew nothing of).
    /// </param>
    public sealed record Registration(string Name, string Command, string[] Arguments, string LanguageId, string[] Extensions, System.Text.Json.Nodes.JsonObject? Settings = null);

    private static readonly Lock gate = new();
    private static readonly List<Registration> registrations = [];
    // By registration and root folder, see Key. Null for one that couldn't be started, which isn't tried again
    private static readonly Dictionary<string, LanguageServer?> running = new(StringComparer.Ordinal);

    // What a server is known by, its registration's name and the folder it was started in
    private static string Key(string name, string root) =>
        name + "\n" + Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    // The ones that were started, kept as an array because an editor asks every tick and a list a tick is garbage
    private static LanguageServer[] started = [];

    /// <summary>Everything registered, in the order it was.</summary>
    public static IReadOnlyList<Registration> Registered
    {
        get
        {
            lock (gate)
                return [.. registrations];
        }
    }

    /// <summary>
    /// Says that files with these extensions are linted by this command. A name registered before is replaced, and its
    /// server, if it was running, stopped off the caller's thread: what's handed back is that stop, done once the
    /// process is gone (for whoever wants to write over its program).
    /// </summary>
    public static Task Register(string name, string command, string[] arguments, string languageId, string[] extensions, System.Text.Json.Nodes.JsonObject? settings = null)
    {
        Task stopping = Task.CompletedTask;
        lock (gate)
        {
            registrations.RemoveAll(other => other.Name == name);
            registrations.Add(new Registration(name, command, arguments, languageId, extensions, settings));
            // Every one of its folders, asked to shut down and given a couple of seconds for it, off the caller's thread,
            // which is the UI's
            var going = new List<LanguageServer>();
            foreach (string key in running.Keys.Where(key => key.StartsWith(name + "\n", StringComparison.Ordinal)).ToList())
            {
                if (running.Remove(key, out LanguageServer? was) && was is not null)
                    going.Add(was);
            }

            if (going.Count > 0)
            {
                stopping = Task.WhenAll(going.Select(was => Task.Run(was.Dispose)));
                started = [.. running.Values.OfType<LanguageServer>()];
            }
        }

        return stopping;
    }

    /// <summary>The registration that lints a file, by its extension, null for none.</summary>
    public static Registration? RegistrationFor(string path)
    {
        string extension = Path.GetExtension(path);
        if (extension.Length == 0)
            return null;

        lock (gate)
            return registrations.Find(entry => Array.Exists(entry.Extensions, known => known.Equals(extension, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// The server for a file in a root folder, started there if it isn't running yet. Null when nothing is registered for
    /// the file, the command isn't on this machine, or the server couldn't be started there (which isn't tried again).
    /// </summary>
    public static LanguageServer? For(string path, string rootDirectory)
    {
        if (RegistrationFor(path) is not { } entry)
            return null;

        // A program started from the Dock finds what a terminal would once the login shell's PATH is in
        Horizon.Core.Processes.ProcessEnvironment.WaitForLoginShell();

        lock (gate)
        {
            string key = Key(entry.Name, rootDirectory);
            if (running.TryGetValue(key, out LanguageServer? known))
                return known is { Failure: null } ? known : null;

            LanguageServer? server = IsAvailable(entry.Command) ? LanguageServer.Start(entry.Name, entry.Command, entry.Arguments, entry.LanguageId, rootDirectory, SettingsIn(entry.Settings, rootDirectory)) : null;
            running[key] = server;
            if (server is not null)
                started = [.. running.Values.OfType<LanguageServer>()];
            return server;
        }
    }

    /// <summary>A registration's settings for a server started in a folder, every {root} in them that folder.</summary>
    internal static System.Text.Json.Nodes.JsonObject? SettingsIn(System.Text.Json.Nodes.JsonObject? settings, string rootDirectory)
    {
        if (settings is null || !settings.ToJsonString().Contains("{root}", StringComparison.Ordinal))
            return settings;

        string root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        System.Text.Json.Nodes.JsonNode? Filled(System.Text.Json.Nodes.JsonNode? node) => node switch
        {
            System.Text.Json.Nodes.JsonObject inside => new System.Text.Json.Nodes.JsonObject(inside.Select(pair => KeyValuePair.Create(pair.Key, Filled(pair.Value)))),
            System.Text.Json.Nodes.JsonArray list => new System.Text.Json.Nodes.JsonArray([.. list.Select(Filled)]),
            System.Text.Json.Nodes.JsonValue value when value.TryGetValue(out string? text) => System.Text.Json.Nodes.JsonValue.Create(text.Contains("{root}", StringComparison.Ordinal) ? Path.GetFullPath(text.Replace("{root}", root, StringComparison.Ordinal)) : text),
            _ => node?.DeepClone()
        };

        return (System.Text.Json.Nodes.JsonObject?)Filled(settings);
    }

    /// <summary>The servers that were started, running or not. The same array until one starts or stops, nothing to iterate it costs.</summary>
    public static IReadOnlyList<LanguageServer> Running => started;

    /// <summary>Whether a command can be started, a file that is there or a program on the PATH (with PATHEXT's endings on Windows).</summary>
    public static bool IsAvailable(string command)
    {
        if (command.Length == 0)
            return false;

        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
            return File.Exists(command);

        string[] endings = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [];

        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(folder.Trim('"'), command);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (File.Exists(candidate))
                return true;

            foreach (string ending in endings)
            {
                if (File.Exists(candidate + ending))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Shuts down the servers started in a folder or anywhere in it, off the caller's thread, done when their processes
    /// are. A process's folder can't be deleted on Windows while it runs, so before a folder goes, they go.
    /// </summary>
    public static Task StopUnder(string folder)
    {
        var going = new List<LanguageServer>();
        lock (gate)
        {
            foreach (var (key, server) in running.ToList())
            {
                if (server is not null && Horizon.Core.Platform.PlatformPaths.IsUnder(server.RootDirectory, folder))
                {
                    running.Remove(key);
                    going.Add(server);
                }
            }

            if (going.Count > 0)
                started = [.. running.Values.OfType<LanguageServer>()];
        }

        return Task.WhenAll(going.Select(server => Task.Run(server.Dispose)));
    }

    /// <summary>Shuts every running server down, all at once, each given a moment to go by itself before it's ended.</summary>
    public static void StopAll()
    {
        List<LanguageServer> stopping;
        lock (gate)
        {
            stopping = [.. running.Values.OfType<LanguageServer>()];
            running.Clear();
            started = [];
        }

        Task.WaitAll([.. stopping.Select(server => Task.Run(server.Dispose))]);
    }
}
