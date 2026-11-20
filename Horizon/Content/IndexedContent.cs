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

    public string ResolvePath(string path) =>
        TryResolve(path, out string resolved, out string? problem) ? resolved : throw new ArgumentException(problem, nameof(path));

    /// <summary>
    /// Helper method to turn a path into the one the index keeps it by, or say why it can't be one. Every look at the
    /// package comes through here, and nearly every path it is asked about is one already, which is handed back as it is.
    /// A path that isn't in the package is an answer here rather than an exception, asking whether one exists is no error.
    /// </summary>
    private bool TryResolve(string path, out string resolved, out string? problem)
    {
        problem = null;
        if (IsCanonical(path))
        {
            resolved = path;
            return true;
        }

        resolved = string.Empty;
        path = path.Replace('\\', '/');
        if (Path.IsPathRooted(path))
        {
            if (diskRoot is null)
            {
                problem = "A package path must be relative.";
                return false;
            }

            path = Path.GetRelativePath(diskRoot, path).Replace('\\', '/');

            // Another drive comes back as it was, which isn't anywhere in the package
            if (Path.IsPathRooted(path))
            {
                problem = "The path is outside of the package.";
                return false;
            }
        }
        var parts = new List<string>();
        foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0)
                {
                    problem = "A package path cannot leave its root.";
                    return false;
                }
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part);
        }
        resolved = string.Join('/', parts);
        return true;
    }

    /// <summary>
    /// Helper method for whether a path is written the way the index keeps it already: forward slashes, relative, no
    /// empty parts and no . or .. in it. Such a path comes out of <see cref="TryResolve"/> the same as it went in.
    /// </summary>
    private static bool IsCanonical(string path)
    {
        if (path.Contains('\\') || Path.IsPathRooted(path))
            return false;

        ReadOnlySpan<char> rest = path;
        while (rest.Length > 0)
        {
            int slash = rest.IndexOf('/');
            ReadOnlySpan<char> part = slash < 0 ? rest : rest[..slash];
            if (part.IsEmpty || part is "." or "..")
                return false;

            if (slash < 0)
                break;

            // A slash at the very end leaves an empty part behind it
            rest = rest[(slash + 1)..];
            if (rest.IsEmpty)
                return false;
        }

        return true;
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
        return files.Where(path => (search == SearchOption.AllDirectories ? path.StartsWith(prefix, StringComparison.Ordinal) : IsIn(path, at))
            && FileSystemName.MatchesWin32Expression(expression, path.AsSpan(path.LastIndexOf('/') + 1), ignoreCase: false))
            .Order(StringComparer.Ordinal).Select(path => Returned(directory, path));
    }

    public IEnumerable<string> EnumerateDirectories(string directory)
    {
        if (!TryResolve(directory, out string at)) return [];
        return directories.Where(path => path.Length > 0 && IsIn(path, at)).Order(StringComparer.Ordinal).Select(path => Returned(directory, path));
    }

    private bool TryResolve(string path, out string resolved) => TryResolve(path, out resolved, out _);

    // Asked about a folder by where it is on the disk, the answers say where on the disk too, the way DiskContent's do,
    // so an overlay of the two lists a file once
    private string Returned(string asked, string path) =>
        diskRoot is not null && Path.IsPathRooted(asked) ? Path.Combine(diskRoot, path.Replace('/', Path.DirectorySeparatorChar)) : path;

    private static string Parent(string path) => path.LastIndexOf('/') is int index && index >= 0 ? path[..index] : string.Empty;

    // Whether a path is right inside of a folder, the way Parent(path) == folder says without making the parent to compare
    private static bool IsIn(string path, string folder) =>
        path.AsSpan(0, Math.Max(path.LastIndexOf('/'), 0)).SequenceEqual(folder);
}
