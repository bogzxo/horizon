using Horizon.HIDL;
using Horizon.HIDL.Runtime;

namespace Horizon.Content;

/// <summary>
/// Points a HIDL runtime at game content, so the files it runs and includes come out of <see cref="ContentFiles"/>
/// rather than the disk. On desktop that's the same files by the same full paths as before, out of a pack the paths
/// stay the pack's own and an include next to an included file still finds it.
/// </summary>
public static class ContentScripts
{
    /// <summary>
    /// Has the runtime read through one content source, or through whichever is installed at the time it reads when it
    /// isn't given one, so a runtime made before the host installs its source still reads the right files.
    /// </summary>
    public static HIDLRuntime ReadingContent(this HIDLRuntime runtime, IContentFiles? from = null)
    {
        Func<IContentFiles> source = from is null ? static () => ContentFiles.Source : () => from;
        runtime.FilePathResolver = path => source().ResolvePath(path);
        runtime.FileReader = path => ContentFiles.ReadAllText(source(), path);
        runtime.IncludeReader = (file, folder) =>
        {
            string path = Path.IsPathRooted(file) || folder is null ? file : Path.Combine(folder, file);
            IContentFiles files = source();
            return files.Exists(path) ? ContentFiles.ReadAllText(files, path) : throw new HidlRuntimeException($"There is no '{path}' to include.");
        };
        return runtime;
    }
}
