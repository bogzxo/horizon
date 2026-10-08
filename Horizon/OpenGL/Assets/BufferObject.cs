using System.Runtime.CompilerServices;

using Horizon.Core.Primitives;
using Horizon.OpenGL.Managers;

using Silk.NET.OpenGL;

namespace Horizon.OpenGL.Assets;

/// <summary>
/// A buffer on the GPU: vertices, indices, the instances of a draw, the storage a shader reads or a uniform block.
/// Everything here goes through direct state access (OpenGL 4.5), so nothing has to be bound to be filled or
/// mapped and nothing is left bound afterwards. The one thing that still binds is <see cref="BindBase"/>, which is
/// how a shader is told where a storage or uniform block lives.
/// <code>
/// var buffer = BufferObject.Create(BufferObjectDescription.ArrayBuffer);
/// buffer.Upload(vertices);
/// </code>
/// </summary>
public class BufferObject : GLObject, IDisposable
{
    public BufferTargetARB Type { get; init; }

    /// <summary>How many bytes the buffer holds, as of the last time it was given any.</summary>
    public nuint Size { get; private set; }

    /// <summary>Whether the storage of the buffer is immutable (see <see cref="Storage"/>), which is what every mapped buffer is.</summary>
    public bool IsImmutable { get; private set; }

    /// <summary>
    /// Makes a buffer the way a description says. Throws if the GPU won't have it.
    /// </summary>
    public static BufferObject Create(in Descriptions.BufferObjectDescription description) =>
        ObjectManager.Instance.Buffers.TryCreate(description, out var result)
            ? result.Asset
            : throw new InvalidOperationException(result.Message);

    /// <summary>
    /// Frees the buffer right now, for whoever doesn't want to wait for the scene to end. Render thread.
    /// </summary>
    public void Dispose()
    {
        ObjectManager.Instance.Buffers.Remove(this);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The alignment (in bytes) an offset into a storage buffer has to have to be bound as a range, see <see cref="BindRange"/>.
    /// </summary>
    public static uint StorageOffsetAlignment { get; private set; }

    /// <summary>
    /// The alignment (in bytes) an offset into a uniform buffer has to have to be bound as a range.
    /// </summary>
    public static uint UniformOffsetAlignment { get; private set; }

    /// <summary>
    /// Asks the GL what it needs, once it is there. Called by the object manager.
    /// </summary>
    internal static void ReadLimits()
    {
        StorageOffsetAlignment = (uint)Math.Max(1, GL.GetInteger(GetPName.ShaderStorageBufferOffsetAlignment));
        UniformOffsetAlignment = (uint)Math.Max(1, GL.GetInteger(GetPName.UniformBufferOffsetAlignment));
    }

    /* Giving the buffer its contents */

    /// <summary>
    /// Replaces whatever is in the buffer with new storage holding a copy of the data. The old storage is left to the
    /// driver (which is what makes this cheap for a buffer that is rewritten every frame while the GPU may still be
    /// reading the last one).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Upload<T>(ReadOnlySpan<T> data, BufferUsageARB usage = BufferUsageARB.DynamicDraw)
        where T : unmanaged
    {
        Size = (nuint)(data.Length * sizeof(T));
        GL.NamedBufferData(Handle, Size, data, usage);
    }

    /// <inheritdoc cref="Upload{T}(ReadOnlySpan{T}, BufferUsageARB)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Upload<T>(T[] data, BufferUsageARB usage = BufferUsageARB.DynamicDraw)
        where T : unmanaged => Upload<T>(data.AsSpan(), usage);

    /// <summary>
    /// Replaces whatever is in the buffer with new, empty storage of a size.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Allocate(nuint bytes, BufferUsageARB usage = BufferUsageARB.DynamicDraw)
    {
        Size = bytes;
        GL.NamedBufferData(Handle, bytes, null, usage);
    }

    /// <summary>
    /// Writes data over part of what is in the buffer, which has to be big enough for it already.
    /// </summary>
    /// <param name="byteOffset">Where in the buffer the data goes, in bytes.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Update<T>(ReadOnlySpan<T> data, nint byteOffset = 0)
        where T : unmanaged
    {
        if (data.IsEmpty) return;
        GL.NamedBufferSubData(Handle, byteOffset, (nuint)(data.Length * sizeof(T)), data);
    }

    /// <summary>
    /// Writes raw bytes over part of what is in the buffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Update(void* data, nuint bytes, nint byteOffset = 0) =>
        GL.NamedBufferSubData(Handle, byteOffset, bytes, data);

    /// <summary>
    /// Gives the buffer storage that is never resized again, which is what a buffer that is mapped for good needs
    /// (see <see cref="Map"/>) and what the factory does for a description that asks for storage.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Storage(nuint bytes, BufferStorageMask flags)
    {
        Size = bytes;
        IsImmutable = true;
        GL.NamedBufferStorage(Handle, bytes, null, flags);
    }

    /* Reading it back */

    /// <summary>
    /// Reads a stretch of the buffer back into memory. Slow (the GPU has to be caught up with), for tools and tests.
    /// </summary>
    public unsafe void Read<T>(Span<T> into, nint byteOffset = 0) where T : unmanaged
    {
        fixed (T* pointer = into)
            GL.GetNamedBufferSubData(Handle, byteOffset, (nuint)(into.Length * sizeof(T)), pointer);
    }

    /* Mapping */

    /// <summary>
    /// Maps a stretch of the buffer into memory. For a buffer with persistent storage the pointer stays good until
    /// <see cref="Unmap"/>, frames and all, see <see cref="Buffers.StreamBuffer{T}"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void* Map(nuint bytes, MapBufferAccessMask access, nint byteOffset = 0) =>
        GL.MapNamedBufferRange(Handle, byteOffset, bytes, access);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Unmap() => GL.UnmapNamedBuffer(Handle);

    /* Binding, for the shaders */

    /// <summary>
    /// Makes the whole buffer what a shader reads at an index: a storage block, a uniform block, an atomic counter.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BindBase(BufferTargetARB target, uint index) => GL.BindBufferBase(target, index, Handle);

    /// <summary>
    /// Makes part of the buffer what a shader reads at an index. The offset has to be a multiple of the alignment the
    /// target wants (<see cref="StorageOffsetAlignment"/>, <see cref="UniformOffsetAlignment"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BindRange(BufferTargetARB target, uint index, nint byteOffset, nuint bytes) =>
        GL.BindBufferRange(target, index, Handle, byteOffset, bytes);

    /// <summary>
    /// Binds the buffer to its target the old way, for the few calls that still go by what is bound (indirect
    /// draws, pixel transfers). Nothing that fills or maps a buffer needs this any more.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Bind() => GL.BindBuffer(Type, Handle);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Unbind() => GL.BindBuffer(Type, 0);
}
