namespace Horizon.UI.Scripting;

/// <summary>
/// The language servers an editor knows of, by the files they lint, and the ones that are running. A tool registers
/// what it has a configuration for (<see cref="Register"/>, csharp-ls for .cs, pyright for .py), asks for the one a
/// file wants (<see cref="For"/>) and gets it started on the first file of its kind, or null when the command isn't on
/// this machine, which a tool takes quietly, a file nobody can lint is still a file. One server runs per
/// registration for the run, in the root folder it was first asked for, and <see cref="StopAll"/> ends them.
/// </summary>
public static class LanguageServers
{
    /// <summary>A server an editor can run, as a configuration names it.</summary>
    /// <param name="Name">What it is called, a status bar says it.</param>
    /// <param name="Command">The program, on the PATH or a full path.</param>
    /// <param name="Arguments">What the program is started with, --stdio for the ones that want asking.</param>
    /// <param name="LanguageId">The language id of the protocol, csharp, python.</param>
    /// <param name="Extensions">The files it lints, ".cs" and the like.</param>
    public sealed record Registration(string Name, string Command, string[] Arguments, string LanguageId, string[] Extensions);

    private static readonly Lock gate = new();
    private static readonly List<Registration> registrations = [];
    private static readonly Dictionary<string, LanguageServer?> running = new(StringComparer.Ordinal);

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

    /// <summary>Says that files with these extensions are linted by this command. A name registered before is replaced.</summary>
    public static void Register(string name, string command, string[] arguments, string languageId, string[] extensions)
    {
        lock (gate)
        {
            registrations.RemoveAll(other => other.Name == name);
            registrations.Add(new Registration(name, command, arguments, languageId, extensions));
            if (running.Remove(name, out LanguageServer? was))
            {
                was?.Dispose();
                started = [.. running.Values.OfType<LanguageServer>()];
            }
        }
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
    /// The server for a file, started in a root folder if it isn't running yet. Null when nothing is registered for
    /// the file, the command isn't on this machine, or the server couldn't be started (which isn't tried again).
    /// </summary>
    public static LanguageServer? For(string path, string rootDirectory)
    {
        if (RegistrationFor(path) is not { } entry)
            return null;

        lock (gate)
        {
            if (running.TryGetValue(entry.Name, out LanguageServer? known))
                return known is { Failure: null } ? known : null;

            LanguageServer? server = IsAvailable(entry.Command) ? LanguageServer.Start(entry.Name, entry.Command, entry.Arguments, entry.LanguageId, rootDirectory) : null;
            running[entry.Name] = server;
            if (server is not null)
                started = [.. running.Values.OfType<LanguageServer>()];
            return server;
        }
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

    /// <summary>Shuts every running server down.</summary>
    public static void StopAll()
    {
        List<LanguageServer> stopping;
        lock (gate)
        {
            stopping = [.. running.Values.OfType<LanguageServer>()];
            running.Clear();
            started = [];
        }

        foreach (LanguageServer server in stopping)
            server.Dispose();
    }
}
