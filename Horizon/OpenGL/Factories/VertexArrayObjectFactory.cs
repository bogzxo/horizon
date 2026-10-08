using Horizon.Content;
using Horizon.Content.Descriptions;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Descriptions;
using Horizon.OpenGL.Managers;

namespace Horizon.OpenGL.Factories;

/// <summary>
/// Makes a vertex array and the buffers a description says go with it. The element buffer (if there is one) is made
/// part of the array; the rest are only made, what the array reads out of them is said with
/// <see cref="VertexArrayObject.SetLayout{T}"/> or <see cref="VertexArrayObject.SetVertexBuffer"/>. Nothing is bound along the way.
/// </summary>
public class VertexArrayObjectFactory
    : IAssetFactory<VertexArrayObject, VertexArrayObjectDescription>
{
    public static bool TryCreate(
        in VertexArrayObjectDescription description,
        out AssetCreationResult<VertexArrayObject> result
    )
    {
        uint vaoHandle = ObjectManager.GL.CreateVertexArray();

        Dictionary<VertexArrayBufferAttachmentType, BufferObject> buffers = new();

        foreach (var (type, desc) in description.Buffers)
        {
            if (!ObjectManager.Instance.Buffers.TryCreate(desc, out var buffer))
            {
                // make sure to free the VertexArrayObject
                ObjectManager.GL.DeleteVertexArray(vaoHandle);

                // incase the first buffer wasnt the one to fail.
                foreach (var (_, item) in buffers)
                    ObjectManager.Instance.Buffers.Remove(item);
                buffers.Clear();

                result = new AssetCreationResult<VertexArrayObject>
                {
                    Asset = new(),
                    Status = AssetCreationStatus.Failed,
                    Message = $"Failed to create and attach BufferObject[{type}] to VAO!"
                };
                return false;
            }

            buffers.Add(type, buffer.Asset);

            if (type == VertexArrayBufferAttachmentType.ElementBuffer)
                ObjectManager.GL.VertexArrayElementBuffer(vaoHandle, buffer.Asset.Handle);
        }

        result = new AssetCreationResult<VertexArrayObject>
        {
            Asset = new() { Handle = vaoHandle, Buffers = buffers },
            Status = AssetCreationStatus.Success
        };
        return true;
    }
}
