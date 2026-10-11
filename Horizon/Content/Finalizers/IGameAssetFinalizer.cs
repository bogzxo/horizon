using Horizon.Graphics;

namespace Horizon.Content.Disposers;

/// <summary>
/// How a kind of asset is freed, one at a time or the lot at once.
/// </summary>
public interface IGameAssetFinalizer<AssetType>
    where AssetType : IGpuObject
{
    /// <summary>
    /// Frees a whole bunch of them in one go.
    /// </summary>
    public static abstract void DisposeAll(in IEnumerable<AssetType> assets);

    /// <summary>
    /// Frees one.
    /// </summary>
    public static abstract void Dispose(in AssetType asset);
}