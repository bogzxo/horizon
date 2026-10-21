using Horizon.Logging;

using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace Horizon.Graphics.Vulkan;

/// <summary>
/// The pipelines, one for every way a shader has been drawn with, which attachments, what blending, which vertex
/// layout, what topology. Most of what OpenGL called state is dynamic in Vulkan 1.3 (the viewport, the depth and
/// stencil tests, the stencil masks), so the key is short and a shader rarely needs more than two or three. The
/// driver's own cache of what it compiled is kept on disk between runs, beside the SPIR-V.
/// </summary>
internal sealed unsafe class PipelineCache : IDisposable
{
    /// <summary>Everything a graphics pipeline is told that isn't dynamic.</summary>
    public readonly record struct GraphicsKey(
        uint Shader,
        bool Blend,
        BlendMode BlendMode,
        bool ColorWrite,
        Topology Topology,
        CullMode Cull,
        ulong VertexLayout,
        ulong Attachments);

    private const string CACHE_FILE = "pipelines.cache";

    private readonly VulkanContext context;
    private readonly DescriptorLayouts layouts;
    private readonly Dictionary<GraphicsKey, Pipeline> graphics = [];
    private readonly Dictionary<uint, Pipeline> compute = [];
    private readonly Lock gate = new();

    private Silk.NET.Vulkan.PipelineCache cache;

    public int Count => graphics.Count + compute.Count;

    // How long after the last new pipeline the cache is written down, a burst of them at a scene's start is one write
    private const int SAVE_AFTER_MS = 3000;

    // Writes the cache a while after pipelines were made, so a game that crashes or is killed keeps what it compiled
    private readonly Timer saver;
    private bool disposed;

    // The header every driver starts its cache data with (VkPipelineCacheHeaderVersionOne), in bytes
    private const int HEADER_SIZE = 32;

    /// <summary>
    /// Helper method to say whether cache data on disk was made by this driver on this card. The driver is meant to
    /// turn down another's, and some of them crash on it instead, so it's checked here first.
    /// </summary>
    private bool Fits(byte[] data)
    {
        if (data.Length < HEADER_SIZE) return false;

        uint headerSize = BitConverter.ToUInt32(data, 0), version = BitConverter.ToUInt32(data, 4);
        uint vendor = BitConverter.ToUInt32(data, 8), device = BitConverter.ToUInt32(data, 12);
        if (headerSize < HEADER_SIZE || version != (uint)PipelineCacheHeaderVersion.One) return false;

        PhysicalDeviceProperties properties = context.Properties;
        if (vendor != properties.VendorID || device != properties.DeviceID) return false;

        for (int i = 0; i < 16; i++)
        {
            if (data[16 + i] != properties.PipelineCacheUuid[i]) return false;
        }

        return true;
    }

    /// <summary>Helper method to have the cache written in a little while, or later still if more pipelines come.</summary>
    private void SaveSoon()
    {
        if (!disposed && ShaderCompiler.CacheDirectory() is not null)
            saver.Change(SAVE_AFTER_MS, Timeout.Infinite);
    }

    private void SaveQuietly()
    {
        lock (gate)
        {
            if (!disposed) Save();
        }
    }

    public PipelineCache(VulkanContext context, DescriptorLayouts layouts)
    {
        this.context = context;
        this.layouts = layouts;

        byte[] kept = [];
        string? folder = ShaderCompiler.CacheDirectory();
        if (folder is not null)
        {
            try
            {
                string path = Path.Combine(folder, CACHE_FILE);
                if (File.Exists(path)) kept = File.ReadAllBytes(path);
            }
            catch (Exception)
            {
                kept = [];
            }

            if (kept.Length > 0 && !Fits(kept))
            {
                Log.Info("[Vulkan] The pipeline cache on disk is another driver's or another card's, starting it over.");
                kept = [];
            }
        }

        saver = new Timer(_ => SaveQuietly(), null, Timeout.Infinite, Timeout.Infinite);

        fixed (byte* data = kept)
        {
            var info = new PipelineCacheCreateInfo
            {
                SType = StructureType.PipelineCacheCreateInfo,
                InitialDataSize = (nuint)kept.Length,
                PInitialData = data
            };

            // A cache from another driver is turned down, which only means starting over
            if (context.Vk.CreatePipelineCache(context.Device, in info, null, out cache) != Result.Success)
            {
                info.InitialDataSize = 0;
                info.PInitialData = null;
                VulkanContext.Check(context.Vk.CreatePipelineCache(context.Device, in info, null, out cache), "making the pipeline cache");
            }
        }
    }

    /// <summary>The pipeline for a shader drawn a certain way, made the first time it is asked for.</summary>
    public Pipeline GetGraphics(in GraphicsKey key, Shader shader, VertexArray? vertices, ReadOnlySpan<Format> colorFormats, Format depthFormat, bool hasStencil)
    {
        lock (gate)
        {
            if (graphics.TryGetValue(key, out Pipeline existing)) return existing;

            Pipeline made = MakeGraphics(key, shader, vertices, colorFormats, depthFormat, hasStencil);
            graphics[key] = made;
            SaveSoon();
            return made;
        }
    }

    public Pipeline GetCompute(Shader shader)
    {
        lock (gate)
        {
            if (compute.TryGetValue(shader.Handle, out Pipeline existing)) return existing;

            Pipeline made = MakeCompute(shader);
            compute[shader.Handle] = made;
            SaveSoon();
            return made;
        }
    }

    /// <summary>Forgets every pipeline of a shader that is going away. The pipelines are destroyed by the caller once the GPU is done with them.</summary>
    public List<Pipeline> Forget(Shader shader)
    {
        var gone = new List<Pipeline>();
        lock (gate)
        {
            foreach (var (key, pipeline) in graphics.ToArray())
            {
                if (key.Shader != shader.Handle) continue;
                graphics.Remove(key);
                gone.Add(pipeline);
            }

            if (compute.Remove(shader.Handle, out Pipeline computePipeline)) gone.Add(computePipeline);
        }

        return gone;
    }

    private Pipeline MakeGraphics(in GraphicsKey key, Shader shader, VertexArray? vertices, ReadOnlySpan<Format> colorFormats, Format depthFormat, bool hasStencil)
    {
        var vk = context.Vk;
        var entries = new nint[shader.Modules.Count];

        var stages = new PipelineShaderStageCreateInfo[shader.Modules.Count];
        for (int i = 0; i < stages.Length; i++)
        {
            var stage = shader.Modules[i];
            entries[i] = SilkMarshal.StringToPtr(stage.EntryPoint);
            stages[i] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = StageOf(stage.Kind),
                Module = stage.Module,
                PName = (byte*)entries[i]
            };
        }

        // The vertex input is whatever the vertex array says it is, nothing for the passes that make their vertices up
        var bindings = new List<VertexInputBindingDescription>();
        var attributes = new List<VertexInputAttributeDescription>();
        if (vertices is not null)
        {
            foreach (var (binding, description) in vertices.Bindings)
            {
                bindings.Add(new VertexInputBindingDescription
                {
                    Binding = binding,
                    Stride = description.Stride,
                    InputRate = description.Divisor > 0 ? VertexInputRate.Instance : VertexInputRate.Vertex
                });
            }

            foreach (var attribute in vertices.Attributes)
            {
                attributes.Add(new VertexInputAttributeDescription
                {
                    Location = attribute.Index,
                    Binding = attribute.Binding,
                    Format = VertexArray.FormatOf(attribute.Attribute),
                    Offset = (uint)attribute.Attribute.Offset
                });
            }
        }

        var bindingArray = bindings.ToArray();
        var attributeArray = attributes.ToArray();
        var formats = colorFormats.ToArray();

        // Every colour attachment blends alike, the way glEnable(GL_BLEND) had it
        var blendAttachments = new PipelineColorBlendAttachmentState[formats.Length];
        for (int i = 0; i < formats.Length; i++)
        {
            blendAttachments[i] = new PipelineColorBlendAttachmentState
            {
                BlendEnable = key.Blend,
                SrcColorBlendFactor = FactorOf(key.BlendMode.SourceColor),
                DstColorBlendFactor = FactorOf(key.BlendMode.DestinationColor),
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = FactorOf(key.BlendMode.SourceAlpha),
                DstAlphaBlendFactor = FactorOf(key.BlendMode.DestinationAlpha),
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = key.ColorWrite
                    ? ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit
                    : 0
            };
        }

        var dynamicStates = stackalloc DynamicState[10]
        {
            DynamicState.Viewport, DynamicState.Scissor,
            DynamicState.DepthTestEnable, DynamicState.DepthWriteEnable,
            DynamicState.StencilTestEnable, DynamicState.StencilOp,
            DynamicState.StencilCompareMask, DynamicState.StencilWriteMask, DynamicState.StencilReference,
            DynamicState.LineWidth
        };

        fixed (PipelineShaderStageCreateInfo* stagePointer = stages)
        fixed (VertexInputBindingDescription* bindingPointer = bindingArray)
        fixed (VertexInputAttributeDescription* attributePointer = attributeArray)
        fixed (PipelineColorBlendAttachmentState* blendPointer = blendAttachments)
        fixed (Format* formatPointer = formats)
        {
            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = (uint)bindingArray.Length,
                PVertexBindingDescriptions = bindingPointer,
                VertexAttributeDescriptionCount = (uint)attributeArray.Length,
                PVertexAttributeDescriptions = attributePointer
            };

            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = TopologyOf(key.Topology),
                PrimitiveRestartEnable = false
            };

            var viewportState = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                ScissorCount = 1
            };

            var rasterization = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                CullMode = key.Cull switch { CullMode.Back => CullModeFlags.BackBit, CullMode.Front => CullModeFlags.FrontBit, _ => CullModeFlags.None },

                // Anticlockwise is the front, as the triangles of every shape in the engine go round seen from
                // outside. A first try at reasoning this through the clip correction's Y flip came out Clockwise
                // and culled every front in the engine, the sphere showed its inside and the floor vanished.
                // Checked by looking at a cube with its backs culled, which is the only argument that counts here
                FrontFace = FrontFace.CounterClockwise,
                LineWidth = 1.0f
            };

            var multisample = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit
            };

            var depthStencil = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthCompareOp = CompareOp.Less,
                DepthBoundsTestEnable = false
            };

            var colorBlend = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = (uint)blendAttachments.Length,
                PAttachments = blendPointer
            };

            var dynamic = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 10,
                PDynamicStates = dynamicStates
            };

            var rendering = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = (uint)formats.Length,
                PColorAttachmentFormats = formatPointer,
                DepthAttachmentFormat = depthFormat,
                StencilAttachmentFormat = hasStencil ? depthFormat : Format.Undefined
            };

            var info = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                PNext = &rendering,
                StageCount = (uint)stages.Length,
                PStages = stagePointer,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewportState,
                PRasterizationState = &rasterization,
                PMultisampleState = &multisample,
                PDepthStencilState = &depthStencil,
                PColorBlendState = &colorBlend,
                PDynamicState = &dynamic,
                Layout = layouts.PipelineLayout
            };

            Result result = vk.CreateGraphicsPipelines(context.Device, cache, 1, in info, null, out Pipeline pipeline);
            foreach (nint entry in entries) SilkMarshal.Free(entry);
            VulkanContext.Check(result, $"making a pipeline for {shader.Name}");
            context.Name(ObjectType.Pipeline, pipeline.Handle, $"{shader.Name} pipeline");
            return pipeline;
        }
    }

    private Pipeline MakeCompute(Shader shader)
    {
        var compute = shader.Modules.First(stage => stage.Kind == ShaderStage.Compute);
        nint entry = SilkMarshal.StringToPtr(compute.EntryPoint);
        var info = new ComputePipelineCreateInfo
        {
            SType = StructureType.ComputePipelineCreateInfo,
            Stage = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.ComputeBit,
                Module = compute.Module,
                PName = (byte*)entry
            },
            Layout = layouts.PipelineLayout
        };

        Result result = context.Vk.CreateComputePipelines(context.Device, cache, 1, in info, null, out Pipeline pipeline);
        SilkMarshal.Free(entry);
        VulkanContext.Check(result, $"making a compute pipeline for {shader.Name}");
        context.Name(ObjectType.Pipeline, pipeline.Handle, $"{shader.Name} compute pipeline");
        return pipeline;
    }

    public static ShaderStageFlags StageOf(ShaderStage stage) => stage switch
    {
        ShaderStage.Vertex => ShaderStageFlags.VertexBit,
        ShaderStage.Fragment => ShaderStageFlags.FragmentBit,
        ShaderStage.Compute => ShaderStageFlags.ComputeBit,
        _ => ShaderStageFlags.GeometryBit
    };

    private static PrimitiveTopology TopologyOf(Topology topology) => topology switch
    {
        Topology.Triangles => PrimitiveTopology.TriangleList,
        Topology.TriangleStrip => PrimitiveTopology.TriangleStrip,
        Topology.Lines => PrimitiveTopology.LineList,
        Topology.LineStrip => PrimitiveTopology.LineStrip,
        _ => PrimitiveTopology.PointList
    };

    private static Silk.NET.Vulkan.BlendFactor FactorOf(BlendFactor factor) => factor switch
    {
        BlendFactor.Zero => Silk.NET.Vulkan.BlendFactor.Zero,
        BlendFactor.One => Silk.NET.Vulkan.BlendFactor.One,
        BlendFactor.SrcColor => Silk.NET.Vulkan.BlendFactor.SrcColor,
        BlendFactor.OneMinusSrcColor => Silk.NET.Vulkan.BlendFactor.OneMinusSrcColor,
        BlendFactor.DstColor => Silk.NET.Vulkan.BlendFactor.DstColor,
        BlendFactor.OneMinusDstColor => Silk.NET.Vulkan.BlendFactor.OneMinusDstColor,
        BlendFactor.SrcAlpha => Silk.NET.Vulkan.BlendFactor.SrcAlpha,
        BlendFactor.OneMinusSrcAlpha => Silk.NET.Vulkan.BlendFactor.OneMinusSrcAlpha,
        BlendFactor.DstAlpha => Silk.NET.Vulkan.BlendFactor.DstAlpha,
        _ => Silk.NET.Vulkan.BlendFactor.OneMinusDstAlpha
    };

    /// <summary>Writes what the driver compiled to disk, for next time.</summary>
    public void Save()
    {
        string? folder = ShaderCompiler.CacheDirectory();
        if (folder is null) return;

        try
        {
            nuint size = 0;
            context.Vk.GetPipelineCacheData(context.Device, cache, &size, null);
            if (size == 0) return;

            var data = new byte[size];
            fixed (byte* pointer = data)
                context.Vk.GetPipelineCacheData(context.Device, cache, &size, pointer);

            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, CACHE_FILE);
            File.WriteAllBytes(path + ".partial", data);
            File.Move(path + ".partial", path, overwrite: true);
        }
        catch (Exception e)
        {
            Log.Warning($"[Vulkan] The pipeline cache couldn't be kept: {e.Message}");
        }
    }

    public void Dispose()
    {
        // The timer stops before the cache goes, one that fires now finds it disposed under the gate
        saver.Dispose();

        lock (gate)
        {
            if (disposed) return;
            disposed = true;

            Save();

            foreach (var pipeline in graphics.Values) context.Vk.DestroyPipeline(context.Device, pipeline, null);
            foreach (var pipeline in compute.Values) context.Vk.DestroyPipeline(context.Device, pipeline, null);
            graphics.Clear();
            compute.Clear();

            context.Vk.DestroyPipelineCache(context.Device, cache, null);
        }
    }
}
