namespace Horizon.UI.Components;

/// <summary>
/// Where Hex's own files are, the ones Dawn and Dusk share. The settings folder next to the program's exe, with
/// settings.hor (in sections, see SettingsSections), keys.hor, plugins/, history.txt and sessions.hor in it, so they're
/// a double click away from Hex.exe rather than off in AppData. What was in %AppData%\Hex (or %AppData%\Dusk before
/// that) is copied over the first time. Where the exe's folder can't be written (Program Files) it's still %AppData%\Hex
/// on Windows and ~/.config/hex elsewhere. A file next to a program's exe wins over the folder's when there is one, the
/// plugins next to it come on top. <c>HEX_HOME</c> (or <c>DUSK_HOME</c>, as it was) moves the whole folder, for a test.
/// </summary>
public static class TerminalFiles
{
    public const string KEYS = "keys.hor", PLUGINS = "plugins", HISTORY = "history.txt", SESSIONS = "sessions.hor";

    /// <summary>The name of the folder next to the exe.</summary>
    public const string BESIDE = "settings";

    // the folder worked out once, it's asked for all the time and the answer doesn't change in a run
    private static string? found;

    /// <summary>The user's folder of Hex's files, made the first time it is asked for.</summary>
    public static string UserFolder
    {
        get
        {
            if ((Environment.GetEnvironmentVariable("HEX_HOME") ?? Environment.GetEnvironmentVariable("DUSK_HOME")) is { Length: > 0 } home)
            {
                Make(home);
                return home;
            }

            if (found is not null)
                return found;

            string parent = OperatingSystem.IsWindows()
                ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
                    ? config
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            string user = Path.Combine(parent, OperatingSystem.IsWindows() ? "Hex" : "hex");

            // Dusk's folder from before it was a part of Hex, moved over once, left where it was in case
            string old = Path.Combine(parent, OperatingSystem.IsWindows() ? "Dusk" : "dusk");
            if (!Directory.Exists(user) && Directory.Exists(old))
                CopyFolder(old, user);

            // Next to the exe, where anybody looking for them looks first, with what AppData had copied in the first time
            string beside = Path.Combine(AppContext.BaseDirectory, BESIDE);
            bool fresh = !Directory.Exists(beside);
            if (Writable(beside))
            {
                if (fresh && Directory.Exists(user))
                    CopyFolder(user, beside);
                return found = beside;
            }

            Make(user);
            return found = user;
        }
    }

    // Whether a folder can be made and written into, which a program in Program Files' can't
    private static bool Writable(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string probe = Path.Combine(folder, ".written");
            System.IO.File.WriteAllText(probe, string.Empty);
            System.IO.File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Make(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception)
        {
            // A folder that can't be made is a folder nothing is kept in, the files say so when they are written
        }
    }

    private static void CopyFolder(string from, string to)
    {
        try
        {
            foreach (string folder in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, folder)));
            Directory.CreateDirectory(to);
            foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
                System.IO.File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: false);
        }
        catch (Exception)
        {
            // what didn't come over is made anew
        }
    }

    /// <summary>A file of the terminal's, the one next to the exe when there is one there, the user's otherwise.</summary>
    public static string File(string name, string? programFolder = null)
    {
        if (programFolder is not null)
        {
            string beside = Path.Combine(programFolder, name);
            if (System.IO.File.Exists(beside) || Directory.Exists(beside))
                return beside;
        }

        return Path.Combine(UserFolder, name);
    }

    public static string Settings(string? programFolder = null) => File(TerminalSettings.FILE, programFolder);

    public static string Keys(string? programFolder = null) => File(KEYS, programFolder);

    public static string History(string? programFolder = null) => File(HISTORY, programFolder);

    public static string Sessions(string? programFolder = null) => File(SESSIONS, programFolder);

    /// <summary>
    /// The folders plugins are found in, the program's own (built in) first, then the user's, then any next to the exe,
    /// a plugin of the same folder name in a later one standing in for the earlier.
    /// </summary>
    public static List<(string Folder, bool BuiltIn)> PluginFolders(string builtIn, string? programFolder = null)
    {
        var folders = new List<(string, bool)> { (builtIn, true), (Path.Combine(UserFolder, PLUGINS), false) };
        if (programFolder is not null && Directory.Exists(Path.Combine(programFolder, PLUGINS)))
            folders.Add((Path.Combine(programFolder, PLUGINS), false));
        return folders;
    }

    /// <summary>Whether a file is one of the terminal's, by being in the user's folder or saying #define dusk at its top.</summary>
    public static bool IsTerminalFile(string path, string? text = null)
    {
        string full = Path.GetFullPath(path);
        if (full.StartsWith(Path.GetFullPath(UserFolder), StringComparison.OrdinalIgnoreCase))
            return true;

        text ??= System.IO.File.Exists(path) ? ReadHead(path) : string.Empty;
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("#define dusk", StringComparison.Ordinal))
                return true;
            if (trimmed.Length > 0 && !trimmed.StartsWith("//", StringComparison.Ordinal) && !trimmed.StartsWith('#'))
                return false;
        }

        return false;
    }

    private static string ReadHead(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            char[] head = new char[2048];
            int read = reader.Read(head, 0, head.Length);
            return new string(head, 0, read);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
