using Horizon.Content.Descriptions;

namespace Horizon.Graphics;

/// <summary>Which of the buffers of a vertex array a buffer is, see <see cref="VertexBuffer"/>.</summary>
public enum VertexArraySlot
{
    Vertices,
    Indices,
    Indirect,
    Instances
}

/// <summary>
/// The buffers a vertex array is made with. The array only describes how they are read, see <see cref="VertexArray"/>.
/// </summary>
public readonly struct VertexArrayDescription : IAssetDescription
{
    public Dictionary<VertexArraySlot, BufferDescription> Buffers { get; init; }

    /// <summary>Vertices and indices, which is what most things want.</summary>
    public static VertexArrayDescription VertexBuffer { get; } = new()
    {
        Buffers = new()
        {
            { VertexArraySlot.Vertices, BufferDescription.VertexBuffer },
            { VertexArraySlot.Indices, BufferDescription.IndexBuffer }
        }
    };

    /// <summary>Vertices, indices and a buffer of instances besides.</summary>
    public static VertexArrayDescription InstancedVertexBuffer { get; } = new()
    {
        Buffers = new()
        {
            { VertexArraySlot.Vertices, BufferDescription.VertexBuffer },
            { VertexArraySlot.Indices, BufferDescription.IndexBuffer },
            { VertexArraySlot.Instances, BufferDescription.VertexBuffer }
        }
    };

    /// <summary>No buffers at all, for what makes its vertices up in the shader.</summary>
    public static VertexArrayDescription Empty { get; } = new() { Buffers = [] };
}
