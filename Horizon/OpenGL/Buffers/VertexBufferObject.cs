using Horizon.Content;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Descriptions;
using Horizon.OpenGL.Managers;

namespace Horizon.OpenGL.Buffers;

/// <summary>
/// A vertex array with the buffers most things want by name: the vertices, the indices, and if asked for a buffer of
/// instances and one of indirect draw commands. Drawing with it is binding it (one call, the element buffer is part
/// of the array) and drawing.
/// <code>
/// var mesh = VertexBufferObject.Create();
/// mesh.SetLayout&lt;Vertex2D&gt;();
/// mesh.VertexBuffer.Upload(vertices);
/// mesh.ElementBuffer.Upload(indices);
/// </code>
/// </summary>
public class VertexBufferObject
{
    /// <summary>The binding the vertex buffer is read through, see <see cref="VertexArrayObject.SetVertexBuffer"/>.</summary>
    public const uint VERTEX_BINDING = 0;

    /// <summary>The binding the instance buffer is read through.</summary>
    public const uint INSTANCE_BINDING = 1;

    public uint Handle => VertexArrayObject.Handle;

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

    /// <summary>
    /// Makes a vertex buffer with a buffer of instances besides its vertices and indices.
    /// </summary>
    public static VertexBufferObject CreateInstanced() => Create(VertexArrayObjectDescription.InstancedVertexBuffer);

    public VertexBufferObject(in VertexArrayObject vao)
    {
        VertexArrayObject = vao;

        VertexBuffer = vao.Buffers[VertexArrayBufferAttachmentType.ArrayBuffer];
        ElementBuffer = vao.Buffers[VertexArrayBufferAttachmentType.ElementBuffer];

        if (vao.Buffers.TryGetValue(VertexArrayBufferAttachmentType.IndirectBuffer, out BufferObject? indirect))
            IndirectBuffer = indirect;

        if (vao.Buffers.TryGetValue(VertexArrayBufferAttachmentType.AdditionalBuffer0, out BufferObject? instances))
            InstanceBuffer = instances;
    }

    public VertexBufferObject(AssetCreationResult<VertexArrayObject> result)
        : this(result.Asset) { }

    /// <summary>
    /// Lays the attributes of a vertex struct over the vertex buffer.
    /// </summary>
    public void SetLayout<T>() where T : unmanaged, IVertex =>
        VertexArrayObject.SetLayout<T>(VERTEX_BINDING, VertexBuffer);

    /// <summary>
    /// Lays the attributes of an instance struct over the instance buffer, read once per instance.
    /// </summary>
    public void SetInstanceLayout<T>() where T : unmanaged, IVertex =>
        VertexArrayObject.SetLayout<T>(INSTANCE_BINDING, InstanceBuffer ?? throw new InvalidOperationException("This vertex buffer has no instance buffer."), 1);

    /// <summary>
    /// Lays the attributes of an instance struct over a buffer of somebody else's (a simulation's, say), read once per instance.
    /// </summary>
    public void SetInstanceLayout<T>(BufferObject instances) where T : unmanaged, IVertex =>
        VertexArrayObject.SetLayout<T>(INSTANCE_BINDING, instances, 1);

    /// <summary>
    /// Binds the array, which is everything a draw needs: the vertex buffers it reads and the indices are part of it.
    /// </summary>
    public virtual void Bind() => ObjectManager.GL.BindVertexArray(Handle);

    public virtual void Unbind() => ObjectManager.GL.BindVertexArray(0);
}
