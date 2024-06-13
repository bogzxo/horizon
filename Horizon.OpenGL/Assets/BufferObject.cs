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
    public unsafe void SetLayout<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicFields)] T>()
        where T : unmanaged
    {
        // get all fields
        var fields = typeof(T)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance) // read all fields and sort by index, we trust every property has the attribute.
            .OrderBy(p => p.GetCustomAttributes().OfType<VertexLayout>().First().Index)
            .ToArray(); // remember enumerate the array.

        // store queue of layout.
        var queue = new Queue<VertexLayoutDescription>();

        // iterate
        int totalSizeInBytes = 0;
        for (uint i = 0; i < fields.Length; i++)
        {
            var attribute =
                (fields[i].GetCustomAttribute(typeof(VertexLayout)) as VertexLayout)
                ?? throw new Exception("Undescribed property!");

            int count = fields[i].FieldType.IsPrimitive ? 1 : Math.Max(fields[i].FieldType.GetFields().Length, 1);
            int size = count * GetSizeFromVertexAttribPointerType(attribute.Type);

            queue.Enqueue(
                new VertexLayoutDescription
                {
                    Index = i,
                    Size = size,
                    Count = count,
                    Offset = totalSizeInBytes,
                    Type = attribute.Type,
                    Instanced = attribute.Instanced
                }
            );
            totalSizeInBytes += size;
        }

        if (totalSizeInBytes != sizeof(T))
            throw new Exception($"Size of {nameof(T)} doesn't match VertexLayout declarations!");

        if (totalSizeInBytes % 4 != 0)
            throw new Exception($"Size of {nameof(T)} doesn't align to 4 byte boundary!");

        while (queue.Count > 0)
        {
            var ptr = queue.Dequeue();

            if (ptr.Instanced) VertexAttributeDivisor(ptr.Index, 1);

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
                        (uint)totalSizeInBytes,
                        ptr.Offset
                    );
                    break;

                default:
                    VertexAttributePointer(
                        ptr.Index,
                        ptr.Count,
                        ptr.Type,
                        (uint)totalSizeInBytes,
                        ptr.Offset
                    );
                    break;
            }
        }
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
            VertexBufferObjectUsage.DynamicDraw
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
            VertexBufferObjectUsage.DynamicDraw
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
                VertexBufferObjectUsage.DynamicDraw
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
                VertexBufferObjectUsage.DynamicDraw
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