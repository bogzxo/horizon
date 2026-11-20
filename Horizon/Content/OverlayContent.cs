namespace Horizon.Content;

/// <summary>Sources in priority order. Loose desktop assets can override files shipped inside a package.</summary>
public sealed class OverlayContent(params IContentFiles[] sources) : IContentFiles
{
    private readonly IContentFiles[] sources = sources.Length > 0 ? [.. sources] : throw new ArgumentException("At least one content source is required.", nameof(sources));

    private IContentFiles Owner(string path)
    {
        foreach (IContentFiles source in sources)
        {
            if (source.Exists(path) || source.DirectoryExists(path)) return source;
        }

        return sources[0];
    }

    // The first source that has the file, the first of all if none has. Plain loops, every asset that is opened asks
    private IContentFiles HolderOf(string path)
    {
        foreach (IContentFiles source in sources)
        {
            if (source.Exists(path)) return source;
        }

        return sources[0];
    }

    public string ResolvePath(string path) => Path.IsPathRooted(path)
        ? Path.GetFullPath(path)
        : Owner(path).ResolvePath(path);
    public bool Exists(string path)
    {
        foreach (IContentFiles source in sources)
        {
            if (source.Exists(path)) return true;
        }

        return false;
    }

    public bool DirectoryExists(string path)
    {
        foreach (IContentFiles source in sources)
        {
            if (source.DirectoryExists(path)) return true;
        }

        return false;
    }

    public Stream OpenRead(string path) => HolderOf(path).OpenRead(path);
    public DateTime GetLastWriteTimeUtc(string path) => Owner(path).GetLastWriteTimeUtc(path);

    public IEnumerable<string> EnumerateFiles(string directory, string pattern = "*", SearchOption search = SearchOption.TopDirectoryOnly) =>
        Distinct(sources.Where(source => source.DirectoryExists(directory)).SelectMany(source => source.EnumerateFiles(directory, pattern, search)));

    public IEnumerable<string> EnumerateDirectories(string directory) =>
        Distinct(sources.Where(source => source.DirectoryExists(directory)).SelectMany(source => source.EnumerateDirectories(directory)));

    // The first source to list a path wins. Either slash is the same path, and on Windows so is either case, like its disk
    private static IEnumerable<string> Distinct(IEnumerable<string> paths) =>
        paths.DistinctBy(path => path.Replace('\\', '/'), OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
}
