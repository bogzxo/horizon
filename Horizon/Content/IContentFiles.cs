namespace Horizon.Content;

/// <summary>Read-only game files. Paths identify content and need not exist on the host filesystem.</summary>
public interface IContentFiles
{
    Stream OpenRead(string path);
    bool Exists(string path);
    bool DirectoryExists(string path);
    IEnumerable<string> EnumerateFiles(string directory, string pattern = "*", SearchOption search = SearchOption.TopDirectoryOnly);
    IEnumerable<string> EnumerateDirectories(string directory);

    /// <summary>A canonical path for relative references and cache keys.</summary>
    string ResolvePath(string path);

    /// <summary>When mutable content changed. Immutable packages may return <see cref="DateTime.MinValue"/>.</summary>
    DateTime GetLastWriteTimeUtc(string path) => DateTime.MinValue;
}
