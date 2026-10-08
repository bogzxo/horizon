using Horizon.Graphics.Vulkan;
using Horizon.Logging;

using Silk.NET.Vulkan;

using Buffer = Silk.NET.Vulkan.Buffer;

namespace Horizon.Graphics;

/// <summary>
/// Making and freeing what lives on the GPU, the memory of buffers and images, the views and the shader modules. The
/// resource classes call in here; nothing else has to.
/// </summary>
public sealed unsafe partial class GraphicsDevice
{
    /* Buffers */

    /// <summary>
    /// Helper method to give a buffer memory of a size. It gets new memory if it has none yet, is too small, or the GPU
    /// may still be reading what it has, which is what makes rewriting a buffer every frame safe.
    /// </summary>
    internal void AllocateBuffer(GpuBuffer buffer, nuint bytes)
    {
        bytes = Math.Max(bytes, 16);

        if (buffer.Buffer.Handle != 0)
        {
            bool inUse = buffer.LastUse != 0 && !IsDone(buffer.LastUse);
            if (!inUse && buffer.Capacity >= bytes)
            {
                buffer.Size = bytes;
                return;
            }

            RetireBuffer(buffer.Buffer, buffer.Memory);
            buffer.Buffer = default;
            buffer.Memory = default;
        }

        BufferUsageFlags usage = BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit;
        if ((buffer.Usage & BufferUsage.Vertex) != 0) usage |= BufferUsageFlags.VertexBufferBit;
        if ((buffer.Usage & BufferUsage.Index) != 0) usage |= BufferUsageFlags.IndexBufferBit;
        if ((buffer.Usage & BufferUsage.Storage) != 0) usage |= BufferUsageFlags.StorageBufferBit;
        if ((buffer.Usage & BufferUsage.Uniform) != 0) usage |= BufferUsageFlags.UniformBufferBit;
        if ((buffer.Usage & BufferUsage.Indirect) != 0) usage |= BufferUsageFlags.IndirectBufferBit;

        var info = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = bytes,
            Usage = usage,
            SharingMode = SharingMode.Exclusive
        };

        VulkanContext.Check(Vk.CreateBuffer(Device, in info, null, out Buffer handle), "making a buffer");
        Vk.GetBufferMemoryRequirements(Device, handle, out MemoryRequirements requirements);

        // What the CPU writes to lives where it can see it (on the card if the card lets it), what only the GPU touches on the card
        VulkanMemory.Allocation memory = buffer.Access == BufferAccess.Dynamic
            ? Memory.Allocate(requirements, MemoryPropertyFlags.DeviceLocalBit, MemoryPropertyFlags.None, forImage: false)
            : Memory.Allocate(requirements, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, MemoryPropertyFlags.DeviceLocalBit, forImage: false);

        VulkanContext.Check(Vk.BindBufferMemory(Device, handle, memory.Memory, memory.Offset), "binding a buffer's memory");

        buffer.Buffer = handle;
        buffer.Memory = memory;
        buffer.Capacity = bytes;
        buffer.Size = bytes;
        buffer.LastUse = 0;
        Statistics.Buffers++;
    }

    private void RetireBuffer(Buffer handle, VulkanMemory.Allocation memory)
    {
        Statistics.Buffers--;
        Retire(() =>
        {
            Vk.DestroyBuffer(Device, handle, null);
            Memory.Free(memory);
        });
    }

    internal void DestroyBuffer(GpuBuffer buffer)
    {
        Unbind(buffer);
        if (buffer.Buffer.Handle == 0) return;

        RetireBuffer(buffer.Buffer, buffer.Memory);
        buffer.Buffer = default;
        buffer.Memory = default;
    }

    /* Textures */

    /// <summary>Helper method to make the image, its memory and its view, and fill it in if there is anything to fill it with.</summary>
    internal void CreateTexture(Texture texture, ReadOnlySpan<byte> pixels)
    {
        TextureDefinition definition = texture.Definition;
        Format format = FormatOf(definition.Format);
        uint levels = definition.Mipmaps ? (uint)Math.Floor(Math.Log2(Math.Max(texture.Width, texture.Height))) + 1 : 1;

        ImageUsageFlags usage = ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit;
        if ((definition.Usage & TextureUsage.Sampled) != 0) usage |= ImageUsageFlags.SampledBit;
        if ((definition.Usage & TextureUsage.Storage) != 0) usage |= ImageUsageFlags.StorageBit;
        if ((definition.Usage & TextureUsage.RenderTarget) != 0)
            usage |= definition.IsDepth ? ImageUsageFlags.DepthStencilAttachmentBit : ImageUsageFlags.ColorAttachmentBit;

        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D(texture.Width, texture.Height, 1),
            MipLevels = levels,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined
        };

        VulkanContext.Check(Vk.CreateImage(Device, in imageInfo, null, out Image image), $"making a {texture.Width} by {texture.Height} image");
        Vk.GetImageMemoryRequirements(Device, image, out MemoryRequirements requirements);
        var memory = Memory.Allocate(requirements, MemoryPropertyFlags.DeviceLocalBit, MemoryPropertyFlags.None, forImage: true);
        VulkanContext.Check(Vk.BindImageMemory(Device, image, memory.Memory, memory.Offset), "binding an image's memory");

        ImageAspectFlags aspect = definition.IsDepth
            ? ImageAspectFlags.DepthBit | (definition.HasStencil ? ImageAspectFlags.StencilBit : 0)
            : ImageAspectFlags.ColorBit;

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            // A shader only ever reads the depth of a depth texture
            SubresourceRange = new ImageSubresourceRange(definition.IsDepth ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit, 0, levels, 0, 1)
        };

        VulkanContext.Check(Vk.CreateImageView(Device, in viewInfo, null, out ImageView view), "making an image view");

        texture.Image = image;
        texture.View = view;
        texture.Memory = memory;
        texture.VkFormat = format;
        texture.MipLevels = levels;
        texture.Aspect = aspect;
        texture.Layout = ImageLayout.Undefined;
        texture.LastStage = PipelineStageFlags2.TopOfPipeBit;
        texture.LastAccess = AccessFlags2.None;
        texture.DefaultSampler = SamplerFor(definition.Sampler);
        texture.BindlessIndex = (definition.Usage & TextureUsage.Sampled) != 0 ? Bindless.Add(view, texture.DefaultSampler) : BindlessTextures.NONE;
        Statistics.Textures++;

        if (!pixels.IsEmpty)
        {
            fixed (byte* pointer = pixels)
                UploadTexels(texture, 0, 0, texture.Width, texture.Height, definition.Format == PixelFormat.R8 ? TexelFormat.R8 : TexelFormat.Rgba8, pointer);

            if (levels > 1) GenerateMipmaps(texture);
        }
        else if (!definition.IsDepth && (definition.Usage & TextureUsage.RenderTarget) == 0)
        {
            // A texture that is read before anything was put in it shows nothing rather than whatever was in the memory
            ClearImageNow(texture, System.Numerics.Vector4.Zero);
        }
    }

    internal void DestroyTexture(Texture texture)
    {
        Unbind(texture);
        if (texture.Image.Handle == 0) return;

        Image image = texture.Image;
        ImageView view = texture.View;
        VulkanMemory.Allocation memory = texture.Memory;
        uint slot = texture.BindlessIndex;
        uint[] variants = texture.BindlessVariants is { } made ? [.. made.Values] : [];
        texture.BindlessIndex = BindlessTextures.NONE;
        texture.BindlessVariants = null;
        Statistics.Textures--;

        // The slots go back once nothing that was recorded with them can still be running
        Retire(() =>
        {
            Vk.DestroyImageView(Device, view, null);
            Vk.DestroyImage(Device, image, null);
            Memory.Free(memory);
            if (slot != BindlessTextures.NONE) Bindless.Release(slot);
            foreach (uint variant in variants) Bindless.Release(variant);
        });

        texture.Image = default;
        texture.View = default;
        texture.Memory = default;
    }

    internal Format FormatOf(PixelFormat format) => format switch
    {
        PixelFormat.Rgba8 => Format.R8G8B8A8Unorm,
        PixelFormat.Rgba8Srgb => Format.R8G8B8A8Srgb,
        PixelFormat.R8 => Format.R8Unorm,
        PixelFormat.R16F => Format.R16Sfloat,
        PixelFormat.Rg16F => Format.R16G16Sfloat,
        PixelFormat.Rg32F => Format.R32G32Sfloat,
        PixelFormat.Rgba16F => Format.R16G16B16A16Sfloat,
        PixelFormat.Rgba32F => Format.R32G32B32A32Sfloat,
        PixelFormat.Depth24Stencil8 => Context.DepthStencilFormat,
        PixelFormat.Depth32F => Format.D32Sfloat,
        _ => Format.R8G8B8A8Unorm
    };

    /* Shaders */

    /// <summary>Helper method to turn SPIR-V into a module.</summary>
    internal ShaderModule CreateShaderModule(byte[] spirv)
    {
        fixed (byte* pointer = spirv)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirv.Length,
                PCode = (uint*)pointer
            };

            VulkanContext.Check(Vk.CreateShaderModule(Device, in info, null, out ShaderModule module), "making a shader module");
            return module;
        }
    }

    internal void DestroyShader(Shader shader)
    {
        Unbind(shader);

        var pipelines = Pipelines.Forget(shader);
        var modules = shader.Modules.Select(stage => stage.Module).DistinctBy(module => module.Handle).ToArray();
        shader.Modules.Clear();

        Retire(() =>
        {
            foreach (var pipeline in pipelines) Vk.DestroyPipeline(Device, pipeline, null);
            foreach (var module in modules) Vk.DestroyShaderModule(Device, module, null);
        });
    }

    /* Render targets and vertex arrays own nothing of their own on the GPU */

    internal void DestroyRenderTarget(RenderTarget target) => Unbind(target);

    internal void DestroyVertexArray(VertexArray array) => Unbind(array);
}
