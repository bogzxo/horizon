using Horizon.Content.Descriptions;

namespace Horizon.Graphics;

/// <summary>
/// What a buffer is for and how it is written, see <see cref="BufferUsage"/> and <see cref="BufferAccess"/>. A size of
/// 0 makes a buffer with no memory yet, which gets some the first time it is given anything.
/// </summary>
public readonly record struct BufferDescription(BufferUsage Usage, BufferAccess Access = BufferAccess.Static, nuint Size = 0) : IAssetDescription
{
    /// <summary>Vertices, filled once.</summary>
    public static BufferDescription VertexBuffer { get; } = new(BufferUsage.Vertex);

    /// <summary>Indices, filled once.</summary>
    public static BufferDescription IndexBuffer { get; } = new(BufferUsage.Index);

    /// <summary>A storage block shaders read (and write), updated in parts from the CPU.</summary>
    public static BufferDescription StorageBuffer(nuint size) => new(BufferUsage.Storage | BufferUsage.Vertex, BufferAccess.Dynamic, size);

    /// <summary>Indirect draw commands.</summary>
    public static BufferDescription IndirectBuffer { get; } = new(BufferUsage.Indirect, BufferAccess.Dynamic);
}
