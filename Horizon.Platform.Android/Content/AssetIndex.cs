using Horizon.Platform.Android.Native;

namespace Horizon.Platform.Android;

/// <summary>
/// Walks the APK's assets under some folders and lists every file in them, by its path from the assets root. Java's
/// AssetManager.List gives the names in a folder, files and folders alike, and gives nothing back for a file and for
/// an empty folder both. So a name with something under it is a folder, and a name with nothing under it is a file
/// when it opens and an empty folder when it doesn't. Empty folders aren't indexed, the same as IndexedContent knows
/// folders only by the files in them. Done once as the content is made, never per read.
/// </summary>
internal static class AssetIndex
{
    public static List<string> Build(global::Android.Content.Res.AssetManager assets, nint manager, IEnumerable<string> roots)
    {
        var files = new List<string>();
        foreach (string root in roots) Walk(assets, manager, root.Trim('/'), files);
        return files;
    }

    private static void Walk(global::Android.Content.Res.AssetManager assets, nint manager, string path, List<string> files)
    {
        string[] names = assets.List(path) ?? [];
        if (names.Length == 0)
        {
            // The root of the assets is always a folder, and asking open about "" means nothing
            if (path.Length > 0 && IsFile(manager, path)) files.Add(path);
            return;
        }

        foreach (string name in names)
        {
            if (string.IsNullOrEmpty(name)) continue;
            Walk(assets, manager, path.Length == 0 ? name : path + '/' + name, files);
        }
    }

    private static bool IsFile(nint manager, string path)
    {
        nint asset = NativeAssets.Open(manager, path, NativeAssets.MODE_STREAMING);
        // Streaming so a compressed asset is only opened here, not inflated to find out it is one
        if (asset == 0) return false;
        NativeAssets.Close(asset);
        return true;
    }
}
