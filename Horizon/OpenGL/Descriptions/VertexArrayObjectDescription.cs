using Horizon.Content.Descriptions;

namespace Horizon.OpenGL.Descriptions;

public readonly struct VertexArrayObjectDescription : IAssetDescription
{
    public Dictionary<
        VertexArrayBufferAttachmentType,
        BufferObjectDescription
    > Buffers
    { get; init; }

    /// <summary>
    /// A vertex buffer with a buffer of instances besides its vertices and indices, see <c>VertexBufferObject.InstanceBuffer</c>.
    /// </summary>
    public static VertexArrayObjectDescription InstancedVertexBuffer { get; } =
        new()
        {
            Buffers = new()
            {
                { VertexArrayBufferAttachmentType.ElementBuffer, BufferObjectDescription.ElementArrayBuffer },
                { VertexArrayBufferAttachmentType.ArrayBuffer, BufferObjectDescription.ArrayBuffer },
                { VertexArrayBufferAttachmentType.AdditionalBuffer0, BufferObjectDescription.ArrayBuffer }
            }
        };

    /// <summary>
    /// A standard configuration of a vertex buffer with one array buffer and one element buffer.
    /// </summary>
    public static VertexArrayObjectDescription VertexBuffer { get; } =
        new()
        {
            Buffers = new()
            {
                {
                    VertexArrayBufferAttachmentType.ElementBuffer,
                    BufferObjectDescription.ElementArrayBuffer
                },
                {
                    VertexArrayBufferAttachmentType.ArrayBuffer,
                    BufferObjectDescription.ArrayBuffer
                },
            }
        };
}