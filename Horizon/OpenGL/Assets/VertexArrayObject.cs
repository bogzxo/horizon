using System.Runtime.CompilerServices;

using Horizon.Core.Primitives;
using Horizon.OpenGL.Descriptions;
using Horizon.OpenGL.Managers;

using Silk.NET.OpenGL;

namespace Horizon.OpenGL.Assets;

/// <summary>
/// How the GPU reads vertices: which buffers they come out of and what the attributes of a vertex are. Set up through
/// direct state access (OpenGL 4.5): nothing has to be bound to describe it, and once described, binding it is all a
/// draw has to do. The element buffer is part of it, it is never bound on its own.
/// <para>
/// Attributes come out of numbered bindings, each a buffer read at a stride (<see cref="SetVertexBuffer"/>), and
/// every attribute says which binding it reads from (<see cref="SetAttribute"/>). <see cref="SetLayout{T}"/> does
/// both for a struct that describes itself, see <see cref="IVertex"/>.
/// </para>
/// </summary>
public class VertexArrayObject : IGLObject
{
    public uint Handle { get; init; }

    public Dictionary<VertexArrayBufferAttachmentType, BufferObject> Buffers { get; init; } = [];

    public static VertexArrayObject Invalid { get; } = new VertexArrayObject { Handle = 0 };

    public BufferObject this[VertexArrayBufferAttachmentType type] => Buffers[type];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Bind() => ObjectManager.GL.BindVertexArray(Handle);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Unbind() => ObjectManager.GL.BindVertexArray(0);

    /// <summary>
    /// Makes a buffer the indices of the array. Part of the array's state from here on, bound along with it.
    /// </summary>
    public void SetElementBuffer(BufferObject buffer) => ObjectManager.GL.VertexArrayElementBuffer(Handle, buffer.Handle);

    /// <summary>
    /// Has a binding read a buffer, one vertex (or instance) every so many bytes.
    /// </summary>
    /// <param name="binding">The binding, which attributes refer to.</param>
    /// <param name="stride">How many bytes apart the vertices are.</param>
    /// <param name="offset">Where in the buffer the first one starts.</param>
    /// <param name="divisor">0 to step through the buffer once per vertex, 1 once per instance.</param>
    public void SetVertexBuffer(uint binding, BufferObject buffer, uint stride, nint offset = 0, uint divisor = 0)
    {
        ObjectManager.GL.VertexArrayVertexBuffer(Handle, binding, buffer.Handle, offset, stride);
        ObjectManager.GL.VertexArrayBindingDivisor(Handle, binding, divisor);
    }

    /// <summary>
    /// Describes one attribute: what it is made of, where it is in a vertex and which binding its vertices come out of.
    /// </summary>
    public void SetAttribute(uint index, uint binding, in VertexLayoutDescription attribute)
    {
        var gl = ObjectManager.GL;

        gl.EnableVertexArrayAttrib(Handle, index);

        if (attribute.IsInteger)
            gl.VertexArrayAttribIFormat(Handle, index, attribute.Count, (VertexAttribIType)attribute.Type, (uint)attribute.Offset);
        else
            gl.VertexArrayAttribFormat(Handle, index, attribute.Count, (VertexAttribType)attribute.Type, attribute.Normalized, (uint)attribute.Offset);

        gl.VertexArrayAttribBinding(Handle, index, binding);
    }

    /// <summary>
    /// Has a binding read a buffer of a struct, with every attribute the struct describes. The stride is the size
    /// of the struct, which has to be exactly what its attributes add up to.
    /// </summary>
    /// <param name="divisor">0 for a buffer of vertices, 1 for a buffer of instances.</param>
    public unsafe void SetLayout<T>(uint binding, BufferObject buffer, uint divisor = 0) where T : unmanaged, IVertex
    {
        int stride = sizeof(T);
        if (stride % 4 != 0)
            throw new InvalidOperationException($"The size of {typeof(T).Name} ({stride} bytes) isn't a multiple of four.");

        int described = 0;
        foreach (ref readonly VertexLayoutDescription attribute in T.GetLayout())
        {
            SetAttribute(attribute.Index, binding, in attribute);
            described += attribute.Size;
        }

        if (described != stride)
            throw new InvalidOperationException($"The attributes of {typeof(T).Name} add up to {described} bytes, the struct is {stride}: check the offsets of GetLayout().");

        SetVertexBuffer(binding, buffer, (uint)stride, 0, divisor);
    }
}
