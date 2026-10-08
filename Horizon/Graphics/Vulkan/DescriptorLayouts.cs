using Silk.NET.Vulkan;

namespace Horizon.Graphics.Vulkan;

/// <summary>
/// The one pipeline layout every shader of the engine is made against, so nothing has to be reflected and any shader
/// can be bound after any other without the descriptors having to be made anew. Set 0 is the buffers, binding 0 the
/// camera block and 1 the params of the draw (both with an offset that changes per draw, out of the uniform arena of
/// the frame), 2 and up storage buffers. Set 1 is the textures (0 to 7) and the storage images (8 to 11). Every
/// binding is partially bound, a shader that doesn't use one doesn't need anything in it. Set 2 is the bindless
/// table of every texture, see <see cref="BindlessTextures"/>.
/// The descriptor sets themselves come out of a pool per frame that is wiped at the start of the frame, see
/// <see cref="FrameResources"/>.
/// </summary>
internal sealed unsafe class DescriptorLayouts : IDisposable
{
    public const uint CAMERA_BINDING = 0;
    public const uint PARAMS_BINDING = 1;
    public const uint FIRST_STORAGE_BINDING = 2;
    public const uint FIRST_IMAGE_BINDING = 8;
    public const uint FIRST_SAMPLER_BINDING = 12;

    private readonly VulkanContext context;

    public DescriptorSetLayout Buffers { get; }
    public DescriptorSetLayout Images { get; }
    public PipelineLayout PipelineLayout { get; }

    public DescriptorLayouts(VulkanContext context, BindlessTextures bindless)
    {
        this.context = context;

        var bufferBindings = new DescriptorSetLayoutBinding[2 + ShaderPreprocessor.STORAGE_BINDINGS];
        bufferBindings[0] = Binding(CAMERA_BINDING, DescriptorType.UniformBufferDynamic);
        bufferBindings[1] = Binding(PARAMS_BINDING, DescriptorType.UniformBufferDynamic);
        for (uint i = 0; i < ShaderPreprocessor.STORAGE_BINDINGS; i++)
            bufferBindings[2 + i] = Binding(FIRST_STORAGE_BINDING + i, DescriptorType.StorageBuffer);

        var imageBindings = new DescriptorSetLayoutBinding[ShaderPreprocessor.TEXTURE_BINDINGS + ShaderPreprocessor.IMAGE_BINDINGS + ShaderPreprocessor.SAMPLER_BINDINGS];
        for (uint i = 0; i < ShaderPreprocessor.TEXTURE_BINDINGS; i++)
            imageBindings[i] = Binding(i, DescriptorType.CombinedImageSampler);
        for (uint i = 0; i < ShaderPreprocessor.IMAGE_BINDINGS; i++)
            imageBindings[ShaderPreprocessor.TEXTURE_BINDINGS + i] = Binding(FIRST_IMAGE_BINDING + i, DescriptorType.StorageImage);
        for (uint i = 0; i < ShaderPreprocessor.SAMPLER_BINDINGS; i++)
            imageBindings[ShaderPreprocessor.TEXTURE_BINDINGS + ShaderPreprocessor.IMAGE_BINDINGS + i] = Binding(FIRST_SAMPLER_BINDING + i, DescriptorType.Sampler);

        Buffers = MakeLayout(bufferBindings);
        Images = MakeLayout(imageBindings);
        context.Name(ObjectType.DescriptorSetLayout, Buffers.Handle, "set 0, the buffers");
        context.Name(ObjectType.DescriptorSetLayout, Images.Handle, "set 1, the textures and images");

        var layouts = stackalloc DescriptorSetLayout[3] { Buffers, Images, bindless.Layout };
        var pipelineInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 3,
            PSetLayouts = layouts
        };

        VulkanContext.Check(context.Vk.CreatePipelineLayout(context.Device, in pipelineInfo, null, out PipelineLayout layout), "making the pipeline layout");
        PipelineLayout = layout;
        context.Name(ObjectType.PipelineLayout, layout.Handle, "the one pipeline layout");
    }

    private static DescriptorSetLayoutBinding Binding(uint binding, DescriptorType type) => new()
    {
        Binding = binding,
        DescriptorType = type,
        DescriptorCount = 1,
        StageFlags = ShaderStageFlags.All
    };

    private DescriptorSetLayout MakeLayout(DescriptorSetLayoutBinding[] bindings)
    {
        var flags = new DescriptorBindingFlags[bindings.Length];
        Array.Fill(flags, DescriptorBindingFlags.PartiallyBoundBit);

        fixed (DescriptorSetLayoutBinding* bindingPointer = bindings)
        fixed (DescriptorBindingFlags* flagPointer = flags)
        {
            var flagsInfo = new DescriptorSetLayoutBindingFlagsCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo,
                BindingCount = (uint)bindings.Length,
                PBindingFlags = flagPointer
            };

            var info = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                PNext = &flagsInfo,
                BindingCount = (uint)bindings.Length,
                PBindings = bindingPointer
            };

            VulkanContext.Check(context.Vk.CreateDescriptorSetLayout(context.Device, in info, null, out DescriptorSetLayout layout), "making a descriptor set layout");
            return layout;
        }
    }

    public void Dispose()
    {
        context.Vk.DestroyPipelineLayout(context.Device, PipelineLayout, null);
        context.Vk.DestroyDescriptorSetLayout(context.Device, Buffers, null);
        context.Vk.DestroyDescriptorSetLayout(context.Device, Images, null);
    }
}

/// <summary>
/// Hands out descriptor sets for a frame. Nothing is ever freed one by one, the whole pool is reset when the frame
/// comes round again. A pool that runs dry gets another next to it.
/// </summary>
internal sealed unsafe class DescriptorAllocator : IDisposable
{
    private const uint SETS_PER_POOL = 2048;

    private readonly VulkanContext context;
    private readonly List<DescriptorPool> pools = [];
    private int current;

    public int SetsAllocated { get; private set; }

    // Which frame in flight the sets are for, for the names
    private readonly int frame;

    public DescriptorAllocator(VulkanContext context, int frame)
    {
        this.context = context;
        this.frame = frame;
        pools.Add(MakePool());
    }

    private DescriptorPool MakePool()
    {
        var sizes = stackalloc DescriptorPoolSize[5]
        {
            new(DescriptorType.UniformBufferDynamic, SETS_PER_POOL * 2),
            new(DescriptorType.StorageBuffer, SETS_PER_POOL * ShaderPreprocessor.STORAGE_BINDINGS),
            new(DescriptorType.CombinedImageSampler, SETS_PER_POOL * ShaderPreprocessor.TEXTURE_BINDINGS),
            new(DescriptorType.StorageImage, SETS_PER_POOL * ShaderPreprocessor.IMAGE_BINDINGS),
            new(DescriptorType.Sampler, SETS_PER_POOL * ShaderPreprocessor.SAMPLER_BINDINGS)
        };

        var info = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = SETS_PER_POOL * 2,
            PoolSizeCount = 5,
            PPoolSizes = sizes
        };

        VulkanContext.Check(context.Vk.CreateDescriptorPool(context.Device, in info, null, out DescriptorPool pool), "making a descriptor pool");
        context.Name(ObjectType.DescriptorPool, pool.Handle, $"frame {frame} descriptors {pools.Count}");
        return pool;
    }

    public DescriptorSet Allocate(DescriptorSetLayout layout)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var info = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = pools[current],
                DescriptorSetCount = 1,
                PSetLayouts = &layout
            };

            Result result = context.Vk.AllocateDescriptorSets(context.Device, in info, out DescriptorSet set);
            if (result == Result.Success)
            {
                SetsAllocated++;
                return set;
            }

            if (result is not (Result.ErrorOutOfPoolMemory or Result.ErrorFragmentedPool))
                VulkanContext.Check(result, "allocating a descriptor set");

            // This pool is full, on to the next (made if there is none)
            current++;
            if (current == pools.Count) pools.Add(MakePool());
        }

        throw new InvalidOperationException("A fresh descriptor pool had no room in it.");
    }

    /// <summary>Wipes every set, for the frame to come round again.</summary>
    public void Reset()
    {
        foreach (var pool in pools)
            context.Vk.ResetDescriptorPool(context.Device, pool, 0);

        current = 0;
        SetsAllocated = 0;
    }

    public void Dispose()
    {
        foreach (var pool in pools)
            context.Vk.DestroyDescriptorPool(context.Device, pool, null);
        pools.Clear();
    }
}
