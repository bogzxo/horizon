using Horizon.Content;
using Horizon.Content.Descriptions;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Descriptions;
using Horizon.OpenGL.Managers;

namespace Horizon.OpenGL.Factories;
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

        ObjectManager.GL.BindVertexArray(vaoHandle);

        foreach (var (type, desc) in description.Buffers)
        {
            if (!ObjectManager.Instance.Buffers.TryCreate(desc, out var buffer))
            {
                // make sure to free the VertexArrayObject
                ObjectManager.GL.BindVertexArray(0);
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
            else
            {
                buffers.Add(type, buffer.Asset);
            }

            buffer.Asset.Bind();
        }
        ObjectManager.GL.BindVertexArray(0);

        result = new AssetCreationResult<VertexArrayObject>
        {
            Asset = new() { Handle = vaoHandle, Buffers = buffers },
            Status = AssetCreationStatus.Success
        };
        return true;
    }
}