using Silk.NET.Vulkan;

namespace Horizon.Graphics.Vulkan;

/// <summary>
/// Where buffers and images get their memory. Vulkan hands out memory in big lumps and only a few thousand of them,
/// so this takes blocks of it (64 MB on the card, 32 MB where the CPU can see it) and carves them up with a free
/// list. Memory the CPU can write to is mapped once when the block is made and stays mapped. Buffers and images
/// never share a block, which keeps the granularity rules out of it.
/// Render thread, like everything that makes resources.
/// </summary>
internal sealed unsafe class VulkanMemory : IDisposable
{
    private const ulong DEVICE_BLOCK = 64 * 1024 * 1024;
    private const ulong HOST_BLOCK = 32 * 1024 * 1024;

    /// <summary>A piece of a block, handed to a buffer or an image.</summary>
    public readonly struct Allocation
    {
        public readonly Block Block;
        public readonly ulong Offset;
        public readonly ulong Size;

        internal Allocation(Block block, ulong offset, ulong size)
        {
            Block = block;
            Offset = offset;
            Size = size;
        }

        public DeviceMemory Memory => Block.Memory;

        /// <summary>Where the piece is in the CPU's view of the block, null for memory the CPU can't see.</summary>
        public byte* Mapped => Block.Mapped == null ? null : Block.Mapped + Offset;

        public bool IsHostVisible => Block.Mapped != null;

        public bool IsValid => Block is not null;
    }

    public sealed class Block
    {
        public DeviceMemory Memory;
        public ulong Size;
        public uint TypeIndex;
        public bool ForImages;
        public byte* Mapped;

        // The holes, in order, each (offset, size)
        public readonly List<(ulong Offset, ulong Size)> Free = [];
        public ulong Used;
    }

    private readonly VulkanContext context;
    private readonly List<Block> blocks = [];

    /// <summary>How many bytes are handed out right now.</summary>
    public ulong InUse { get; private set; }

    public int BlockCount => blocks.Count;

    public VulkanMemory(VulkanContext context)
    {
        this.context = context;
    }

    /// <summary>
    /// Finds room for something. Memory with the required flags, with the preferred ones on top if there is any.
    /// </summary>
    public Allocation Allocate(in MemoryRequirements requirements, MemoryPropertyFlags required, MemoryPropertyFlags preferred, bool forImage)
    {
        uint typeIndex = FindType(requirements.MemoryTypeBits, required | preferred);
        if (typeIndex == uint.MaxValue) typeIndex = FindType(requirements.MemoryTypeBits, required);
        if (typeIndex == uint.MaxValue)
            throw new InvalidOperationException("The card has no memory of the kind that was asked for.");

        bool hostVisible = (context.MemoryProperties.MemoryTypes[(int)typeIndex].PropertyFlags & MemoryPropertyFlags.HostVisibleBit) != 0;
        ulong size = requirements.Size, alignment = Math.Max(requirements.Alignment, 1);

        foreach (Block block in blocks)
        {
            if (block.TypeIndex != typeIndex || block.ForImages != forImage) continue;
            if (TryCarve(block, size, alignment, out Allocation allocation)) return allocation;
        }

        ulong blockSize = Math.Max(hostVisible ? HOST_BLOCK : DEVICE_BLOCK, size);
        Block made = MakeBlock(typeIndex, blockSize, forImage, hostVisible);
        if (made is null)
        {
            // The memory of that kind is spoken for, something else will have to do
            uint fallback = FindType(requirements.MemoryTypeBits, required, typeIndex);
            if (fallback == uint.MaxValue) throw new InvalidOperationException("The card is out of memory.");

            bool fallbackVisible = (context.MemoryProperties.MemoryTypes[(int)fallback].PropertyFlags & MemoryPropertyFlags.HostVisibleBit) != 0;
            made = MakeBlock(fallback, Math.Max(fallbackVisible ? HOST_BLOCK : DEVICE_BLOCK, size), forImage, fallbackVisible)
                   ?? throw new InvalidOperationException("The card is out of memory.");
        }

        blocks.Add(made);
        if (!TryCarve(made, size, alignment, out Allocation fresh))
            throw new InvalidOperationException("A new block of memory had no room in it, which can't be.");

        return fresh;
    }

    private Block? MakeBlock(uint typeIndex, ulong size, bool forImage, bool hostVisible)
    {
        var info = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = size,
            MemoryTypeIndex = typeIndex
        };

        if (context.Vk.AllocateMemory(context.Device, in info, null, out DeviceMemory memory) != Result.Success)
            return null;

        var block = new Block { Memory = memory, Size = size, TypeIndex = typeIndex, ForImages = forImage };
        block.Free.Add((0, size));

        if (hostVisible)
        {
            void* mapped;
            VulkanContext.Check(context.Vk.MapMemory(context.Device, memory, 0, size, 0, &mapped), "mapping a block of memory");
            block.Mapped = (byte*)mapped;
        }

        return block;
    }

    private bool TryCarve(Block block, ulong size, ulong alignment, out Allocation allocation)
    {
        for (int i = 0; i < block.Free.Count; i++)
        {
            var (offset, free) = block.Free[i];
            ulong aligned = (offset + alignment - 1) / alignment * alignment;
            ulong padding = aligned - offset;
            if (free < padding + size) continue;

            // What is left before (the padding) and after goes back to the list
            block.Free.RemoveAt(i);
            ulong after = free - padding - size;
            if (after > 0) block.Free.Insert(i, (aligned + size, after));
            if (padding > 0) block.Free.Insert(i, (offset, padding));

            block.Used += size;
            InUse += size;
            allocation = new Allocation(block, aligned, size);
            return true;
        }

        allocation = default;
        return false;
    }

    public void Free(in Allocation allocation)
    {
        Block block = allocation.Block;
        if (block is null) return;

        block.Used -= allocation.Size;
        InUse -= allocation.Size;

        // Back into the list in order, joined with the neighbours it touches
        int at = 0;
        while (at < block.Free.Count && block.Free[at].Offset < allocation.Offset) at++;
        block.Free.Insert(at, (allocation.Offset, allocation.Size));

        if (at + 1 < block.Free.Count && block.Free[at].Offset + block.Free[at].Size == block.Free[at + 1].Offset)
        {
            block.Free[at] = (block.Free[at].Offset, block.Free[at].Size + block.Free[at + 1].Size);
            block.Free.RemoveAt(at + 1);
        }

        if (at > 0 && block.Free[at - 1].Offset + block.Free[at - 1].Size == block.Free[at].Offset)
        {
            block.Free[at - 1] = (block.Free[at - 1].Offset, block.Free[at - 1].Size + block.Free[at].Size);
            block.Free.RemoveAt(at);
        }

        // An empty block that isn't the only one of its kind goes back to the driver
        if (block.Used == 0 && HasAnother(block))
        {
            blocks.Remove(block);
            Release(block);
        }
    }

    // Whether there is another block of the same kind, so this one can go. A loop, this is run for everything
    // that is ever freed and counting with a lambda made garbage out of taking the garbage out
    private bool HasAnother(Block block)
    {
        foreach (Block other in blocks)
        {
            if (!ReferenceEquals(other, block) && other.TypeIndex == block.TypeIndex && other.ForImages == block.ForImages)
                return true;
        }

        return false;
    }

    private uint FindType(uint allowed, MemoryPropertyFlags flags, uint except = uint.MaxValue)
    {
        var types = context.MemoryProperties.MemoryTypes;
        for (uint i = 0; i < context.MemoryProperties.MemoryTypeCount; i++)
        {
            if (i == except) continue;
            if ((allowed & (1u << (int)i)) == 0) continue;
            if ((types[(int)i].PropertyFlags & flags) == flags) return i;
        }

        return uint.MaxValue;
    }

    private void Release(Block block)
    {
        if (block.Mapped != null) context.Vk.UnmapMemory(context.Device, block.Memory);
        context.Vk.FreeMemory(context.Device, block.Memory, null);
    }

    public void Dispose()
    {
        foreach (Block block in blocks) Release(block);
        blocks.Clear();
        InUse = 0;
    }
}
