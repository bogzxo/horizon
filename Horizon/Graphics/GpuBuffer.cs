using System.Runtime.CompilerServices;

using Horizon.Graphics.Vulkan;

namespace Horizon.Graphics;

/// <summary>
/// A buffer on the GPU, holding vertices, indices, the instances of a draw or the storage a shader reads. How it is written says
/// where it lives, see <see cref="BufferAccess"/>. Nothing has to be bound to fill one, and binding one for a shader is
/// one call (<see cref="BindStorage"/>).
/// <code>
/// var buffer = GpuBuffer.Create(BufferDescription.VertexBuffer);
/// buffer.Upload(vertices);
/// </code>
/// </summary>
public sealed unsafe class GpuBuffer : GpuResource, IDisposable
{
    private readonly GraphicsDevice device;

    internal Silk.NET.Vulkan.Buffer Buffer;
    internal VulkanMemory.Allocation Memory;

    /// <summary>The submission the buffer was last handed to, 0 for never. What the device goes by before it writes over it.</summary>
    internal ulong LastUse;

    /// <summary>How many bytes the buffer holds, as of the last time it was given any.</summary>
    public nuint Size { get; internal set; }

    /// <summary>How many bytes the memory behind it has room for, which can be more than <see cref="Size"/>.</summary>
    public nuint Capacity { get; internal set; }

    public BufferUsage Usage { get; }
    public BufferAccess Access { get; }

    /// <summary>Whether the buffer lives on the card and is written through commands rather than mapped, see <see cref="BufferAccess.Dynamic"/>.</summary>
    public bool IsImmutable => Access == BufferAccess.Dynamic;

    /// <summary>The Vulkan buffer, for whoever goes underneath. Changes when the buffer is given new memory.</summary>
    public Silk.NET.Vulkan.Buffer Native => Buffer;

    internal GpuBuffer(GraphicsDevice device, in BufferDescription description)
    {
        this.device = device;
        Usage = description.Usage;
        Access = description.Access;

        if (description.Size > 0) device.AllocateBuffer(this, description.Size);
    }

    /// <summary>Makes a buffer the way a description says. Throws if the GPU won't have it.</summary>
    protected override void Named()
    {
        if (Buffer.Handle != 0) device.LabelBuffer(this);
    }

    public static GpuBuffer Create(in BufferDescription description) =>
        ObjectManager.Instance.Buffers.TryCreate(description, out var result)
            ? result.Asset
            : throw new InvalidOperationException(result.Message);

    /// <summary>Frees the buffer right now, for whoever doesn't want to wait for the scene to end. Render thread.</summary>
    public void Dispose()
    {
        ObjectManager.Instance.Buffers.Remove(this);
        GC.SuppressFinalize(this);
    }

    /* Giving the buffer its contents */

    /// <summary>
    /// Replaces whatever is in the buffer with a copy of the data. A buffer the GPU may still be reading gets new
    /// memory and the old goes once the GPU is done with it, so this is safe to do every frame.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Upload<T>(ReadOnlySpan<T> data) where T : unmanaged
    {
        nuint bytes = (nuint)(data.Length * sizeof(T));
        device.AllocateBuffer(this, bytes);
        if (bytes == 0) return;

        fixed (T* pointer = data)
            device.FillBuffer(this, pointer, bytes);
    }

    /// <inheritdoc cref="Upload{T}(ReadOnlySpan{T})"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Upload<T>(T[] data) where T : unmanaged => Upload<T>(data.AsSpan());

    /// <summary>Replaces whatever is in the buffer with new, empty storage of a size.</summary>
    public void Allocate(nuint bytes) => device.AllocateBuffer(this, bytes);

    /// <summary>
    /// Writes data over part of what is in the buffer, which has to be big enough for it already. It happens in order
    /// with the draws, so whatever was drawn with the buffer before this reads what was there before.
    /// </summary>
    /// <param name="byteOffset">Where in the buffer the data goes, in bytes.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Update<T>(ReadOnlySpan<T> data, nint byteOffset = 0) where T : unmanaged
    {
        if (data.IsEmpty) return;

        fixed (T* pointer = data)
            device.UpdateBuffer(this, pointer, (nuint)(data.Length * sizeof(T)), byteOffset);
    }

    /// <summary>Writes raw bytes over part of what is in the buffer.</summary>
    public void Update(void* data, nuint bytes, nint byteOffset = 0) => device.UpdateBuffer(this, data, bytes, byteOffset);

    /* Reading it back */

    /// <summary>Reads a stretch of the buffer back into memory. Slow (the GPU has to be caught up with), for tools and tests.</summary>
    public void Read<T>(Span<T> into, nint byteOffset = 0) where T : unmanaged
    {
        fixed (T* pointer = into)
            device.ReadBuffer(this, pointer, (nuint)(into.Length * sizeof(T)), byteOffset);
    }

    /* Mapping */

    /// <summary>
    /// Where a buffer the CPU can see is in memory, for a <see cref="BufferAccess.Stream"/> buffer that is written
    /// straight into frame after frame (see <see cref="StreamBuffer{T}"/>). Null for one that lives on the card alone.
    /// The pointer is good until the buffer is given new memory.
    /// </summary>
    public void* Map(nuint bytes, nint byteOffset = 0)
    {
        if (Memory.Mapped == null) return null;
        if ((ulong)byteOffset + bytes > Capacity) throw new ArgumentOutOfRangeException(nameof(bytes));
        return Memory.Mapped + byteOffset;
    }

    public void Unmap()
    { }

    /* Binding, for the shaders */

    /// <summary>Makes the whole buffer what a shader reads at a storage binding (2 and up), see <see cref="GraphicsDevice.BindStorageBuffer"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BindStorage(uint index) => device.BindStorageBuffer(index, this);

    /// <summary>
    /// Makes part of the buffer what a shader reads at a storage binding. The offset has to be a multiple of
    /// <see cref="GraphicsDevice.StorageOffsetAlignment"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BindStorageRange(uint index, nint byteOffset, nuint bytes) => device.BindStorageBuffer(index, this, byteOffset, bytes);

    protected override void DestroyCore() => device.DestroyBuffer(this);
}
