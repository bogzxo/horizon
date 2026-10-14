using Horizon.Graphics;

namespace Horizon.Content.Descriptions;

/// <summary>
/// Abstraction for the sake of style conformance, dont lecture me on the double generics, i know.
/// </summary>
public interface IAssetFactory<AssetType, DescriptionType>
    where AssetType : IGpuObject
    where DescriptionType : IAssetDescription
{
    public static abstract bool TryCreate(in DescriptionType description, out AssetCreationResult<AssetType> result);
}