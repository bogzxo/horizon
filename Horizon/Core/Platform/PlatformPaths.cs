namespace Horizon.Core.Platform;

/// <summary>
/// Where things go on the machine a program is running on, and how its paths compare. Windows keeps settings in
/// AppData and caches in LocalAppData, macOS keeps both under ~/Library, Linux goes by the XDG folders (~/.config,
/// ~/.local/share, ~/.local/state, ~/.cache, or what $XDG_*_HOME says). Every folder is made the first time it's asked for.
/// </summary>
public static class PlatformPaths
{
    /// <summary>
    /// How two paths compare on this system. Windows' and macOS' file systems don't mind the case of a name (an APFS
    /// volume made case-sensitive does, which is rare enough to go by the usual), Linux's do: Foo.png and foo.png are
    /// two files there.
    /// </summary>
    public static StringComparison Comparison { get; } = OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>The comparer for a set or a dictionary of paths, see <see cref="Comparison"/>.</summary>
    public static StringComparer Comparer { get; } = Comparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>Whether two paths are the same file, by their full paths, the way this system compares them.</summary>
    public static bool Same(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), Comparison);
        }
        catch (Exception)
        {
            return string.Equals(a, b, Comparison);
        }
    }

    /// <summary>Whether a path is inside a folder (or is it), the way this system compares them.</summary>
    public static bool IsUnder(string path, string folder)
    {
        string full = Path.GetFullPath(path), root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.Equals(root, Comparison) || full.StartsWith(root + Path.DirectorySeparatorChar, Comparison);
    }

    /// <summary>The user's home folder.</summary>
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// The folder of a program's settings, %AppData%\Name on Windows, ~/Library/Application Support/Name on macOS,
    /// $XDG_CONFIG_HOME/name (~/.config/name) on Linux, lower case there as folders are.
    /// </summary>
    public static string Config(string name) => Made(Path.Combine(ConfigRoot, Named(name)));

    /// <summary>The folder of what a program keeps that isn't settings, %LocalAppData% · ~/Library/Application Support · ~/.local/share.</summary>
    public static string Data(string name) => Made(Path.Combine(DataRoot, Named(name)));

    /// <summary>The folder of what a program keeps of its state (backups, history), ~/.local/state on Linux, the data folder elsewhere.</summary>
    public static string State(string name) => Made(Path.Combine(StateRoot, Named(name)));

    /// <summary>The folder of what can be thrown away and made again (compiled shaders), %LocalAppData% · ~/Library/Caches · ~/.cache.</summary>
    public static string Cache(string name) => Made(Path.Combine(CacheRoot, Named(name)));

    /// <summary>The folder above the programs' settings folders.</summary>
    public static string ConfigRoot =>
        OperatingSystem.IsWindows() ? Special(Environment.SpecialFolder.ApplicationData)
        : OperatingSystem.IsMacOS() ? Path.Combine(Home, "Library", "Application Support")
        : Xdg("XDG_CONFIG_HOME", ".config");

    public static string DataRoot =>
        OperatingSystem.IsWindows() ? Special(Environment.SpecialFolder.LocalApplicationData)
        : OperatingSystem.IsMacOS() ? Path.Combine(Home, "Library", "Application Support")
        : Xdg("XDG_DATA_HOME", Path.Combine(".local", "share"));

    public static string StateRoot => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? DataRoot : Xdg("XDG_STATE_HOME", Path.Combine(".local", "state"));

    public static string CacheRoot =>
        OperatingSystem.IsWindows() ? Special(Environment.SpecialFolder.LocalApplicationData)
        : OperatingSystem.IsMacOS() ? Path.Combine(Home, "Library", "Caches")
        : Xdg("XDG_CACHE_HOME", ".cache");

    /// <summary>
    /// The folder a program's own files (its Assets) are in: next to the program, or Contents/Resources when it is the
    /// program of a macOS .app (Contents/MacOS), where the bundle keeps them.
    /// </summary>
    public static string Program
    {
        get
        {
            string beside = AppContext.BaseDirectory;
            if (OperatingSystem.IsMacOS())
            {
                string trimmed = beside.TrimEnd('/');
                if (Path.GetFileName(trimmed) == "MacOS" && Path.GetDirectoryName(trimmed) is { } contents
                    && Path.Combine(contents, "Resources") is var resources && Directory.Exists(Path.Combine(resources, "Assets")))
                    return resources + Path.DirectorySeparatorChar;
            }

            return beside;
        }
    }

    // Windows' and macOS' folders are named as programs are, Linux's in lower case
    private static string Named(string name) => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? name : name.ToLowerInvariant();

    private static string Xdg(string variable, string otherwise) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } set && Path.IsPathRooted(set) ? set : Path.Combine(Home, otherwise);

    private static string Special(Environment.SpecialFolder folder) =>
        Environment.GetFolderPath(folder, Environment.SpecialFolderOption.Create) is { Length: > 0 } found ? found : AppContext.BaseDirectory;

    private static string Made(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception)
        {
            // Whoever writes in it finds out
        }

        return folder;
    }
}
