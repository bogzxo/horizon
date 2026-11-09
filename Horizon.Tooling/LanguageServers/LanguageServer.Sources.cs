using System.Collections.Concurrent;

namespace Horizon.UI.Scripting;

public sealed partial class LanguageServer
{
    private readonly ConcurrentDictionary<string, string> sourceUris = new(Horizon.Core.Platform.PlatformPaths.Comparer);

    /// <summary>Routes a local read-only source view back to the virtual document owned by this server.</summary>
    public void RegisterSource(string path, string uri)
    {
        if (LanguageId != "csharp" || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme != "csharp")
            throw new ArgumentException("Only C# server-owned source URIs can be registered.", nameof(uri));
        sourceUris[Path.GetFullPath(path)] = parsed.AbsoluteUri;
    }

    public void ForgetSource(string path) => sourceUris.TryRemove(Path.GetFullPath(path), out _);

    private string UriOf(string path)
    {
        string full = Path.GetFullPath(path);
        return sourceUris.TryGetValue(full, out var uri) ? uri : new Uri(full).AbsoluteUri;
    }
}
