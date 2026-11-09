using Foundation;
using Horizon.Content;

namespace Horizon.Platform.iOS;

/// <summary>The signed bundle's game files, indexed once with the same case-sensitive names as Android content.</summary>
public sealed class IosContent : IndexedContent
{
    private readonly string root;

    public IosContent() : this(NSBundle.MainBundle.ResourcePath) { }

    public IosContent(string resourceRoot) : base(Index(resourceRoot), resourceRoot)
        => root = Path.GetFullPath(resourceRoot);

    protected override Stream OpenFile(string path) => File.OpenRead(Path.Combine(root, path));

    private static IEnumerable<string> Index(string resourceRoot)
    {
        string root = Path.GetFullPath(resourceRoot);
        foreach (string name in new[] { "Assets", "shaders" })
        {
            string folder = Path.Combine(root, name);
            if (!Directory.Exists(folder)) continue;
            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                yield return Path.GetRelativePath(root, file).Replace('\\', '/');
        }
    }
}
