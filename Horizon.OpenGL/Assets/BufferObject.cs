using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;

using Horizon.Core.Data;
using Horizon.Core.Primitives;
using Horizon.OpenGL.Managers;

using Silk.NET.OpenGL;

namespace Horizon.OpenGL.Assets;

/* This is an abstraction for a buffer object */

public class BufferObject : GLObject
{
    public BufferTargetARB Type { get; init; }
    public uint Size { get; init; }

    public static long ALIGNMENT = 0;

    static BufferObject()
    {
        ALIGNMENT = GL.GetInteger64(GetPName.MinMapBufferAlignment);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public virtual unsafe void BufferData<T>(in ReadOnlySpan<T> data)
        where T : unmanaged
    {
        Bind();
        // FIXME cross static ref to BaseGameEngine

        GL.BufferData(Type, (nuint)(data.Length * sizeof(T)), data, BufferUsageARB.DynamicDraw);

        // FIXME cross static ref to BaseGameEngine
        GL.BindBuffer(Type, 0);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public virtual unsafe void BufferData<T>(in uint size)
        where T : unmanaged
    {
        Bind();
        // FIXME cross static ref to BaseGameEngine

        GL.BufferData(Type, (nuint)(size * sizeof(T)), null, BufferUsageARB.DynamicDraw);

        // FIXME cross static ref to BaseGameEngine
        GL.BindBuffer(Type, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public virtual unsafe void BufferSubData<T>(in ReadOnlySpan<T> data, int offset = 0)
        where T : unmanaged
    {
        Bind();

        // FIXME cross static ref to BaseGameEngine
        GL.BufferSubData(Type, offset, (nuint)(sizeof(T) * data.Length), data);

        // FIXME cross static ref to BaseGameEngine
        GL.BindBuffer(Type, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public virtual unsafe void BufferSubData<T>(in T[] data, int offset = 0)
        where T : unmanaged
    {
        Bind();

        fixed (void* d = data)
        {
            // FIXME cross static ref to BaseGameEngine
            GL.BufferSubData(Type, offset, (nuint)(sizeof(T) * data.Length), d);
        }

        // FIXME cross static ref to BaseGameEngine
        GL.BindBuffer(Type, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe T GetSubData<T>(uint offset, uint size)
        where T : unmanaged
    {
        return GL.GetNamedBufferSubData<T>(Handle, (nint)(offset * sizeof(T)), (nuint)(size * sizeof(T)));
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe T GetSubData<T>(uint size)
        where T : unmanaged
    {
        return GL.GetNamedBufferSubData<T>(Handle, 0, (nuint)(size * sizeof(T)));
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe T GetSubData<T>()
        where T : unmanaged
    {
        return GL.GetNamedBufferSubData<T>(Handle, 0, (nuint)(Size * sizeof(T)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public virtual unsafe void BufferData<T>(in T[] data)
        where T : unmanaged
    {
        Bind();

        fixed (void* d = data)
        {
            // FIXME cross static ref to BaseGameEngine
            GL.BufferData(Type, (nuint)(data.Length * sizeof(T)), d, BufferUsageARB.DynamicDraw);
        }
        // FIXME cross static ref to BaseGameEngine
        GL.BindBuffer(Type, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void VertexAttributePointer(
        uint index,
        int count,
        VertexAttribPointerType type,
        uint vertexSize,
        int offSet
    )
    {
        ObjectManager
            .GL
            .VertexAttribPointer(index, count, type, false, vertexSize, (void*)(offSet));
        ObjectManager.GL.EnableVertexAttribArray(index);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void VertexAttributeIPointer(
        uint index,
        int count,
        VertexAttribIType type,
        uint vertexSize,
        int offSet
    )
    {
        ObjectManager
            .GL
            .VertexAttribIPointer(index, count, type, vertexSize, (void*)(offSet));
        ObjectManager.GL.EnableVertexAttribArray(index);
    }

    private readonly struct VertexLayoutDescription
    {
        public readonly uint Index { get; init; }
        public readonly int Size { get; init; }
        public readonly int Count { get; init; }
        public readonly int Offset { get; init; }
        public readonly bool Instanced { get; init; }
        public readonly VertexAttribPointerType Type { get; init; }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void SetLayout<T>() where T : unmanaged, IVertex
    {
        // Because T is unmanaged, sizeof(T) works perfectly at compile time
        int totalSizeInBytes = sizeof(T);

        if (totalSizeInBytes % 4 != 0)
            throw new Exception($"Size of {nameof(T)} doesn't align to a 4-byte boundary!");

        // Pull the layout directly from the struct type without reflection
        var layout = T.GetLayout();

        int calculatedSize = 0;

        foreach (ref readonly var ptr in layout)
        {
            if (ptr.Instanced)
                VertexAttributeDivisor(ptr.Index, 1);

            switch (ptr.Type)
            {
                case VertexAttribPointerType.Int:
                case VertexAttribPointerType.Byte:
                case VertexAttribPointerType.UnsignedByte:
                case VertexAttribPointerType.Short:
                case VertexAttribPointerType.UnsignedShort:
                case VertexAttribPointerType.UnsignedInt:
                    VertexAttributeIPointer(
                        ptr.Index,
                        ptr.Count,
                        (VertexAttribIType)ptr.Type,
                        (uint)totalSizeInBytes, // Total stride
                        ptr.Offset
                    );
                    break;

                default:
                    VertexAttributePointer(
                        ptr.Index,
                        ptr.Count,
                        ptr.Type,
                        (uint)totalSizeInBytes, // Total stride
                        ptr.Offset
                    );
                    break;
            }

            calculatedSize += ptr.Size;
        }

        if (calculatedSize != totalSizeInBytes)
            throw new Exception($"Calculated size ({calculatedSize}) of {nameof(T)} doesn't match struct size ({totalSizeInBytes})! Check your GetLayout() offsets.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetSizeFromVertexAttribPointerType(in VertexAttribPointerType type)
    {
        return type switch
        {
            VertexAttribPointerType.Double => sizeof(double),

            VertexAttribPointerType.Float => sizeof(float),
            VertexAttribPointerType.Int => sizeof(int),
            VertexAttribPointerType.UnsignedInt => sizeof(uint),

            VertexAttribPointerType.UnsignedShort => sizeof(uint),
            VertexAttribPointerType.HalfFloat => sizeof(float) / 2,
            _ => 0
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void VertexAttributeDivisor(uint index, uint divisor)
    {
        ObjectManager.GL.VertexAttribDivisor(index, divisor);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public virtual void Bind()
    {
        /* Binding the buffer object, with the correct buffer type.
         */
        // FIXME cross static ref to BaseGameEngine
        GL.BindBuffer(Type, Handle);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public virtual unsafe void NamedBufferData<T>(in ReadOnlySpan<T> data)
        where T : unmanaged
    {
        // FIXME cross static ref to BaseGameEngine
        GL.NamedBufferData(
            Handle,
            (nuint)(data.Length * sizeof(T)),
            data,
            BufferUsageARB.DynamicDraw
        );
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public virtual unsafe void NamedBufferData(in nuint size)
    {
        // FIXME cross static ref to BaseGameEngine
        GL.NamedBufferData(
            Handle,
            (nuint)(size),
            null,
            BufferUsageARB.DynamicDraw
        );
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public virtual unsafe void NamedBufferSubData<T>(in ReadOnlySpan<T> data, int offset = 0, int length = 0)
        where T : unmanaged
    {
        // FIXME cross static ref to BaseGameEngine
        GL.NamedBufferSubData(Handle, offset, (nuint)(length > 0 ? length : (sizeof(T) * data.Length)), data);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public virtual unsafe void NamedBufferSubData(in void* data, int length, int offset = 0)
    {
        // FIXME cross static ref to BaseGameEngine
        GL.NamedBufferSubData(Handle, offset, (nuint)(length), data);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public virtual unsafe void NamedBufferSubData<T>(in T[] data, int length = 0)
        where T : unmanaged
    {
        fixed (void* d = data)
        {
            GL.NamedBufferData(
                Handle,
                (nuint)(length > 0 ? length : (sizeof(T) * data.Length)),
                d,
                BufferUsageARB.DynamicDraw
            );
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public virtual unsafe void NamedBufferData<T>(in T[] data)
        where T : unmanaged
    {
        fixed (void* d = data)
        {
            GL.NamedBufferData(
                Handle,
                (nuint)(sizeof(T) * data.Length),
                d,
                BufferUsageARB.DynamicDraw
            );
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void* MapBufferRange(int size, MapBufferAccessMask access) =>
        MapBufferRange((uint)size, access);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void* MapBufferRange(uint length, MapBufferAccessMask access)
    {
        //int length = (int)(Math.Round((size) / (double)ALIGNMENT) * (double)ALIGNMENT + ALIGNMENT);

        return GL.MapNamedBufferRange(Handle, 0, length, access);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UnmapBuffer()
    {
        GL.UnmapNamedBuffer(Handle);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public virtual void Unbind()
    {
        GL.BindBuffer(Type, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void BufferStorage(
        uint size,
        BufferStorageMask masks =
            BufferStorageMask.MapPersistentBit
            | BufferStorageMask.MapCoherentBit
            | BufferStorageMask.MapWriteBit
    )
    {
        GL.NamedBufferStorage(Handle, (nuint)(size), null, masks);
    }
}