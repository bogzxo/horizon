using System.Numerics;

using Horizon.Graphics.Vulkan;

using Silk.NET.Vulkan;

using Buffer = Silk.NET.Vulkan.Buffer;
using PipelineCache = Horizon.Graphics.Vulkan.PipelineCache;

namespace Horizon.Graphics;

/// <summary>
/// What is bound and how the next draw is set up, the shader, the vertex array, the render target, the textures and
/// buffers on their bindings, the blending and the tests. None of it touches the command buffer until a draw or a
/// dispatch needs it, so setting the same thing twice costs nothing and the order things are bound in doesn't matter.
/// </summary>
public sealed unsafe partial class GraphicsDevice
{
    private Shader? boundShader;
    private VertexArray? boundVertexArray;
    private RenderTarget? boundTarget;
    private bool targetIsWindow = true;

    internal Technique? BoundTechnique;

    private readonly Texture?[] boundTextures = new Texture?[ShaderPreprocessor.TEXTURE_BINDINGS];
    private readonly uint[] boundSamplers = new uint[ShaderPreprocessor.TEXTURE_BINDINGS];
    private readonly Texture?[] boundImages = new Texture?[ShaderPreprocessor.IMAGE_BINDINGS];
    private readonly (GpuBuffer? Buffer, ulong Offset, ulong Size)[] boundStorage = new (GpuBuffer?, ulong, ulong)[ShaderPreprocessor.STORAGE_BINDINGS];

    // Where in the frame's uniform arena the camera block and the params of the draw are
    private ulong cameraOffset, paramsOffset;
    private int uniformsGeneration = -1;

    private bool buffersDirty = true, imagesDirty = true, offsetsDirty = true;
    private DescriptorSet bufferSet, imageSet;
    private PipelineBindPoint setsBoundFor = (PipelineBindPoint)(-1);

    // The pipeline state that isn't dynamic, see PipelineCache
    private bool blend, colorWrite = true;
    private BlendMode blendMode = BlendMode.Replace;

    // And the state that is
    private bool depthTest, depthWrite = true, stencilTest;
    private CullMode cullMode;
    private CompareFunction stencilFunction = CompareFunction.Always;
    private int stencilReference;
    private uint stencilCompareMask = 0xFF, stencilWriteMask = 0xFF;
    private StencilAction stencilFail = StencilAction.Keep, stencilDepthFail = StencilAction.Keep, stencilPass = StencilAction.Keep;
    private bool dynamicDirty = true;

    private int viewportX, viewportY;
    private uint viewportWidth, viewportHeight;
    private bool viewportDirty = true;

    private Pipeline currentPipeline;
    private bool renderingActive;
    private RenderTarget? renderingTarget;
    private bool renderingWindow;

    /// <summary>Helper method to forget everything a command buffer knew, for a fresh one.</summary>
    private void ResetRecordingState()
    {
        currentPipeline = default;
        renderingActive = false;
        renderingTarget = null;
        buffersDirty = imagesDirty = offsetsDirty = true;
        setsBoundFor = (PipelineBindPoint)(-1);
        dynamicDirty = viewportDirty = true;
    }

    /* What draws */

    /// <summary>Makes a shader what the next draws (or dispatches) run. Null for none.</summary>
    public void BindShader(Shader? shader)
    {
        if (ReferenceEquals(shader, boundShader)) return;

        boundShader = shader is { IsValid: true } ? shader : null;
        Statistics.Counting.ShaderBinds++;
    }

    /// <summary>Makes a vertex array what the next draws read their vertices (and indices) from. Null for a draw that makes its own up.</summary>
    public void BindVertexArray(VertexArray? array) => boundVertexArray = array is { IsValid: true } ? array : null;

    /* Textures, images and buffers */

    /// <summary>Binds a texture to a unit of set 1, where <c>BIND_TEXTURE(unit)</c> says a sampler is. Null leaves nothing on it.</summary>
    public void BindTexture(uint unit, Texture? texture)
    {
        if (unit >= boundTextures.Length) return;

        Texture? valid = texture is { IsValid: true } ? texture : null;
        if (ReferenceEquals(boundTextures[unit], valid)) return;

        boundTextures[unit] = valid;
        imagesDirty = true;
    }

    /// <summary>Binds textures to a run of units in one go, the first to <paramref name="firstUnit"/>.</summary>
    public void BindTextures(ReadOnlySpan<Texture?> textures, uint firstUnit = 0)
    {
        for (int i = 0; i < textures.Length; i++)
            BindTexture(firstUnit + (uint)i, textures[i]);
    }

    /// <summary>Binds samplers to a run of units in one go. A handle of 0 has the unit go by the texture's own settings.</summary>
    public void BindSamplers(ReadOnlySpan<uint> samplers, uint firstUnit = 0)
    {
        for (int i = 0; i < samplers.Length; i++)
        {
            uint unit = firstUnit + (uint)i;
            if (unit >= boundSamplers.Length || boundSamplers[unit] == samplers[i]) continue;

            boundSamplers[unit] = samplers[i];
            imagesDirty = true;
        }
    }

    /// <summary>Binds a texture as a storage image on a slot of set 1, where <c>BIND_IMAGE(slot, format)</c> says one is. For compute.</summary>
    public void BindStorageImage(uint slot, Texture? texture)
    {
        if (slot >= boundImages.Length) return;

        Texture? valid = texture is { IsValid: true } ? texture : null;
        if (ReferenceEquals(boundImages[slot], valid)) return;

        boundImages[slot] = valid;
        imagesDirty = true;
    }

    /// <summary>
    /// Makes a buffer (or a stretch of it) what a shader reads at a storage binding, where <c>BIND_BUFFER(index)</c>
    /// says a block is. The offset has to be a multiple of <see cref="StorageOffsetAlignment"/>. A size of 0 is the rest of the buffer.
    /// </summary>
    public void BindStorageBuffer(uint index, GpuBuffer? buffer, nint offset = 0, nuint size = 0)
    {
        if (index < DescriptorLayouts.FIRST_STORAGE_BINDING)
            throw new ArgumentOutOfRangeException(nameof(index), "Bindings 0 and 1 of set 0 are the camera and the params, storage buffers start at 2.");

        uint slot = index - DescriptorLayouts.FIRST_STORAGE_BINDING;
        if (slot >= boundStorage.Length) return;

        GpuBuffer? valid = buffer is { IsValid: true } ? buffer : null;
        ulong bytes = valid is null ? 0 : size == 0 ? (ulong)valid.Size - (ulong)offset : size;

        var wanted = (valid, (ulong)offset, bytes);
        if (boundStorage[slot] == wanted) return;

        boundStorage[slot] = wanted;
        buffersDirty = true;
    }

    /// <summary>
    /// Writes a uniform block into the frame's arena and makes it what the shaders read at the camera binding (0).
    /// Cheap, so write whenever the camera changes.
    /// </summary>
    public void SetCameraBlock(ReadOnlySpan<byte> block)
    {
        cameraOffset = WriteUniform(block);
        offsetsDirty = true;
    }

    /// <summary>
    /// Writes the params of the next draw into the frame's arena and makes them what the shader reads at binding 1.
    /// <see cref="Technique"/> does this for whoever sets uniforms by name.
    /// </summary>
    public void SetParams(ReadOnlySpan<byte> block)
    {
        paramsOffset = WriteUniform(block);
        offsetsDirty = true;
    }

    private const ulong UNIFORM_RANGE = 4096;

    private ulong WriteUniform(ReadOnlySpan<byte> block)
    {
        LinearArena arena = Frame.Uniforms;
        ulong offset = arena.Take((ulong)block.Length, UniformOffsetAlignment, UNIFORM_RANGE);
        block.CopyTo(new Span<byte>(arena.Mapped + offset, block.Length));
        Statistics.Counting.BytesStreamed += block.Length;

        if (arena.Generation != uniformsGeneration)
        {
            uniformsGeneration = arena.Generation;
            buffersDirty = true;
        }

        return offset;
    }

    /* Samplers */

    /// <summary>Makes a sampler, for drawing a texture with other settings than its own. The number stands for it in <see cref="BindSamplers"/>.</summary>
    public uint CreateSampler(SamplerSettings settings)
    {
        Sampler sampler = SamplerFor(settings);
        foreach (var (id, existing) in samplersById)
        {
            if (existing.Handle == sampler.Handle) return id;
        }

        uint made = nextSamplerId++;
        samplersById[made] = sampler;
        return made;
    }

    public void DeleteSampler(uint sampler)
    {
        // Samplers are shared by their settings and freed with the device, there is nothing to do here
    }

    internal Sampler SamplerFor(SamplerSettings settings)
    {
        if (samplersBySettings.TryGetValue(settings, out Sampler existing)) return existing;

        Filter filter = settings.Smooth ? Filter.Linear : Filter.Nearest;
        SamplerAddressMode address = settings.Repeat ? SamplerAddressMode.Repeat : settings.Border ? SamplerAddressMode.ClampToBorder : SamplerAddressMode.ClampToEdge;

        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = filter,
            MinFilter = filter,
            MipmapMode = settings.Smooth ? SamplerMipmapMode.Linear : SamplerMipmapMode.Nearest,
            AddressModeU = address,
            AddressModeV = address,
            AddressModeW = address,
            MaxLod = settings.Mipmaps ? Vk.LodClampNone : 0.25f,
            BorderColor = BorderColor.FloatTransparentBlack
        };

        VulkanContext.Check(Vk.CreateSampler(Device, in info, null, out Sampler sampler), "making a sampler");
        Context.Name(ObjectType.Sampler, sampler.Handle, $"{(settings.Smooth ? "smooth" : "nearest")}{(settings.Mipmaps ? " mipmapped" : "")}{(settings.Repeat ? " repeating" : settings.Border ? " bordered" : " clamped")} sampler");
        samplersBySettings[settings] = sampler;
        uint id = nextSamplerId++;
        samplersById[id] = sampler;
        return sampler;
    }

    private Sampler SamplerOf(uint id, Texture texture) => id != 0 && samplersById.TryGetValue(id, out Sampler sampler) ? sampler : texture.DefaultSampler;

    /// <summary>
    /// The slot of a texture in the bindless table when read through a sampler of its own rather than its default
    /// one. Made the first time it is asked for and kept with the texture.
    /// </summary>
    public uint BindlessSlot(Texture texture, uint sampler)
    {
        if (!texture.IsValid) return BindlessTextures.NONE;
        if (sampler == 0 || !samplersById.TryGetValue(sampler, out Sampler found)) return texture.BindlessIndex;

        texture.BindlessVariants ??= [];
        if (texture.BindlessVariants.TryGetValue(sampler, out uint slot)) return slot;

        slot = Bindless.Add(texture.View, found);
        texture.BindlessVariants[sampler] = slot;
        return slot;
    }

    /* What is drawn into */

    /// <summary>Makes the window what is drawn into, with the viewport over all of it.</summary>
    public void BindWindow() => BindRenderTarget(null);

    /// <summary>Makes a render target what is drawn into (the window for null), with the viewport over all of it.</summary>
    public void BindRenderTarget(RenderTarget? target)
    {
        bool window = target is null;
        if (window == targetIsWindow && ReferenceEquals(target, boundTarget))
            return;

        // A target that was cleared and then never drawn into still has to come out cleared
        if (renderingActive && !ReferenceEquals(renderingTarget, target)) EndRendering();
        if (boundTarget is { } previous && !ReferenceEquals(previous, target)) FlushPendingClears(previous);

        boundTarget = target;
        targetIsWindow = window;
        Statistics.Counting.RenderTargetBinds++;

        if (window) SetViewport(0, 0, swapchain.Width, swapchain.Height);
        else SetViewport(0, 0, target!.Width, target.Height);
    }

    /// <summary>The render target that is drawn into, null for the window.</summary>
    public RenderTarget? BoundRenderTarget => targetIsWindow ? null : boundTarget;

    /// <summary>Sets what of the bound target is drawn to, in pixels from its top left.</summary>
    public void SetViewport(int x, int y, uint width, uint height)
    {
        viewportX = x;
        viewportY = y;
        viewportWidth = Math.Max(1, width);
        viewportHeight = Math.Max(1, height);
        viewportDirty = true;
    }

    /* State the renderers set around their draws */

    /// <summary>Whether what is drawn is mixed with what is there (the way the mode says) or simply replaces it.</summary>
    public void SetBlend(bool enabled, in BlendMode mode)
    {
        blend = enabled;
        blendMode = mode;
    }

    public void SetColorWrite(bool enabled) => colorWrite = enabled;

    public void SetDepthTest(bool enabled)
    {
        depthTest = enabled;
        dynamicDirty = true;
    }

    /// <summary>Which side of every triangle is thrown away from here on, see <see cref="CullMode"/>. Part of the pipeline, not a dynamic state.</summary>
    public void SetCullMode(CullMode mode) => cullMode = mode;

    public void SetDepthWrite(bool enabled)
    {
        depthWrite = enabled;
        dynamicDirty = true;
    }

    public void SetStencilTest(bool enabled)
    {
        stencilTest = enabled;
        dynamicDirty = true;
    }

    public void SetStencilWrite(uint mask)
    {
        stencilWriteMask = mask;
        dynamicDirty = true;
    }

    public void SetStencilFunction(CompareFunction function, int reference, uint mask)
    {
        stencilFunction = function;
        stencilReference = reference;
        stencilCompareMask = mask;
        dynamicDirty = true;
    }

    public void SetStencilOperation(StencilAction onFail, StencilAction onDepthFail, StencilAction onPass)
    {
        stencilFail = onFail;
        stencilDepthFail = onDepthFail;
        stencilPass = onPass;
        dynamicDirty = true;
    }

    /// <summary>What <see cref="Clear"/> fills the colour with.</summary>
    public Vector4 ClearColor
    {
        get => clearColor;
        set => clearColor = value;
    }

    /* Getting a draw ready */

    /// <summary>Helper method to put everything a draw needs into the command buffer, the pipeline, the dynamic state, the descriptors and the vertices.</summary>
    private bool PrepareDraw(Topology topology)
    {
        if (boundShader is null || boundShader.IsCompute) return false;
        if (targetIsWindow && !Frame.HasImage) return false;

        BoundTechnique?.Flush(this);
        PrepareTextures(drawing: true);
        EnsureRendering();

        var key = new PipelineCache.GraphicsKey(
            boundShader.Handle, blend, blendMode, colorWrite, topology, cullMode,
            boundVertexArray?.LayoutKey ?? 0,
            targetIsWindow ? WindowFormatKey : boundTarget!.FormatKey);

        Pipeline pipeline = targetIsWindow
            ? Pipelines.GetGraphics(key, boundShader, boundVertexArray, [swapchain.Format], Format.Undefined, false)
            : Pipelines.GetGraphics(key, boundShader, boundVertexArray, boundTarget!.ColorFormats, boundTarget.DepthFormat, boundTarget.HasStencil);

        if (pipeline.Handle != currentPipeline.Handle)
        {
            Vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);
            currentPipeline = pipeline;
            Statistics.Counting.PipelineBinds++;
            dynamicDirty = viewportDirty = true;
        }

        if (viewportDirty)
        {
            var viewport = new Viewport(viewportX, viewportY, viewportWidth, viewportHeight, 0.0f, 1.0f);
            var scissor = new Rect2D(new Offset2D(viewportX, viewportY), new Extent2D(viewportWidth, viewportHeight));
            Vk.CmdSetViewport(cmd, 0, 1, in viewport);
            Vk.CmdSetScissor(cmd, 0, 1, in scissor);
            viewportDirty = false;
        }

        if (dynamicDirty)
        {
            Vk.CmdSetDepthTestEnable(cmd, depthTest);
            Vk.CmdSetDepthWriteEnable(cmd, depthWrite);
            Vk.CmdSetStencilTestEnable(cmd, stencilTest);
            Vk.CmdSetStencilOp(cmd, StencilFaceFlags.FaceFrontAndBack, Of(stencilFail), Of(stencilPass), Of(stencilDepthFail), Of(stencilFunction));
            Vk.CmdSetStencilCompareMask(cmd, StencilFaceFlags.FaceFrontAndBack, stencilCompareMask);
            Vk.CmdSetStencilWriteMask(cmd, StencilFaceFlags.FaceFrontAndBack, stencilWriteMask);
            Vk.CmdSetStencilReference(cmd, StencilFaceFlags.FaceFrontAndBack, (uint)stencilReference);
            Vk.CmdSetLineWidth(cmd, 1.0f);
            dynamicDirty = false;
        }

        BindDescriptors(PipelineBindPoint.Graphics);

        if (boundVertexArray is { } array)
        {
            foreach (var (binding, description) in array.BindingList)
            {
                if (description.Buffer is not { IsValid: true } buffer || buffer.Buffer.Handle == 0) continue;

                Buffer handle = buffer.Buffer;
                ulong offset = (ulong)description.Offset;
                Vk.CmdBindVertexBuffers(cmd, binding, 1, in handle, in offset);
                buffer.LastUse = NextSubmission;
            }

            if (array.IndexBuffer is { IsValid: true } indices && indices.Buffer.Handle != 0)
            {
                Vk.CmdBindIndexBuffer(cmd, indices.Buffer, 0, IndexType.Uint32);
                indices.LastUse = NextSubmission;
            }
        }

        return true;
    }

    /// <summary>Helper method to put everything a dispatch needs into the command buffer.</summary>
    private bool PrepareDispatch()
    {
        if (boundShader is not { IsCompute: true }) return false;

        BoundTechnique?.Flush(this);
        EndRendering();
        PrepareTextures(drawing: false);
        PrepareImages();

        Pipeline pipeline = Pipelines.GetCompute(boundShader);
        if (pipeline.Handle != currentPipeline.Handle)
        {
            Vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, pipeline);
            currentPipeline = pipeline;
            Statistics.Counting.PipelineBinds++;
        }

        BindDescriptors(PipelineBindPoint.Compute);
        return true;
    }

    /// <summary>
    /// Helper method to have every bound texture in a layout a shader can read it in. For a draw, an attachment of
    /// the target that is drawn into is left alone, reading that is a feedback loop, undefined on every API there is.
    /// A compute pass is outside of any rendering and may read what was drawn so far.
    /// </summary>
    private void PrepareTextures(bool drawing)
    {
        foreach (Texture? texture in boundTextures)
        {
            if (texture is null || !texture.IsValid) continue;

            if (drawing && !targetIsWindow && boundTarget!.Owns(texture)) continue;

            if (texture.Layout != ImageLayout.ShaderReadOnlyOptimal || (texture.LastAccess & WriteAccess) != 0)
                Transition(texture, ImageLayout.ShaderReadOnlyOptimal, PipelineStageFlags2.AllGraphicsBit | PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit);
        }
    }

    private void PrepareImages()
    {
        foreach (Texture? image in boundImages)
        {
            if (image is null || !image.IsValid) continue;
            Transition(image, ImageLayout.General, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
        }
    }

    private const AccessFlags2 WriteAccess = AccessFlags2.ShaderWriteBit | AccessFlags2.ShaderStorageWriteBit | AccessFlags2.ColorAttachmentWriteBit | AccessFlags2.DepthStencilAttachmentWriteBit | AccessFlags2.TransferWriteBit | AccessFlags2.MemoryWriteBit;

    /// <summary>Helper method to make the descriptor sets for what is bound, when anything of it changed, and bind them.</summary>
    private void BindDescriptors(PipelineBindPoint bindPoint)
    {
        FrameResources frame = Frame;

        if (buffersDirty)
        {
            bufferSet = frame.Descriptors.Allocate(Layouts.Buffers);
            Statistics.Counting.DescriptorSets++;

            var infos = stackalloc DescriptorBufferInfo[2 + ShaderPreprocessor.STORAGE_BINDINGS];
            var writes = stackalloc WriteDescriptorSet[2 + ShaderPreprocessor.STORAGE_BINDINGS];
            uint count = 0;

            infos[0] = new DescriptorBufferInfo(frame.Uniforms.Buffer, 0, UNIFORM_RANGE);
            writes[count++] = Write(bufferSet, DescriptorLayouts.CAMERA_BINDING, DescriptorType.UniformBufferDynamic, &infos[0]);
            infos[1] = new DescriptorBufferInfo(frame.Uniforms.Buffer, 0, UNIFORM_RANGE);
            writes[count++] = Write(bufferSet, DescriptorLayouts.PARAMS_BINDING, DescriptorType.UniformBufferDynamic, &infos[1]);

            for (uint i = 0; i < ShaderPreprocessor.STORAGE_BINDINGS; i++)
            {
                var (buffer, offset, size) = boundStorage[i];
                if (buffer is not { IsValid: true } || buffer.Buffer.Handle == 0 || size == 0) continue;

                infos[2 + i] = new DescriptorBufferInfo(buffer.Buffer, offset, size);
                writes[count++] = Write(bufferSet, DescriptorLayouts.FIRST_STORAGE_BINDING + i, DescriptorType.StorageBuffer, &infos[2 + i]);
                buffer.LastUse = NextSubmission;
            }

            Vk.UpdateDescriptorSets(Device, count, writes, 0, null);
            buffersDirty = false;
            offsetsDirty = true;
            setsBoundFor = (PipelineBindPoint)(-1);
        }

        if (imagesDirty)
        {
            imageSet = frame.Descriptors.Allocate(Layouts.Images);
            Statistics.Counting.DescriptorSets++;

            int most = ShaderPreprocessor.TEXTURE_BINDINGS * 2 + ShaderPreprocessor.IMAGE_BINDINGS;
            var infos = stackalloc DescriptorImageInfo[most];
            var writes = stackalloc WriteDescriptorSet[most];
            uint count = 0;

            for (uint i = 0; i < ShaderPreprocessor.TEXTURE_BINDINGS; i++)
            {
                Texture? texture = boundTextures[i];
                if (texture is not { IsValid: true }) continue;

                Sampler sampler = SamplerOf(boundSamplers[i], texture);
                infos[i] = new DescriptorImageInfo(sampler, texture.View, ImageLayout.ShaderReadOnlyOptimal);
                writes[count++] = WriteImage(imageSet, i, DescriptorType.CombinedImageSampler, &infos[i]);
                texture.LastUse = NextSubmission;

                // The same sampler on its own, for an HLSL shader that keeps its textures and samplers apart
                int at = ShaderPreprocessor.TEXTURE_BINDINGS + ShaderPreprocessor.IMAGE_BINDINGS + (int)i;
                infos[at] = new DescriptorImageInfo(sampler, default, ImageLayout.Undefined);
                writes[count++] = WriteImage(imageSet, DescriptorLayouts.FIRST_SAMPLER_BINDING + i, DescriptorType.Sampler, &infos[at]);
            }

            for (uint i = 0; i < ShaderPreprocessor.IMAGE_BINDINGS; i++)
            {
                Texture? image = boundImages[i];
                if (image is not { IsValid: true }) continue;

                int at = ShaderPreprocessor.TEXTURE_BINDINGS + (int)i;
                infos[at] = new DescriptorImageInfo(default, image.View, ImageLayout.General);
                writes[count++] = WriteImage(imageSet, DescriptorLayouts.FIRST_IMAGE_BINDING + i, DescriptorType.StorageImage, &infos[at]);
                image.LastUse = NextSubmission;
            }

            if (count > 0) Vk.UpdateDescriptorSets(Device, count, writes, 0, null);
            imagesDirty = false;
            setsBoundFor = (PipelineBindPoint)(-1);
        }

        if (offsetsDirty || setsBoundFor != bindPoint)
        {
            var sets = stackalloc DescriptorSet[3] { bufferSet, imageSet, Bindless.Set };
            var offsets = stackalloc uint[2] { (uint)cameraOffset, (uint)paramsOffset };
            Vk.CmdBindDescriptorSets(cmd, bindPoint, Layouts.PipelineLayout, 0, 3, sets, 2, offsets);
            offsetsDirty = false;
            setsBoundFor = bindPoint;
        }
    }

    private static WriteDescriptorSet Write(DescriptorSet set, uint binding, DescriptorType type, DescriptorBufferInfo* info) => new()
    {
        SType = StructureType.WriteDescriptorSet,
        DstSet = set,
        DstBinding = binding,
        DescriptorCount = 1,
        DescriptorType = type,
        PBufferInfo = info
    };

    private static WriteDescriptorSet WriteImage(DescriptorSet set, uint binding, DescriptorType type, DescriptorImageInfo* info) => new()
    {
        SType = StructureType.WriteDescriptorSet,
        DstSet = set,
        DstBinding = binding,
        DescriptorCount = 1,
        DescriptorType = type,
        PImageInfo = info
    };

    /// <summary>Helper method to drop a resource that is going away from every binding it is on, so no descriptor is made with it.</summary>
    internal void Unbind(GpuResource resource)
    {
        for (int i = 0; i < boundTextures.Length; i++)
        {
            if (ReferenceEquals(boundTextures[i], resource))
            {
                boundTextures[i] = null;
                imagesDirty = true;
            }
        }

        for (int i = 0; i < boundImages.Length; i++)
        {
            if (ReferenceEquals(boundImages[i], resource))
            {
                boundImages[i] = null;
                imagesDirty = true;
            }
        }

        for (int i = 0; i < boundStorage.Length; i++)
        {
            if (ReferenceEquals(boundStorage[i].Buffer, resource))
            {
                boundStorage[i] = default;
                buffersDirty = true;
            }
        }

        if (ReferenceEquals(boundShader, resource)) boundShader = null;
        if (ReferenceEquals(boundVertexArray, resource)) boundVertexArray = null;
        if (ReferenceEquals(boundTarget, resource))
        {
            if (renderingActive && ReferenceEquals(renderingTarget, resource)) EndRendering();
            boundTarget = null;
            targetIsWindow = true;
        }
    }

    private ulong WindowFormatKey => 0x57494E444F570000UL ^ (ulong)swapchain.Format;

    private static StencilOp Of(StencilAction action) => action switch
    {
        StencilAction.Zero => StencilOp.Zero,
        StencilAction.Replace => StencilOp.Replace,
        StencilAction.Increment => StencilOp.IncrementAndClamp,
        StencilAction.Decrement => StencilOp.DecrementAndClamp,
        StencilAction.Invert => StencilOp.Invert,
        _ => StencilOp.Keep
    };

    private static CompareOp Of(CompareFunction function) => function switch
    {
        CompareFunction.Never => CompareOp.Never,
        CompareFunction.Less => CompareOp.Less,
        CompareFunction.LessEqual => CompareOp.LessOrEqual,
        CompareFunction.Equal => CompareOp.Equal,
        CompareFunction.NotEqual => CompareOp.NotEqual,
        CompareFunction.Greater => CompareOp.Greater,
        CompareFunction.GreaterEqual => CompareOp.GreaterOrEqual,
        _ => CompareOp.Always
    };
}
