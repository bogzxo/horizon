namespace Horizon.Platform.Android;

/// <summary>
/// Where an app on a phone keeps what it writes. Settings, data and state go in folders of their own under the app's
/// private files folder (Context.FilesDir), what can be made again goes under its cache folder (Context.CacheDir),
/// which Android may empty when it's short of space. The APK's assets are read only and are AndroidContent's, nothing
/// is ever written next to them.
/// <para>
/// Saving is DiskStorage's, a new file beside the old one moved over it once it's all flushed to the disk, so a save
/// that throws or is never committed leaves the one before it as it was. The difference is that every path has to be
/// inside one of the roots here, a name or a path that climbs out of them is an error rather than a write somewhere else.
/// </para>
/// <para>
/// The roots are looked up once as this is made and kept as plain strings in <see cref="Roots"/>, so nothing holds on
/// to the context afterwards and the host can hand the same folders to whatever else wants them (the shader and
/// pipeline caches, debug.log).
/// </para>
/// </summary>
public sealed class AndroidStorage : IPlatformStorage
{
    // The same saving the desktop does, for paths already checked to be inside the roots
    private readonly DiskStorage disk = new();

    /// <summary>The folders everything goes under, as Android gave them when this was made.</summary>
    public AndroidStorageRoots Roots { get; }

    /// <summary>Takes the roots of an app's context. Main thread or any thread attached to the Java VM.</summary>
    public AndroidStorage(global::Android.Content.Context context) : this(AndroidStorageRoots.Of(context))
    {
    }

    /// <summary>Over roots somebody already has, which is also how it's checked away from a context.</summary>
    public AndroidStorage(AndroidStorageRoots roots)
    {
        Roots = roots ?? throw new ArgumentNullException(nameof(roots));
    }

    public string Config(string name) => Made(Roots.Config, name);
    public string Data(string name) => Made(Roots.Data, name);
    public string State(string name) => Made(Roots.State, name);
    public string Cache(string name) => Made(Roots.Cache, name);

    public bool Exists(string path) => Inside(path, out string full) && File.Exists(full);
    public Stream OpenRead(string path) => disk.OpenRead(Checked(path));
    public IStorageWrite BeginWrite(string path) => disk.BeginWrite(Checked(path));
    public void Delete(string path) => disk.Delete(Checked(path));

    // A program's folder under a root, made the first time it's asked for. It may be a few names deep (the shader cache
    // asks for Horizon/<game>), either slash goes as the desktop's Path.Combine would take it, but it's always relative
    // and nothing in it climbs, so it can't land anywhere but under the root
    private static string Made(string root, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Contains('\0') || Path.IsPathRooted(name) || name[0] is '/' or '\\')
            throw new ArgumentException($"'{name}' isn't a folder name, it has to be relative to the app's own folder.", nameof(name));

        string[] parts = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || Array.Exists(parts, part => part is "." or ".." || string.IsNullOrWhiteSpace(part)))
            throw new ArgumentException($"'{name}' isn't a folder name, none of its parts may be empty, '.' or '..'.", nameof(name));

        string folder = Path.GetFullPath(Path.Combine(root, string.Join('/', parts)));
        if (!folder.StartsWith(root + '/', StringComparison.Ordinal))
            throw new ArgumentException($"'{name}' ends up outside the app's storage folder.", nameof(name));

        Directory.CreateDirectory(folder);
        return folder;
    }

    private string Checked(string path) => Inside(path, out string full)
        ? full
        : throw new ArgumentException($"'{path}' isn't inside the app's storage folders.", nameof(path));

    // Relative paths count from the files folder, the way the app's working directory would have them
    private bool Inside(string path, out string full)
    {
        full = string.Empty;
        if (string.IsNullOrEmpty(path) || path.Contains('\0')) return false;

        full = Path.GetFullPath(path, Roots.Files);
        foreach (string root in Roots.All)
        {
            if (full.StartsWith(root + '/', StringComparison.Ordinal)) return true;
        }
        return false;
    }
}

/// <summary>
/// The folders an Android app writes in, as they were when asked. Files and Cache are Android's, the other three are
/// folders under Files.
/// </summary>
public sealed record AndroidStorageRoots(string Files, string Cache)
{
    /// <summary>Settings.</summary>
    public string Config { get; } = Path.Combine(Normal(Files), "config");

    /// <summary>What the game keeps that isn't settings, saves and the like.</summary>
    public string Data { get; } = Path.Combine(Normal(Files), "data");

    /// <summary>State, backups and history.</summary>
    public string State { get; } = Path.Combine(Normal(Files), "state");

    public string Files { get; } = Normal(Files);
    public string Cache { get; } = Normal(Cache);

    /// <summary>Every root a path may be inside.</summary>
    public IReadOnlyList<string> All => [Config, Data, State, Cache];

    /// <summary>The roots of an app's context, FilesDir and CacheDir.</summary>
    public static AndroidStorageRoots Of(global::Android.Content.Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string files = context.FilesDir?.AbsolutePath ?? throw new InvalidOperationException("The context has no files folder.");
        string cache = context.CacheDir?.AbsolutePath ?? throw new InvalidOperationException("The context has no cache folder.");
        return new AndroidStorageRoots(files, cache);
    }

    private static string Normal(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (!Path.IsPathRooted(folder)) throw new ArgumentException($"'{folder}' isn't a full path.", nameof(folder));
        return Path.GetFullPath(folder).TrimEnd('/');
    }
}
