using Horizon.Logging;

namespace Horizon.Graphics;

/// <summary>
/// A buffer for what is written anew every frame and read by the GPU as it is drawn, the quads of a sprite batch,
/// the shapes of a primitive renderer, the particles of a simulation on the CPU.
/// <para>
/// It is mapped once and for good, and split into three regions that take turns, so the frame being written never
/// touches the one the GPU is still reading. <see cref="Begin"/> waits for the GPU to be done with the region it is
/// about to hand out (which it nearly always is, two frames on), <see cref="End"/> drops a fence behind the frame and
/// moves on. Writing into the span is writing straight into memory the GPU reads, so it is write only, reading it
/// back is slow and reads whatever the GPU is doing.
/// </para>
/// <code>
/// Span&lt;SpriteItem&gt; items = stream.Begin(count);
/// // fill it in
/// stream.BindRange(3);
/// // draw
/// stream.End();
/// </code>
/// The region of the frame is bound as a range, so a shader indexes it from zero, and a draw's instances start at
/// <see cref="Offset"/> for whoever draws with instanced attributes out of it instead. The buffer grows when a frame
/// needs more than fits, which waits for the GPU and makes a new one, see <see cref="Capacity"/>.
/// </summary>
public sealed unsafe class StreamBuffer<T> : IDisposable where T : unmanaged
{
    /// <summary>How many regions take turns. The CPU writes one while the GPU may still be reading the other two.</summary>
    public const int REGIONS = 3;

    // How much more than was asked for is made, so a count that wavers from frame to frame doesn't remake the buffer every time
    private const float HEADROOM = 1.5f;

    // The longest (in nanoseconds) to wait for the GPU before carrying on regardless
    private const ulong LONGEST_WAIT = 1_000_000_000;

    private readonly BufferUsage usage;
    private readonly string name;
    private readonly nint[] fences = new nint[REGIONS];

    private T* mapped;
    private int region;

    /// <summary>The buffer itself, for binding it as a vertex buffer.</summary>
    public GpuBuffer? Buffer { get; private set; }

    /// <summary>How many items each region (one frame's worth) has room for.</summary>
    public int Capacity { get; private set; }

    /// <summary>How many items the current frame asked for with <see cref="Begin"/>.</summary>
    public int Count { get; private set; }

    /// <summary>Where the current frame's items start, counted in items from the start of the buffer.</summary>
    public int Offset => region * Capacity;

    /// <summary>Where the current frame's items start, in bytes.</summary>
    public nint ByteOffset => (nint)Offset * sizeof(T);

    /// <summary>How many bytes one region is.</summary>
    public nuint RegionBytes => (nuint)Capacity * (nuint)sizeof(T);

    /// <summary>Whether there is a buffer to write to. There isn't if the GPU wouldn't make one, which has been logged.</summary>
    public bool IsAvailable => mapped != null;

    /// <param name="usage">What the buffer is for the shaders, a storage block, an array of instances, or both.</param>
    /// <param name="initialCapacity">How many items a frame is made room for to begin with, it grows when more turn up.</param>
    /// <param name="name">What to call it in the log.</param>
    public StreamBuffer(BufferUsage usage, int initialCapacity, string name = "stream")
    {
        this.usage = usage;
        this.name = name;

        Resize(Math.Max(1, initialCapacity));
    }

    /// <summary>
    /// Starts a frame. Hands out memory for so many items, in the region whose turn it is, once the GPU is done with it.
    /// Empty if there is no buffer to be had.
    /// </summary>
    public Span<T> Begin(int count)
    {
        if (count > Capacity)
            Resize(count);

        if (mapped == null)
        {
            Count = 0;
            return default;
        }

        WaitFor(region);
        Count = count;
        return new Span<T>(mapped + Offset, count);
    }

    /// <summary>
    /// Ends the frame. A fence goes behind everything drawn so far, so the next frame that comes round to this region
    /// knows when the GPU is done with it.
    /// </summary>
    public void End()
    {
        if (mapped == null) return;

        fences[region] = GraphicsDevice.Current.CreateFence();
        region = (region + 1) % REGIONS;
        Count = 0;
    }

    /// <summary>Makes the current frame's region what a shader reads at a storage binding, as a block indexed from zero.</summary>
    public void BindRange(uint index) => Buffer?.BindStorageRange(index, ByteOffset, RegionBytes);

    /// <summary>
    /// Binds some of this frame's region, from an item for so many, where a shader block says it is. The byte offset of
    /// the first item has to suit the storage alignment, which is the caller's to arrange (a padded item).
    /// </summary>
    public void BindRange(uint index, int first, int count) =>
        Buffer?.BindStorageRange(index, ByteOffset + (nint)first * sizeof(T), (nuint)count * (nuint)sizeof(T));

    /// <summary>Writes one item into this frame's region, between <see cref="Begin"/> and <see cref="End"/>.</summary>
    public void Write(int index, in T item)
    {
        if (mapped == null || (uint)index >= (uint)Capacity) return;
        mapped[Offset + index] = item;
    }

    private void Resize(int wanted)
    {
        var device = GraphicsDevice.Current;

        // Every region starts on an offset a shader block may be bound at, so the capacity is rounded up to that
        uint alignment = Math.Max(device.StorageOffsetAlignment, device.UniformOffsetAlignment);
        int capacity = (int)(wanted * HEADROOM);
        nuint regionBytes = (nuint)capacity * (nuint)sizeof(T);
        regionBytes = (regionBytes + alignment - 1) / alignment * alignment;
        capacity = (int)(regionBytes / (nuint)sizeof(T));

        // Nothing is torn out from under the GPU
        for (int i = 0; i < REGIONS; i++)
            WaitFor(i);

        if (Buffer is not null)
        {
            Buffer.Dispose();
            Buffer = null;
            mapped = null;
        }

        nuint bytes = regionBytes * REGIONS;
        if (!ObjectManager.Instance.Buffers.TryCreate(new BufferDescription(usage, BufferAccess.Stream, bytes), out var result))
        {
            Log.Error($"[StreamBuffer] The {name} buffer of {bytes} bytes couldn't be made: {result.Message}");
            Capacity = 0;
            return;
        }

        Buffer = result.Asset;
        Buffer.Name = name;
        Capacity = capacity;
        region = 0;

        mapped = (T*)Buffer.Map(bytes);
        if (mapped == null)
            Log.Error($"[StreamBuffer] The {name} buffer couldn't be mapped.");
    }

    private void WaitFor(int which)
    {
        nint fence = fences[which];
        if (fence == 0) return;

        var device = GraphicsDevice.Current;
        device.WaitFence(fence, LONGEST_WAIT);
        device.DeleteFence(fence);
        fences[which] = 0;
    }

    public void Dispose()
    {
        for (int i = 0; i < REGIONS; i++) fences[i] = 0;

        Buffer?.Dispose();
        Buffer = null;
        mapped = null;
        Capacity = 0;
    }
}
