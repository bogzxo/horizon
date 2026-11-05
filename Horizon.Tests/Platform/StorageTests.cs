using System.Text;

using Horizon.HIDL.Runtime;
using Horizon.Input;
using Horizon.Platform;
using Horizon.Tests.Content;
using Horizon.UI.Components;

namespace Horizon.Tests.Platform;

/// <summary>
/// Saves through platform storage. The disk's writes put a new save in place only once all of it is written, and the
/// engine's writers of settings, bindings and the recent lists go through storage and its commit, not round it.
/// </summary>
[Collection(ContentSourceCollection.NAME)]
public sealed class StorageTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "storage-tests-" + Guid.NewGuid().ToString("N"));
    private readonly IPlatformStorage before = PlatformStorage.Current;

    public void Dispose()
    {
        PlatformStorage.Current = before;
        try { Directory.Delete(folder, recursive: true); } catch { }
    }

    private string[] Leftovers() => Directory.Exists(folder) ? Directory.GetFiles(folder, "*.tmp", SearchOption.AllDirectories) : [];

    private static void Write(IStorageWrite write, string text) => write.Stream.Write(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void ACommittedSaveReplacesTheOldOneInAFolderItMakes()
    {
        var storage = new DiskStorage();
        string path = Path.Combine(folder, "deep", "er", "save.hor");

        PlatformStorage.Current = storage;
        PlatformStorage.WriteAllText(path, "first");
        PlatformStorage.WriteAllText(path, "second");

        Assert.Equal("second", File.ReadAllText(path));
        Assert.Equal("second", PlatformStorage.ReadAllText(path));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void ASaveThatIsntCommittedLeavesTheOldOneAndNoMess()
    {
        var storage = new DiskStorage();
        string path = Path.Combine(folder, "save.hor");
        PlatformStorage.Current = storage;
        PlatformStorage.WriteAllText(path, "kept");

        using (IStorageWrite write = storage.BeginWrite(path))
            Write(write, "half of a new");

        Assert.Equal("kept", File.ReadAllText(path));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void ASaveThatThrowsHalfWayLeavesTheOldOne()
    {
        var storage = new DiskStorage();
        string path = Path.Combine(folder, "save.hor");
        PlatformStorage.Current = storage;
        PlatformStorage.WriteAllText(path, "kept");

        Assert.Throws<InvalidOperationException>(void () =>
        {
            using IStorageWrite write = storage.BeginWrite(path);
            Write(write, "half");
            throw new InvalidOperationException("the disk filled up, say");
        });

        Assert.Equal("kept", File.ReadAllText(path));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void ASaveIsCommittedOnce()
    {
        var storage = new DiskStorage();
        string path = Path.Combine(folder, "save.hor");
        IStorageWrite write = storage.BeginWrite(path);
        Write(write, "once");
        write.Commit();

        Assert.Throws<InvalidOperationException>(write.Commit);
        write.Dispose();
        Assert.Throws<ObjectDisposedException>(write.Commit);
        Assert.Equal("once", File.ReadAllText(path));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void ACommitThatFailsLeavesTheSaveThatWasThere()
    {
        var storage = new MemoryStorage { FailCommits = true };
        storage.Saves["save.hor"] = "kept"u8.ToArray();
        PlatformStorage.Current = storage;

        Assert.Throws<IOException>(() => PlatformStorage.WriteAllText("save.hor", "new"));

        Assert.Equal("kept", PlatformStorage.ReadAllText("save.hor"));
        Assert.Equal(0, storage.Commits);
        Assert.Equal(1, storage.Abandoned);
    }

    [Fact]
    public void GamepadBindingsAreSavedAndLoadedThroughStorage()
    {
        var storage = new MemoryStorage();
        PlatformStorage.Current = storage;
        string path = Path.Combine(folder, "gamepads.hor");

        var saved = new GamepadInputManager();
        saved.AddVirtualGamepad("Pad", takeFreeSlot: true).Bindings.Bind("jump", GamepadInput.Y);
        saved.Save(path);

        Assert.Equal(1, storage.Commits);
        Assert.False(File.Exists(path));

        var loaded = new GamepadInputManager();
        Gamepad pad = loaded.AddVirtualGamepad("Pad", takeFreeSlot: true);
        Assert.True(loaded.Load(path, out var problems), string.Join(", ", problems));
        Assert.True(pad.Bindings.IsBound("jump", GamepadInput.Y));
    }

    [Fact]
    public void SettingsAreWrittenThroughStorageAndReadBack()
    {
        var storage = new MemoryStorage();
        PlatformStorage.Current = storage;
        string path = Path.Combine(folder, "settings.hor");
        UISetting[] settings = [new("volume", "Volume", UIFormKind.Number, "How loud")];
        var source = new FileSectionSource(path, "game", settings, new Dictionary<string, IRuntimeValue> { ["volume"] = new NumberValue(5) });

        source.Set("volume", new NumberValue(8));

        Assert.Equal(1, storage.Commits);
        Assert.False(File.Exists(path));
        Assert.Equal(8.0f, Assert.IsType<NumberValue>(source.Get("volume")).Value);
        Assert.True(source.IsModified("volume"));
        Assert.NotNull(SettingsSections.Read(path));
    }

    [Fact]
    public void TheRecentListIsKeptAndForgottenInStorage()
    {
        var storage = new MemoryStorage();
        PlatformStorage.Current = storage;
        string path = Path.Combine(folder, "recent.txt");

        var recent = new UIRecentList(path);
        recent.Remember("open");
        recent.Remember("save");

        Assert.Equal(["save", "open"], new UIRecentList(path).Items);
        Assert.False(File.Exists(path));

        recent.Clear();
        Assert.False(storage.Exists(path));
    }
}

/// <summary>Saves in memory, counting the commits, and when told to, failing every one.</summary>
internal sealed class MemoryStorage : IPlatformStorage
{
    public Dictionary<string, byte[]> Saves { get; } = [];
    public bool FailCommits { get; init; }
    public int Commits { get; private set; }
    public int Abandoned { get; private set; }

    public string Config(string name) => "config/" + name;
    public string Data(string name) => "data/" + name;
    public string State(string name) => "state/" + name;
    public string Cache(string name) => "cache/" + name;
    public bool Exists(string path) => Saves.ContainsKey(path);
    public Stream OpenRead(string path) => Saves.TryGetValue(path, out byte[]? bytes) ? new MemoryStream(bytes, writable: false) : throw new FileNotFoundException(path);
    public void Delete(string path) => Saves.Remove(path);
    public IStorageWrite BeginWrite(string path) => new Write(this, path);

    private sealed class Write(MemoryStorage storage, string path) : IStorageWrite
    {
        private readonly MemoryStream stream = new();
        private bool committed;
        public Stream Stream => stream;

        public void Commit()
        {
            if (storage.FailCommits) throw new IOException("no room");
            storage.Saves[path] = stream.ToArray();
            storage.Commits++;
            committed = true;
        }

        public void Dispose()
        {
            if (!committed) storage.Abandoned++;
        }
    }
}
