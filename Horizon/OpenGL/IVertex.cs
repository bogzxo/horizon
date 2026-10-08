using Silk.NET.OpenGL;

namespace Horizon.OpenGL;

/// <summary>
/// One attribute of a vertex: where it is in the shader (<see cref="Index"/>), what it is made of and where it sits
/// in the struct. See <see cref="IVertex"/>.
/// </summary>
public readonly struct VertexLayoutDescription
{
    /// <summary>The location the shader reads the attribute at.</summary>
    public uint Index { get; init; }

    /// <summary>How many bytes the attribute takes in the struct.</summary>
    public int Size { get; init; }

    /// <summary>How many components it has, 1 to 4.</summary>
    public int Count { get; init; }

    /// <summary>Where it starts in the struct, in bytes.</summary>
    public int Offset { get; init; }

    /// <summary>
    /// Whether it changes per instance rather than per vertex. Only read by <see cref="Buffers.VertexBufferObject.SetLayout{T}"/>
    /// for a layout that is laid over the vertex buffer; a buffer of instances says so for all of its attributes at once.
    /// </summary>
    public bool Instanced { get; init; }

    public VertexAttribPointerType Type { get; init; }

    /// <summary>
    /// Whether an integer attribute is handed to the shader as a float from 0 to 1 (or -1 to 1). Colours packed into
    /// bytes want this; an attribute the shader reads as an int never goes through here.
    /// </summary>
    public bool Normalized { get; init; }

    /// <summary>Whether the shader reads the attribute as an integer (<c>int</c>, <c>uint</c>, <c>ivec</c>) rather than a float.</summary>
    public bool IsInteger => !Normalized && Type is VertexAttribPointerType.Int or VertexAttribPointerType.UnsignedInt
        or VertexAttribPointerType.Short or VertexAttribPointerType.UnsignedShort
        or VertexAttribPointerType.Byte or VertexAttribPointerType.UnsignedByte;

    /// <summary>A float attribute of a few components.</summary>
    public static VertexLayoutDescription Float(uint index, int count, int offset, bool instanced = false) => new()
    {
        Index = index,
        Count = count,
        Size = sizeof(float) * count,
        Offset = offset,
        Type = VertexAttribPointerType.Float,
        Instanced = instanced
    };

    /// <summary>An unsigned integer attribute of a few components.</summary>
    public static VertexLayoutDescription UInt(uint index, int count, int offset, bool instanced = false) => new()
    {
        Index = index,
        Count = count,
        Size = sizeof(uint) * count,
        Offset = offset,
        Type = VertexAttribPointerType.UnsignedInt,
        Instanced = instanced
    };
}

/// <summary>
/// A struct that can say how it is laid out for the GPU, so a vertex array can be set up from the type alone:
/// <code>
/// public static ReadOnlySpan&lt;VertexLayoutDescription&gt; GetLayout() =>
/// [
///     VertexLayoutDescription.Float(0, 2, 0),
///     VertexLayoutDescription.Float(1, 2, sizeof(float) * 2)
/// ];
/// </code>
/// </summary>
public interface IVertex
{
    static abstract ReadOnlySpan<VertexLayoutDescription> GetLayout();
}
