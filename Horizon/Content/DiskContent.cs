namespace Horizon.Content;

/// <summary>Desktop content, including absolute paths opened by tools. With no root it follows the working directory.</summary>
public sealed class DiskContent(string? root = null) : IContentFiles
{
    private readonly string? root = root is null ? null : Path.GetFullPath(root);
    public string Root => root ?? Environment.CurrentDirectory;
    public string ResolvePath(string path) => Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar), Root);
    public Stream OpenRead(string path) => File.OpenRead(ResolvePath(path));
    public bool Exists(string path) => File.Exists(ResolvePath(path));
    public bool DirectoryExists(string path) => Directory.Exists(ResolvePath(path));
    public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(ResolvePath(path));

    public IEnumerable<string> EnumerateFiles(string directory, string pattern = "*", SearchOption search = SearchOption.TopDirectoryOnly)
    {
        string at = ResolvePath(directory);
        if (!Directory.Exists(at)) return [];
        return Directory.EnumerateFiles(at, pattern, search).Select(path => ReturnedPath(directory, path));
    }

    public IEnumerable<string> EnumerateDirectories(string directory)
    {
        string at = ResolvePath(directory);
        if (!Directory.Exists(at)) return [];
        return Directory.EnumerateDirectories(at).Select(path => ReturnedPath(directory, path));
    }

    private string ReturnedPath(string from, string path) => Path.IsPathRooted(from) ? path : Path.GetRelativePath(Root, path);
}
