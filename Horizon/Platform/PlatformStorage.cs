using System.Text;

namespace Horizon.Platform;

/// <summary>The writable storage installed by the host before settings are loaded.</summary>
public static class PlatformStorage
{
    private static IPlatformStorage current = new DiskStorage();

    public static IPlatformStorage Current
    {
        get => Volatile.Read(ref current);
        set => Volatile.Write(ref current, value ?? throw new ArgumentNullException(nameof(value)));
    }

    public static string Config(string name) => Current.Config(name);
    public static string Data(string name) => Current.Data(name);
    public static string State(string name) => Current.State(name);
    public static string Cache(string name) => Current.Cache(name);
    public static bool Exists(string path) => Current.Exists(path);

    public static string ReadAllText(string path)
    {
        using var reader = new StreamReader(Current.OpenRead(path), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>Writes a whole save and commits it. If anything throws on the way the save that was there stays.</summary>
    public static void WriteAllText(string path, string text)
    {
        using IStorageWrite write = Current.BeginWrite(path);
        using (var writer = new StreamWriter(write.Stream, new UTF8Encoding(false), leaveOpen: true)) writer.Write(text);
        write.Commit();
    }
}
