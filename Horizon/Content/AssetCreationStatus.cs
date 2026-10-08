using Horizon.Core.Primitives;

namespace Horizon.Content;

public enum AssetCreationStatus
{
    Failed = 0,
    Success = 1
}

public struct AssetCreationResult<AssetType>
    where AssetType : IGLObject
{
    public AssetType Asset { get; set; }
    public AssetCreationStatus Status { get; set; }
    public string Message { get; set; }
}