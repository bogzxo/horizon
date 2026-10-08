using Silk.NET.Vulkan;

namespace Horizon.Graphics.Vulkan;

/// <summary>
/// Every texture there is, in one table a shader can index into. Set 2 of the pipeline layout is this table, made
/// once for the device and written to as textures come and go, which descriptor indexing lets happen while the GPU
/// is reading other slots of it. A texture gets a slot the moment it is made and a sprite item names it by that
/// slot, so a batch of sprites that shows a hundred different textures is still one draw.
/// A texture read through other sampler settings than its own gets a slot of its own for the pair.
/// </summary>
internal sealed unsafe class BindlessTextures : IDisposable
{
    /// <summary>How many slots the table has. More than any scene has textures, and a slot is a few bytes.</summary>
    public const uint CAPACITY = 4096;

    /// <summary>The slot that stands for no texture at all.</summary>
    public const uint NONE = 0xFFFF;

    private readonly VulkanContext context;
    private readonly DescriptorPool pool;
    private readonly Stack<uint> free = new();
    private uint next;

    public DescriptorSetLayout Layout { get; }
    public DescriptorSet Set { get; }

    public BindlessTextures(VulkanContext context)
    {
        this.context = context;

        var flags = DescriptorBindingFlags.PartiallyBoundBit | DescriptorBindingFlags.UpdateAfterBindBit | DescriptorBindingFlags.UpdateUnusedWhilePendingBit;
        var flagsInfo = new DescriptorSetLayoutBindingFlagsCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo,
            BindingCount = 1,
            PBindingFlags = &flags
        };

        var binding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = CAPACITY,
            StageFlags = ShaderStageFlags.All
        };

        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            PNext = &flagsInfo,
            Flags = DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit,
            BindingCount = 1,
            PBindings = &binding
        };

        VulkanContext.Check(context.Vk.CreateDescriptorSetLayout(context.Device, in layoutInfo, null, out DescriptorSetLayout layout), "making the bindless layout");
        Layout = layout;

        var size = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, CAPACITY);
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            Flags = DescriptorPoolCreateFlags.UpdateAfterBindBit,
            MaxSets = 1,
            PoolSizeCount = 1,
            PPoolSizes = &size
        };

        VulkanContext.Check(context.Vk.CreateDescriptorPool(context.Device, in poolInfo, null, out pool), "making the bindless pool");

        var allocateInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = pool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout
        };

        VulkanContext.Check(context.Vk.AllocateDescriptorSets(context.Device, in allocateInfo, out DescriptorSet set), "allocating the bindless set");
        Set = set;
    }

    /// <summary>Puts a texture (read through a sampler) in the table and says which slot it got.</summary>
    public uint Add(ImageView view, Sampler sampler)
    {
        uint slot = free.Count > 0 ? free.Pop() : next++;
        if (slot >= CAPACITY) throw new InvalidOperationException($"The bindless table is full, {CAPACITY} textures is the most it holds.");

        Write(slot, view, sampler);
        return slot;
    }

    private void Write(uint slot, ImageView view, Sampler sampler)
    {
        var info = new DescriptorImageInfo(sampler, view, ImageLayout.ShaderReadOnlyOptimal);
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = Set,
            DstBinding = 0,
            DstArrayElement = slot,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &info
        };

        context.Vk.UpdateDescriptorSets(context.Device, 1, in write, 0, null);
    }

    /// <summary>Gives a slot back, once the GPU is done with whatever read it. The caller sees to the waiting.</summary>
    public void Release(uint slot)
    {
        if (slot < CAPACITY) free.Push(slot);
    }

    public void Dispose()
    {
        context.Vk.DestroyDescriptorPool(context.Device, pool, null);
        context.Vk.DestroyDescriptorSetLayout(context.Device, Layout, null);
    }
}
