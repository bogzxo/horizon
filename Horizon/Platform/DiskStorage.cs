using Horizon.Core.Platform;

namespace Horizon.Platform;

/// <summary>Desktop roots and saves replaced only after a complete write.</summary>
public sealed class DiskStorage : IPlatformStorage
{
    public string Config(string name) => PlatformPaths.Config(name);
    public string Data(string name) => PlatformPaths.Data(name);
    public string State(string name) => PlatformPaths.State(name);
    public string Cache(string name) => PlatformPaths.Cache(name);
    public bool Exists(string path) => File.Exists(path);
    public Stream OpenRead(string path) => File.OpenRead(path);
    public IStorageWrite BeginWrite(string path) => new Write(path);
    public void Delete(string path) => File.Delete(path);

    // The new save goes in a file of its own beside the old one and is moved over it once it's all on the disk
    private sealed class Write : IStorageWrite
    {
        private readonly string target;
        private readonly string temporary;
        private readonly FileStream stream;
        private bool committed;
        private bool disposed;
        public Stream Stream => stream;

        public Write(string path)
        {
            target = Path.GetFullPath(path);
            if (Path.GetDirectoryName(target) is { Length: > 0 } folder) Directory.CreateDirectory(folder);
            temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }

        public void Commit()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (committed) throw new InvalidOperationException("This save has already been committed.");
            stream.Flush(flushToDisk: true);
            stream.Dispose();
            File.Move(temporary, target, overwrite: true);
            committed = true;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            // A write that failed half way may fail again flushing what's left, which is no reason to keep the temp file
            try { stream.Dispose(); }
            catch (IOException) { }
            if (committed) return;
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
