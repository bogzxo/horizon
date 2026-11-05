namespace Horizon.Platform.Android;

/// <summary>
/// The folders an app on a phone has for itself, as they were when it started, and the one rule for putting a path a
/// run was handed (a screenshot, an input script) into them. Files and Cache are private, only the app and
/// <c>adb shell run-as</c> get in. External is the app's folder on shared storage, which <c>adb pull</c> reads without
/// run-as, and it's where screenshots go. External can be missing (no shared storage mounted), then Files stands in.
/// </summary>
/// <param name="Files">Context.FilesDir.</param>
/// <param name="Cache">Context.CacheDir.</param>
/// <param name="External">Context.GetExternalFilesDir(null), or Files when there is none.</param>
public sealed record AndroidPaths(string Files, string Cache, string External)
{
    /// <summary>Where screenshots of a run go, under External.</summary>
    public string Screenshots => Path.Combine(External, "screenshots");

    /// <summary>The folders of an app's context.</summary>
    public static AndroidPaths Of(global::Android.Content.Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string files = context.FilesDir?.AbsolutePath ?? throw new InvalidOperationException("The context has no files folder.");
        string cache = context.CacheDir?.AbsolutePath ?? throw new InvalidOperationException("The context has no cache folder.");
        string external = context.GetExternalFilesDir(null)?.AbsolutePath ?? files;
        return new AndroidPaths(files.TrimEnd('/'), cache.TrimEnd('/'), external.TrimEnd('/'));
    }

    /// <summary>
    /// A path a run was handed, made full under a folder. Relative is taken from the folder, a full one has to be inside
    /// it already. Null when it climbs out or is somewhere else, which the caller says in the log rather than writing there.
    /// </summary>
    public static string? Inside(string folder, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0')) return null;

        string root = Path.GetFullPath(folder).TrimEnd('/');
        string full = Path.GetFullPath(path.Replace('\\', '/'), root);
        return full.StartsWith(root + '/', StringComparison.Ordinal) ? full : null;
    }

    /// <summary>Whether a full path is inside any of the app's folders.</summary>
    public bool Owns(string full) => Inside(Files, full) is not null || Inside(Cache, full) is not null || Inside(External, full) is not null;
}
