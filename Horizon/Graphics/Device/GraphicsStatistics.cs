namespace Horizon.Graphics;

/// <summary>
/// What the device did over the last frame, for the performance overlay. How many draws, how many times a pipeline
/// or a render target changed, how much went up to the GPU. The device counts into the frame being drawn and hands
/// it over at the end of the frame, so whoever reads it (from any thread) sees whole frames.
/// </summary>
public sealed class GraphicsStatistics
{
    /// <summary>One frame's worth of counts.</summary>
    public struct Frame
    {
        public int DrawCalls;
        public int Instances;
        public int Dispatches;
        public int ShaderBinds;
        public int PipelineBinds;
        public int RenderTargetBinds;
        public int DescriptorSets;

        /// <summary>Pushes of descriptors (set 1 where the card pushes them), each in place of a set allocated and written.</summary>
        public int DescriptorPushes;

        /// <summary>Textures written straight from the CPU (host image copy), each in place of a staged upload.</summary>
        public int HostCopies;
        public int Barriers;
        public long BytesUploaded;
        public long BytesStreamed;
    }

    private Frame counting;
    private Frame last;

    /// <summary>The last whole frame.</summary>
    public Frame Last => last;

    /// <summary>How many bytes of GPU memory the device has out at the moment.</summary>
    public long MemoryInUse { get; internal set; }

    /// <summary>How many blocks of memory have been taken from the driver.</summary>
    public int MemoryBlocks { get; internal set; }

    /// <summary>How many bytes those blocks come to all together, used or not.</summary>
    public long MemoryBlockBytes { get; internal set; }

    /// <summary>How many bytes of them the CPU can see, which is the machine's memory and not the card's.</summary>
    public long MemoryHostBytes { get; internal set; }

    /// <summary>How many resources are alive.</summary>
    public int Buffers { get; internal set; }
    public int Textures { get; internal set; }
    public int Pipelines { get; internal set; }

    internal ref Frame Counting => ref counting;

    /// <summary>The frame is done, what was counted is what is shown from here on.</summary>
    internal void EndFrame()
    {
        last = counting;
        counting = default;
    }
}
