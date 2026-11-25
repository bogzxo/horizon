namespace Horizon.Platform;

/// <summary>
/// Native open, save and folder dialogs of the host operating system (macOS <c>NSOpenPanel</c> / <c>NSSavePanel</c>),
/// null where the platform has none and the UI's own file browser is used instead.
/// </summary>
public interface IPlatformDialogs
{
    /// <summary>Opens a native file picker, returning the chosen path or null if cancelled.</summary>
    Task<string?> OpenFile(string title, string directory, IReadOnlyList<(string Name, string Pattern)> filter, string picked = "");

    /// <summary>Opens a native folder picker, returning the chosen directory path or null if cancelled.</summary>
    Task<string?> OpenFolder(string title, string directory);

    /// <summary>Opens a native save dialog, returning the chosen path or null if cancelled.</summary>
    Task<string?> SaveFile(string title, string directory, string name, IReadOnlyList<(string Name, string Pattern)> filter);
}

/// <summary>Pure filter parsing helpers shared by native dialog implementations and tests.</summary>
public static class PlatformDialogFilters
{
    private static readonly char[] Separators = [';', ',', ' '];

    /// <summary>
    /// Extracts normalized lowercase file extensions without leading <c>*.</c> or <c>.</c> from a dialog filter list.
    /// Returns an empty array when <paramref name="filter"/> is empty or contains a wildcard (<c>*</c> or <c>*.*</c>),
    /// meaning all file types should be allowed.
    /// </summary>
    public static string[] ExtensionsOf(IReadOnlyList<(string Name, string Pattern)>? filter)
    {
        if (filter is null || filter.Count == 0)
            return [];

        List<string>? extensions = null;
        HashSet<string>? seen = null;

        for (int i = 0; i < filter.Count; i++)
        {
            string pattern = filter[i].Pattern;
            if (string.IsNullOrWhiteSpace(pattern))
                continue;

            string[] parts = pattern.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (int p = 0; p < parts.Length; p++)
            {
                string token = parts[p];
                if (token is "*" or "*.*" or ".*")
                    return [];

                string ext = token.TrimStart('*', '.').ToLowerInvariant();
                if (ext.Length == 0 || ext.IndexOfAny(['*', '?']) >= 0)
                    continue;

                extensions ??= [];
                seen ??= new HashSet<string>(StringComparer.Ordinal);
                if (seen.Add(ext))
                    extensions.Add(ext);
            }
        }

        return extensions is null ? [] : [.. extensions];
    }
}
