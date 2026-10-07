using System.Collections.Concurrent;

using Bogz.Logging;

using Horizon.Content.Descriptions;
using Horizon.Content.Disposers;
using Horizon.Core.Primitives;

namespace Horizon.Content.Managers;

/// <summary>
/// A class build around creating, managing and disposing of game assets in a reliable thread-safe manner.
/// <para>
/// What it keeps track of is safe to get at from any thread, but the assets themselves are on the GPU: they are made
/// and freed on the thread that draws, and it says so (once) in the log when that isn't where it's asked to.
/// </para>
/// </summary>
public class AssetManager<AssetType, AssetFactoryType, AssetDescriptionType, AssetDisposerType>
    : IDisposable
    where AssetType : class, IGLObject
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

    // Everything above, and the two lists that are out in the open, are only ever touched while holding this
    private readonly object sync = new();

    // Whether it was said already that something was made or freed off the thread that draws, which only fills the log the second time
    private bool warnedThread;

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

    /// <summary>
    /// Makes an asset that belongs to whoever asked for it. Null if it couldn't be made, and why is in the log.
    /// </summary>
    public AssetType? Create(in AssetDescriptionType description) =>
        TryCreate(description, out var result) ? result.Asset : null;

    /// <summary>
    /// The asset of a name, made the first time somebody asks for it and shared by everybody who asks after.
    /// Null if it couldn't be made, and why is in the log.
    /// </summary>
    public AssetType? CreateOrGet(string name, in AssetDescriptionType description) =>
        TryCreateOrGet(name, description, out var result) ? result.Asset : null;

    public bool TryCreateOrGet(in string name, in AssetDescriptionType description, out AssetCreationResult<AssetType> result)
    {
        lock (sync)
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
    }

    /// <summary>
    /// The handles of every asset alive right now. Together with <see cref="RemoveUnnamedExcept"/>
    /// this lets a caller free whatever was created after a point in time.
    /// </summary>
    public HashSet<uint> GetOwnedHandles()
    {
        lock (sync) return OwnedAssets.Select(asset => asset.Handle).ToHashSet();
    }

    /// <summary>
    /// Disposes every asset that isn't in <paramref name="keep"/>. Named assets are left alone:
    /// they are a cache shared by whoever asks for the name next.
    /// </summary>
    /// <returns>How many assets were disposed.</returns>
    public int RemoveUnnamedExcept(HashSet<uint> keep)
    {
        lock (sync)
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
        lock (sync)
        {
            CheckThread();
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
        lock (sync)
        {
            CheckThread();
            AssetFactoryType.TryCreate(description, out result);

            if (result.Status != AssetCreationStatus.Success)
            {
                MessageCallback?.Invoke(LogLevel.Error, $"[{name}] {result.Message}");
                return false;
            }

            OwnedAssets.Add(result.Asset);
            Own(result.Asset);
            return true;
        }
    }

    /// <summary>
    /// Helper method to say (once) that an asset is being made somewhere other than on the thread that draws, which
    /// is the only one with the GPU: whatever is made there is broken in ways that are a pain in the arse to track down.
    /// </summary>
    private void CheckThread()
    {
        if (warnedThread || !Horizon.Core.EntityLifecycle.IsOffRenderThread)
            return;

        warnedThread = true;
        MessageCallback?.Invoke(LogLevel.Warning, $"[{name}] A {assetName} is being made on '{Thread.CurrentThread.Name ?? "a thread with no name"}', which isn't the thread that draws. Only that one has the GPU.");
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
        lock (sync)
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
    }

    /// <summary>
    /// Decorator method for managing an instance of an asset manually.
    /// </summary>
    public AssetType Add(AssetType asset)
    {
        lock (sync)
        {
            OwnedAssets.Add(asset);
            Own(asset);
            return asset;
        }
    }

    /// <summary>
    /// Removes an unnamed object.
    /// </summary>
    public bool Remove(AssetType asset)
    {
        lock (sync)
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
    }

    /// <summary>
    /// Removes an object via finding its reference through a handle.
    /// </summary>
    public bool Remove(uint handle)
    {
        lock (sync)
        {
            var asset = OwnedAssets.Find((item) => item.Handle == handle);
            if (asset is null) return false;

            return Remove(asset);
        }
    }

    /// <summary>
    /// Removes a named object from management.
    /// </summary>
    public bool Remove(string name)
    {
        lock (sync)
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
        lock (sync)
        {
            // Everything that has a name is among the owned ones as well. Freeing those twice is an error as far as the GPU is concerned
            var all = OwnedAssets.Concat(NamedAssets.Values).DistinctBy(asset => asset.Handle).ToArray();
            int count = all.Length + DisposeOther();

            AssetDisposerType.DisposeAll(all);

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
}