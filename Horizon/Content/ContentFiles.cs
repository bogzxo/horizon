using System.Text;

namespace Horizon.Content;

/// <summary>The content source installed by the host before it loads game assets.</summary>
public static class ContentFiles
{
    private sealed record Installed(IContentFiles Source, long Version, string Prefix);
    private static Installed installed = new(new DiskContent(), 0, "0|");
    private static long version;

    /// <summary>Install before starting engine threads. Replacing a source gives decoded caches a new identity.</summary>
    public static IContentFiles Source
    {
        get => Volatile.Read(ref installed).Source;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            long next = Interlocked.Increment(ref version);
            Volatile.Write(ref installed, new Installed(value, next, $"{next}|"));
        }
    }

    public static Stream OpenRead(string path) => Source.OpenRead(path);
    public static bool Exists(string path) => Source.Exists(path);
    public static bool DirectoryExists(string path) => Source.DirectoryExists(path);
    public static string ResolvePath(string path) => Source.ResolvePath(path);
    public static DateTime GetLastWriteTimeUtc(string path) => Source.GetLastWriteTimeUtc(path);
    public static IEnumerable<string> EnumerateFiles(string directory, string pattern = "*", SearchOption search = SearchOption.TopDirectoryOnly) => Source.EnumerateFiles(directory, pattern, search);
    public static IEnumerable<string> EnumerateDirectories(string directory) => Source.EnumerateDirectories(directory);
    public static string[] GetFiles(string directory, string pattern = "*", SearchOption search = SearchOption.TopDirectoryOnly) => [.. EnumerateFiles(directory, pattern, search)];
    public static string[] GetDirectories(string directory) => [.. EnumerateDirectories(directory)];

    /// <summary>A cache identity, never a path to open.</summary>
    public static string CacheKey(string path) => Find(path).Key;

    /// <summary>
    /// The source, canonical path and cache key of a file, all from one installed source. Anything that reads the file
    /// again later (after letting go of its bytes) keeps the source it got here, so a source swapped in since can't
    /// hand it somebody else's bytes under the same name.
    /// </summary>
    public static FoundContent Find(string path)
    {
        Installed current = Volatile.Read(ref installed);
        string resolved = current.Source.ResolvePath(path);
        return new FoundContent(current.Source, resolved, string.Concat(current.Prefix, resolved));
    }

    public static byte[] ReadAllBytes(string path) => ReadAllBytes(Source, path);

    public static byte[] ReadAllBytes(IContentFiles source, string path)
    {
        using Stream stream = source.OpenRead(path);

        // A file on the disk (or an embedded one) says how long it is, so it is read straight into an array that size.
        // Through a memory stream it was copied twice more, once each time the stream grew and once more out of it
        if (stream.CanSeek && stream.Length - stream.Position is long length && length <= Array.MaxLength)
        {
            var exact = GC.AllocateUninitializedArray<byte>((int)length);
            int read = stream.ReadAtLeast(exact, exact.Length, throwOnEndOfStream: false);

            // Shorter than it said by the time it was read, what there was of it
            if (read < exact.Length)
                return exact[..read];

            // Grown since it was measured, the rest comes the long way
            int next = stream.ReadByte();
            if (next < 0)
                return exact;

            using var grown = new MemoryStream();
            grown.Write(exact);
            grown.WriteByte((byte)next);
            stream.CopyTo(grown);
            return grown.ToArray();
        }

        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    public static string ReadAllText(string path) => ReadAllText(Source, path);

    public static string ReadAllText(IContentFiles source, string path)
    {
        using var reader = new StreamReader(source.OpenRead(path), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public static string[] ReadAllLines(string path)
    {
        using var reader = new StringReader(ReadAllText(path));
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return [.. lines];
    }

    /// <summary>A seekable stream for decoders that rewind. Ordinary asset reads need no extra copy.</summary>
    public static Stream OpenSeekable(string path)
    {
        Stream stream = OpenRead(path);
        if (stream.CanSeek) return stream;
        using (stream)
        {
            var copy = new MemoryStream();
            try
            {
                stream.CopyTo(copy);
                copy.Position = 0;
                return copy;
            }
            catch
            {
                copy.Dispose();
                throw;
            }
        }
    }
}
