using Horizon.Content;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Descriptions;
using Horizon.OpenGL.Managers;

namespace Horizon.OpenGL.Buffers;

//The vertex array object abstraction.
public class VertexBufferObject
{
    public uint Handle
    {
        get => VertexArrayObject.Handle;
    }

    public BufferObject VertexBuffer { get; init; }
    public BufferObject ElementBuffer { get; init; }

    public BufferObject? InstanceBuffer { get; init; }
    public BufferObject? IndirectBuffer { get; init; }

    public VertexArrayObject VertexArrayObject { get; init; }

    /// <summary>
    /// Makes a vertex buffer. Without a description that is one array buffer and one element buffer, which is what most things want.
    /// Throws if the GPU won't have it.
    /// </summary>
    public static VertexBufferObject Create(VertexArrayObjectDescription? description = null) =>
        ObjectManager.Instance.VertexArrays.TryCreate(description ?? VertexArrayObjectDescription.VertexBuffer, out var result)
            ? new VertexBufferObject(result.Asset)
            : throw new InvalidOperationException(result.Message);

    public VertexBufferObject(in VertexArrayObject vao)
    {
        this.VertexArrayObject = vao;

        VertexBuffer = vao.Buffers[VertexArrayBufferAttachmentType.ArrayBuffer];
        ElementBuffer = vao.Buffers[VertexArrayBufferAttachmentType.ElementBuffer];

        {
            if (vao.Buffers.TryGetValue(VertexArrayBufferAttachmentType.IndirectBuffer, out BufferObject? value))
                IndirectBuffer = value;
        }
        {
            if (vao.Buffers.TryGetValue(VertexArrayBufferAttachmentType.AdditionalBuffer0, out BufferObject? value))
                InstanceBuffer = value;
        }

        Bind();
        VertexBuffer.Bind();
        Unbind();
    }

    public VertexBufferObject(AssetCreationResult<VertexArrayObject> result)
        : this(result.Asset) { }

    public virtual void Bind()
    {
        // Binding the vertex array.
        ObjectManager.GL.BindVertexArray(Handle);
        VertexBuffer.Bind();
        ElementBuffer.Bind();
    }

    public virtual void Unbind()
    {
        // Unbinding the vertex array.
        ObjectManager.GL.BindVertexArray(0);
        VertexBuffer.Unbind();
        ElementBuffer.Unbind();
    }
}