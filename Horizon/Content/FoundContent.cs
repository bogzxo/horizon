namespace Horizon.Content;

/// <summary>A file as one installed source names it. <see cref="Key"/> tells caches apart and is never opened.</summary>
public readonly record struct FoundContent(IContentFiles Source, string Path, string Key)
{
    public Stream OpenRead() => Source.OpenRead(Path);
    public bool Exists => Source.Exists(Path);
    public byte[] ReadAllBytes() => ContentFiles.ReadAllBytes(Source, Path);
    public DateTime LastWriteTimeUtc => Source.GetLastWriteTimeUtc(Path);
}
