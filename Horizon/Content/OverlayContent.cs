namespace Horizon.Content;

/// <summary>Sources in priority order. Loose desktop assets can override files shipped inside a package.</summary>
public sealed class OverlayContent(params IContentFiles[] sources) : IContentFiles
{
    private readonly IContentFiles[] sources = sources.Length > 0 ? [.. sources] : throw new ArgumentException("At least one content source is required.", nameof(sources));

    private IContentFiles Owner(string path) => sources.FirstOrDefault(source => source.Exists(path) || source.DirectoryExists(path)) ?? sources[0];
    public string ResolvePath(string path) => Path.IsPathRooted(path)
        ? Path.GetFullPath(path)
        : Owner(path).ResolvePath(path);
    public bool Exists(string path) => sources.Any(source => source.Exists(path));
    public bool DirectoryExists(string path) => sources.Any(source => source.DirectoryExists(path));
    public Stream OpenRead(string path) => (sources.FirstOrDefault(source => source.Exists(path)) ?? sources[0]).OpenRead(path);
    public DateTime GetLastWriteTimeUtc(string path) => Owner(path).GetLastWriteTimeUtc(path);

    public IEnumerable<string> EnumerateFiles(string directory, string pattern = "*", SearchOption search = SearchOption.TopDirectoryOnly) =>
        Distinct(sources.Where(source => source.DirectoryExists(directory)).SelectMany(source => source.EnumerateFiles(directory, pattern, search)));

    public IEnumerable<string> EnumerateDirectories(string directory) =>
        Distinct(sources.Where(source => source.DirectoryExists(directory)).SelectMany(source => source.EnumerateDirectories(directory)));

    // The first source to list a path wins. Either slash is the same path, and on Windows so is either case, like its disk
    private static IEnumerable<string> Distinct(IEnumerable<string> paths) =>
        paths.DistinctBy(path => path.Replace('\\', '/'), OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
}
