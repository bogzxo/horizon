using Horizon.Content;
using Horizon.Content.Descriptions;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Descriptions;
using Horizon.OpenGL.Managers;

namespace Horizon.OpenGL.Factories;

public class BufferObjectFactory : IAssetFactory<BufferObject, BufferObjectDescription>
{
    public static unsafe bool TryCreate(
        in BufferObjectDescription description,
        out AssetCreationResult<BufferObject> asset
    )
    {
        var buffer = new BufferObject
        {
            Handle = ObjectManager.GL.CreateBuffer(),
            Type = description.Type,
        };

        if (buffer.Handle == 0)
        {
            asset = new() { Asset = buffer, Status = AssetCreationStatus.Failed };
            return false;
        }

        if (description.IsStorageBuffer)
            buffer.Storage(description.Size, description.StorageMasks);
        else if (description.Size > 0)
            buffer.Allocate(description.Size);

        asset = new()
        {
            Asset = buffer,
            Status = AssetCreationStatus.Success,
            Message = description.IsStorageBuffer
                ? $"Storage buffer with size {description.Size} created!"
                : $"{description.Type} buffer created!"
        };

        return true;
    }
}