using System.Runtime.CompilerServices;

using Horizon.Core.Primitives;
using Horizon.OpenGL.Descriptions;
using Horizon.OpenGL.Managers;

namespace Horizon.OpenGL.Assets;

public class VertexArrayObject : IGLObject
{
    public uint Handle { get; init; }

    public Dictionary<VertexArrayBufferAttachmentType, BufferObject> Buffers { get; init; }

    public static VertexArrayObject Invalid { get; } = new VertexArrayObject { Handle = 0 };

    public BufferObject this[VertexArrayBufferAttachmentType type]
    {
        get => Buffers[type];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Bind() => ObjectManager.GL.BindVertexArray(Handle);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Unbind() => ObjectManager.GL.BindVertexArray(0);
}