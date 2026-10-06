using System.Collections.Concurrent;

using Bogz.Logging;

using Horizon.Content.Descriptions;
using Horizon.Content.Disposers;
using Horizon.Core.Primitives;

namespace Horizon.Content.Managers;

/// <summary>
/// A class build around creating, managing and disposing of game assets in a reliable thread-safe manner.
/// </summary>
public class AssetManager<AssetType, AssetFactoryType, AssetDescriptionType, AssetDisposerType>
    : IDisposable
    where AssetType : IGLObject
    where AssetDescriptionType : IAssetDescription
    where AssetFactoryType : IAssetFactory<AssetType, AssetDescriptionType>
    where AssetDisposerType : IGameAssetFinalizer<AssetType>
{
    protected Action<LogLevel, string> MessageCallback;

    protected readonly string assetName,
        name;

    /// <summary>
    /// All keyed assets.
    /// </summary>
    public ConcurrentDictionary<string, AssetType> NamedAssets { get; init; }

    /// <summary>
    /// All unnamed but managed assets.
    /// </summary>
    public List<AssetType> OwnedAssets { get; init; }

    /// <summary>
    /// Whether what is made here belongs to the scope it is made in (see <see cref="AssetScope"/>) and goes when
    /// that is released. Switched off for assets that are better kept for good once they exist: shaders take
    /// long to make and next to no memory to keep.
    /// </summary>
    public bool Scoped { get; set; } = true;

    // Which scope every unnamed asset belongs to, by its handle. What belongs to nobody isn't in here
    private readonly Dictionary<uint, AssetScope> owners = [];

    // Which scopes use every named asset, and the names that are used by somebody outside of any scope: those
    // are never freed for want of users
    private readonly Dictionary<string, HashSet<AssetScope>> users = [];
    private readonly HashSet<string> pinned = [];

    public AssetManager()
    {
        NamedAssets = new();
        OwnedAssets = new();

        assetName = typeof(AssetType).Name;
        name = $"{assetName}Manager";
    }

    /// <summary>
    /// Sets a delegate which will be called on events.
    /// </summary>
    /// <param name="callback"></param>
    public void SetMessageCallback(in Action<LogLevel, string> callback) =>
        this.MessageCallback = callback;

    public bool TryCreateOrGet(in string name, in AssetDescriptionType description, out AssetCreationResult<AssetType> result)
    {
        if (NamedAssets.TryGetValue(name, out AssetType value))
        {
            Retain(name);
            result = new()
            {
                Asset = value,
                Status = AssetCreationStatus.Success,
                Message = string.Empty,
            };
            return true;
        }

        return TryCreate(name, description, out result);
    }

    /// <summary>
    /// The handles of every asset alive right now. Together with <see cref="RemoveUnnamedExcept"/>
    /// this lets a caller free whatever was created after a point in time.
    /// </summary>
    public HashSet<uint> GetOwnedHandles() => OwnedAssets.Select(asset => asset.Handle).ToHashSet();

    /// <summary>
    /// Disposes every asset that isn't in <paramref name="keep"/>. Named assets are left alone:
    /// they are a cache shared by whoever asks for the name next.
    /// </summary>
    /// <returns>How many assets were disposed.</returns>
    public int RemoveUnnamedExcept(HashSet<uint> keep)
    {
        var named = NamedAssets.Values.Select(asset => asset.Handle).ToHashSet();
        var stale = OwnedAssets
            .Where(asset => !keep.Contains(asset.Handle) && !named.Contains(asset.Handle))
            .ToArray();

        foreach (var asset in stale)
        {
            owners.Remove(asset.Handle);
            AssetDisposerType.Dispose(asset);
            OwnedAssets.Remove(asset);
        }

        return stale.Length;
    }

    /// <summary>
    /// Creates a new named managed instance of an asset from a description.
    /// </summary>
    /// <returns>The newly created asset.</returns>
    public bool TryCreate(
        in string name,
        in AssetDescriptionType description,
        out AssetCreationResult<AssetType> result
    )
    {
        AssetFactoryType.TryCreate(description, out result);

        if (result.Status != AssetCreationStatus.Success)
        {
            MessageCallback?.Invoke(LogLevel.Error, $"[{name}] {result.Message}");
            return false;
        }

        MessageCallback?.Invoke(
            LogLevel.Info,
            $"[{name}] Successfully created {assetName} '{name}'!"
        );

        OwnedAssets.Add(result.Asset);

        if (!NamedAssets.TryAdd(name, OwnedAssets[OwnedAssets.Count - 1]))
            MessageCallback?.Invoke(LogLevel.Error, $"[{name}] Failed to add {assetName}!");
        else
            Retain(name);

        if (result.Status > 0 && result.Message?.CompareTo(string.Empty) != 0)
            MessageCallback?.Invoke(LogLevel.Info, $"[{name}] {result.Message}");

        return true;
    }

    /// <summary>
    /// Creates a new unnamed managed instance of an asset from a description.
    /// </summary>
    /// <returns>The newly created asset.</returns>
    public bool TryCreate(
        in AssetDescriptionType description,
        out AssetCreationResult<AssetType> result
        )
    {
        AssetFactoryType.TryCreate(description, out result);

        if (result.Status != AssetCreationStatus.Success)
        {
            MessageCallback?.Invoke(LogLevel.Error, $"[{name}] {result.Message}");
            return false;
        }

        MessageCallback?.Invoke(LogLevel.Info, $"[{name}] Successfully created a new {assetName}!");

        OwnedAssets.Add(result.Asset);
        Own(result.Asset);
        return true;
    }

    /// <summary>
    /// Helper method to note that an unnamed asset belongs to the scope it was made in, if any.
    /// </summary>
    private void Own(AssetType asset)
    {
        if (Scoped && AssetScope.Current is { } scope)
            owners[asset.Handle] = scope;
    }

    /// <summary>
    /// Helper method to note that a named asset is used by the scope it was asked for in, or by somebody outside
    /// of any scope, which keeps it for good.
    /// </summary>
    private void Retain(string name)
    {
        if (!Scoped || AssetScope.Current is not { } scope)
        {
            pinned.Add(name);
            return;
        }

        if (!users.TryGetValue(name, out var scopes))
            users[name] = scopes = [];

        scopes.Add(scope);
    }

    /// <summary>
    /// Frees what a scope has: every unnamed asset that was made in it, and every named asset that no other
    /// scope uses (and nobody outside of any scope ever asked for). GL thread, once nothing draws with any of it.
    /// </summary>
    /// <returns>How many assets were freed.</returns>
    public int Release(AssetScope scope)
    {
        int count = 0;

        var mine = owners.Where(owner => ReferenceEquals(owner.Value, scope)).Select(owner => owner.Key).ToHashSet();
        if (mine.Count > 0)
        {
            foreach (var asset in OwnedAssets.Where(asset => mine.Contains(asset.Handle)).ToArray())
            {
                AssetDisposerType.Dispose(asset);
                OwnedAssets.Remove(asset);
                count++;
            }

            foreach (uint handle in mine)
                owners.Remove(handle);
        }

        foreach (var (name, scopes) in users.ToArray())
        {
            if (!scopes.Remove(scope) || scopes.Count > 0)
                continue;

            users.Remove(name);
            if (!pinned.Contains(name) && Remove(name))
                count++;
        }

        return count;
    }

    /// <summary>
    /// Decorator method for managing an instance of an asset manually.
    /// </summary>
    public AssetType Add(AssetType asset)
    {
        OwnedAssets.Add(asset);
        Own(asset);
        return asset;
    }

    /// <summary>
    /// Removes an unnamed object.
    /// </summary>
    public bool Remove(AssetType asset)
    {
        // Its handle is free for the next asset to be given, which is not to inherit an owner. Before the
        // asset is disposed of, that may well be what clears the handle
        owners.Remove(asset.Handle);

        var named = NamedAssets.Where((item) => item.Value.Handle == asset.Handle).ToArray();
        AssetDisposerType.Dispose(asset);
        if (named.Length == 1)
        {
            _ = NamedAssets.TryRemove(named.FirstOrDefault().Key, out _);
            users.Remove(named[0].Key);
            pinned.Remove(named[0].Key);
        }

        return OwnedAssets.Remove(asset);
    }

    /// <summary>
    /// Removes an object via finding its reference through a handle.
    /// </summary>
    public bool Remove(uint handle)
    {
        var asset = OwnedAssets.Find((item) => item.Handle == handle);
        if (asset is null) return false;

        return Remove(asset);
    }

    /// <summary>
    /// Removes a named object from management.
    /// </summary>
    public bool Remove(string name)
    {
        if (NamedAssets.TryRemove(name, out var asset))
        {
            users.Remove(name);
            pinned.Remove(name);

            OwnedAssets.Remove(asset);
            AssetDisposerType.Dispose(asset);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Dispose method for children classes and extensions.
    /// </summary>
    /// <returns>Count of other assets disposed.</returns>
    protected virtual int DisposeOther()
    {
        return 0;
    }

    /// <summary>
    /// Disposes all managed assets.
    /// </summary>
    public void Dispose()
    {
        int count = OwnedAssets.Count + NamedAssets.Count + DisposeOther();

        AssetDisposerType.DisposeAll(OwnedAssets);
        AssetDisposerType.DisposeAll(NamedAssets.Values);

        OwnedAssets.Clear();
        NamedAssets.Clear();
        owners.Clear();
        users.Clear();
        pinned.Clear();

        MessageCallback?.Invoke(
            LogLevel.Info,
            $"[{name}] Successfully finalized {count} {assetName}s!"
        );

        GC.SuppressFinalize(this);
    }
}