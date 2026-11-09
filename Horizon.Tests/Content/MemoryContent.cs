using Horizon.Content;

namespace Horizon.Tests.Content;

/// <summary>
/// A package held in memory with no disk under it. Its streams can't seek and hand over a few bytes at a time, the
/// worst a packed file does, so a reader that assumes a FileStream falls over here first.
/// </summary>
internal sealed class MemoryContent(IReadOnlyDictionary<string, byte[]> files, string? diskRoot = null, bool awkward = true)
    : IndexedContent(files.Keys, diskRoot)
{
    private readonly Dictionary<string, byte[]> bytes = files.ToDictionary(pair => Normalise(pair.Key), pair => pair.Value, StringComparer.Ordinal);

    public int Opened { get; private set; }

    public static MemoryContent Of(params (string Path, string Text)[] files) =>
        new(files.ToDictionary(file => file.Path, file => System.Text.Encoding.UTF8.GetBytes(file.Text)));

    protected override Stream OpenFile(string path)
    {
        Opened++;
        var stream = new MemoryStream(bytes[path], writable: false);
        return awkward ? new TrickleStream(stream) : stream;
    }

    private static string Normalise(string path) => string.Join('/', path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>A stream that can't seek and reads at most seven bytes at a time.</summary>
internal sealed class TrickleStream(Stream inner) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, 7));
    public override int Read(Span<byte> buffer) => inner.Read(buffer[..Math.Min(buffer.Length, 7)]);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }
    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Puts a content source in for one test and the one that was there back after. Tests that do are in the
/// <see cref="ContentSourceCollection"/>, which runs on its own, so nothing reading the disk at the same time sees it.
/// </summary>
internal sealed class InstalledContent : IDisposable
{
    private readonly IContentFiles before = ContentFiles.Source;
    public InstalledContent(IContentFiles source) => ContentFiles.Source = source;
    public void Dispose() => ContentFiles.Source = before;
}

[CollectionDefinition(NAME, DisableParallelization = true)]
public sealed class ContentSourceCollection
{
    public const string NAME = "Installed content source";
}
