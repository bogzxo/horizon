namespace Horizon.Graphics;

/// <summary>
/// A vertex array with the buffers most things want by name, the vertices, the indices, and if asked for a buffer of
/// instances. Drawing with it is binding it and drawing.
/// <code>
/// var mesh = VertexBuffer.Create();
/// mesh.SetLayout&lt;Vertex2D&gt;();
/// mesh.Vertices.Upload(vertices);
/// mesh.Indices.Upload(indices);
/// </code>
/// </summary>
public class VertexBuffer
{
    /// <summary>The binding the vertex buffer is read through, see <see cref="VertexArray.SetVertexBuffer"/>.</summary>
    public const uint VERTEX_BINDING = 0;

    /// <summary>The binding the instance buffer is read through.</summary>
    public const uint INSTANCE_BINDING = 1;

    public uint Handle => VertexArray.Handle;

    public GpuBuffer Vertices { get; }
    public GpuBuffer Indices { get; }
    public GpuBuffer? Instances { get; }

    public VertexArray VertexArray { get; }

    /// <summary>
    /// Makes a vertex buffer. Without a description that is one buffer of vertices and one of indices, which is what most things want.
    /// Throws if the GPU won't have it.
    /// </summary>
    public static VertexBuffer Create(VertexArrayDescription? description = null) =>
        new(VertexArray.Create(description ?? VertexArrayDescription.VertexBuffer));

    /// <summary>Makes a vertex buffer with a buffer of instances besides its vertices and indices.</summary>
    public static VertexBuffer CreateInstanced() => Create(VertexArrayDescription.InstancedVertexBuffer);

    public VertexBuffer(VertexArray array)
    {
        VertexArray = array;
        Vertices = array.Buffers[VertexArraySlot.Vertices];
        Indices = array.Buffers[VertexArraySlot.Indices];

        if (array.Buffers.TryGetValue(VertexArraySlot.Instances, out GpuBuffer? instances))
            Instances = instances;
    }

    /// <summary>Lays the attributes of a vertex struct over the vertex buffer.</summary>
    public void SetLayout<T>() where T : unmanaged, IVertex => VertexArray.SetLayout<T>(VERTEX_BINDING, Vertices);

    /// <summary>Lays the attributes of an instance struct over the instance buffer, read once per instance.</summary>
    public void SetInstanceLayout<T>() where T : unmanaged, IVertex =>
        VertexArray.SetLayout<T>(INSTANCE_BINDING, Instances ?? throw new InvalidOperationException("This vertex buffer has no instance buffer."), 1);

    /// <summary>Lays the attributes of an instance struct over a buffer of somebody else's (a simulation's, say), read once per instance.</summary>
    public void SetInstanceLayout<T>(GpuBuffer instances) where T : unmanaged, IVertex => VertexArray.SetLayout<T>(INSTANCE_BINDING, instances, 1);

    /// <summary>Binds the array, which is everything a draw needs, the vertex buffers it reads and the indices are part of it.</summary>
    public virtual void Bind() => VertexArray.Bind();

    public virtual void Unbind() => VertexArray.Unbind();
}
