using System.IO.Enumeration;

namespace Horizon.Content;

/// <summary>A package's directory index. Its names use forward slashes and are case sensitive on every host.</summary>
public abstract class IndexedContent : IContentFiles
{
    private readonly HashSet<string> files;
    private readonly HashSet<string> directories = [string.Empty];
    private readonly string? diskRoot;

    protected IndexedContent(IEnumerable<string> paths, string? diskRoot = null)
    {
        this.diskRoot = diskRoot is null ? null : Path.GetFullPath(diskRoot);
        files = new(paths.Select(ResolvePath), StringComparer.Ordinal);
        foreach (string file in files)
        {
            string directory = Parent(file);
            while (directories.Add(directory) && directory.Length > 0) directory = Parent(directory);
        }
    }

    public string ResolvePath(string path)
    {
        path = path.Replace('\\', '/');
        if (Path.IsPathRooted(path))
        {
            if (diskRoot is null) throw new ArgumentException("A package path must be relative.", nameof(path));
            path = Path.GetRelativePath(diskRoot, path).Replace('\\', '/');

            // Another drive comes back as it was, which isn't anywhere in the package
            if (Path.IsPathRooted(path)) throw new ArgumentException("The path is outside of the package.", nameof(path));
        }
        var parts = new List<string>();
        foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) throw new ArgumentException("A package path cannot leave its root.", nameof(path));
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part);
        }
        return string.Join('/', parts);
    }

    public bool Exists(string path) => TryResolve(path, out string at) && files.Contains(at);
    public bool DirectoryExists(string path) => TryResolve(path, out string at) && directories.Contains(at);

    public Stream OpenRead(string path)
    {
        string key = ResolvePath(path);
        if (!files.Contains(key)) throw new FileNotFoundException($"Content '{path}' was not found.", path);
        return OpenFile(key);
    }

    protected abstract Stream OpenFile(string path);

    // Patterns mean what they mean to Directory.EnumerateFiles, "*.*" is everything there
    public IEnumerable<string> EnumerateFiles(string directory, string pattern = "*", SearchOption search = SearchOption.TopDirectoryOnly)
    {
        if (!TryResolve(directory, out string at)) return [];
        string prefix = at.Length == 0 ? string.Empty : at + '/';
        string expression = FileSystemName.TranslateWin32Expression(pattern);
        return files.Where(path => (search == SearchOption.AllDirectories ? path.StartsWith(prefix, StringComparison.Ordinal) : Parent(path) == at)
            && FileSystemName.MatchesWin32Expression(expression, path[(path.LastIndexOf('/') + 1)..], ignoreCase: false))
            .Order(StringComparer.Ordinal).Select(path => Returned(directory, path));
    }

    public IEnumerable<string> EnumerateDirectories(string directory)
    {
        if (!TryResolve(directory, out string at)) return [];
        return directories.Where(path => path.Length > 0 && Parent(path) == at).Order(StringComparer.Ordinal).Select(path => Returned(directory, path));
    }

    private bool TryResolve(string path, out string resolved)
    {
        try { resolved = ResolvePath(path); return true; }
        catch (ArgumentException) { resolved = string.Empty; return false; }
    }

    // Asked about a folder by where it is on the disk, the answers say where on the disk too, the way DiskContent's do,
    // so an overlay of the two lists a file once
    private string Returned(string asked, string path) =>
        diskRoot is not null && Path.IsPathRooted(asked) ? Path.Combine(diskRoot, path.Replace('/', Path.DirectorySeparatorChar)) : path;

    private static string Parent(string path) => path.LastIndexOf('/') is int index && index >= 0 ? path[..index] : string.Empty;
}
