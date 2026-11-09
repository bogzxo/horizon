namespace Horizon.Platform;

/// <summary>Writable host storage, separate from packaged content.</summary>
public interface IPlatformStorage
{
    string Config(string name);
    string Data(string name);
    string State(string name);
    string Cache(string name);
    bool Exists(string path);
    Stream OpenRead(string path);
    IStorageWrite BeginWrite(string path);

    /// <summary>Takes a save away. Nothing happens when it isn't there.</summary>
    void Delete(string path);
}

/// <summary>A pending save. Dispose without committing leaves the previous save intact.</summary>
public interface IStorageWrite : IDisposable
{
    Stream Stream { get; }
    void Commit();
}
