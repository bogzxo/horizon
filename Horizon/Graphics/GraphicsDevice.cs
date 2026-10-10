using System.Numerics;

using Horizon.Graphics.Vulkan;
using Horizon.Logging;

using Silk.NET.Core.Contexts;
using Silk.NET.Vulkan;

using PipelineCache = Horizon.Graphics.Vulkan.PipelineCache;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Horizon.Graphics;

/// <summary>
/// The GPU. Everything that draws goes through here and through the resource classes (buffers, textures, render
/// targets, vertex arrays, shaders and techniques), which are thin and Vulkan all the way down. There is one of it,
/// <see cref="Current"/>, made by the window along with its surface.
/// <code>
/// var device = GraphicsDevice.Current;
/// technique.Bind();
/// device.DrawIndexedInstanced(Topology.Triangles, 6, count);
/// </code>
/// A frame runs from <see cref="BeginFrame"/> to <see cref="EndFrame"/>, which is when the picture goes to the
/// screen. In between, binding things and drawing records into the frame's command buffer. Rendering into a target
/// is begun the moment something is drawn into it and ended the moment something else is needed (a clear of another
/// target, an upload, a compute dispatch), so the renderers never have to think about render passes. Two frames are
/// in flight, so while the GPU draws one the CPU records the next, and whatever a frame writes from the CPU (the uniform
/// blocks of its draws, what it uploads) goes into memory that frame alone owns.
/// Render thread only, like everything that touches the GPU, except for what says it isn't.
/// <para>
/// For whoever wants to go underneath, <see cref="Vk"/>, <see cref="Device"/> and <see cref="CommandBuffer"/> are
/// the real things, and <see cref="EndRendering"/> gets you out of the rendering instance so you can record your own
/// barriers and copies. The engine won't notice as long as everything is back the way it was found.
/// </para>
/// </summary>
public sealed unsafe partial class GraphicsDevice : IDisposable
{
    /// <summary>How many frames are in flight at once, one being drawn by the GPU while the other is recorded.</summary>
    public const int FRAMES_IN_FLIGHT = 2;

    private static GraphicsDevice? current;

    /// <summary>The device there is. Throws before the window has made one.</summary>
    public static GraphicsDevice Current => current ?? throw new InvalidOperationException("There is no graphics device yet, the window makes it along with its surface.");

    /// <summary>Whether there is a device to be had yet.</summary>
    public static bool IsAvailable => current is not null;

    internal readonly VulkanContext Context;
    internal readonly VulkanMemory Memory;
    internal readonly BindlessTextures Bindless;
    internal readonly DescriptorLayouts Layouts;
    internal readonly PipelineCache Pipelines;

    private readonly Swapchain swapchain;
    private readonly FrameResources[] frames = new FrameResources[FRAMES_IN_FLIGHT];
    private int frameIndex;

    // The timeline every submission signals, counting up. A resource used in a submission is free again once the
    // timeline has got past that submission's value
    private readonly Semaphore timeline;
    private ulong submitted;
    private ulong completed;

    private CommandBuffer cmd;
    private bool swapchainStale;
    private bool vsync;
    private Vector4 clearColor;

    private QueryPool timestamps;
    private readonly double[] frameGpuMs = new double[FRAMES_IN_FLIGHT];

    // The scopes of every frame in flight, named as they are opened and read back when the frame is done. Two
    // queries for the frame itself and two for every scope
    private const int MAX_SCOPES = 48;
    private const int QUERIES_PER_FRAME = 2 + MAX_SCOPES * 2;
    private readonly string[][] scopeNames = new string[FRAMES_IN_FLIGHT][];
    private readonly int[][] scopeDepths = new int[FRAMES_IN_FLIGHT][];
    private readonly int[] scopeCounts = new int[FRAMES_IN_FLIGHT];
    private readonly Stack<int> openScopes = new();
    private GpuScope[] gpuScopes = new GpuScope[MAX_SCOPES];
    private int gpuScopeCount;

    private readonly Dictionary<SamplerSettings, Sampler> samplersBySettings = [];
    private readonly Dictionary<uint, Sampler> samplersById = [];
    private uint nextSamplerId = 1;

    /// <summary>The Vulkan API, for whoever goes underneath.</summary>
    public Vk Vk => Context.Vk;

    /// <summary>The logical device.</summary>
    public Device Device => Context.Device;

    /// <summary>The physical device, the card.</summary>
    public PhysicalDevice PhysicalDevice => Context.PhysicalDevice;

    /// <summary>The command buffer of the frame, as it is being recorded.</summary>
    public CommandBuffer CommandBuffer => cmd;

    /// <summary>The queue frames are submitted to. Take <see cref="QueueLock"/> before using it.</summary>
    public Queue GraphicsQueue => Context.GraphicsQueue;

    /// <summary>Held for every submission, on every queue.</summary>
    public Lock QueueLock => Context.QueueLock;

    /// <summary>What the device calls itself, with the card and the driver, for the log and the overlays.</summary>
    public string Description { get; }

    /// <summary>The widest (and tallest) a texture can be, in texels.</summary>
    public uint MaxTextureSize => Context.Limits.MaxImageDimension2D;

    /// <summary>The alignment (in bytes) an offset into a storage buffer has to have to be bound as a range.</summary>
    public uint StorageOffsetAlignment => (uint)Math.Max(1, Context.Limits.MinStorageBufferOffsetAlignment);

    /// <summary>The alignment (in bytes) an offset into a uniform buffer has to have to be bound as a range.</summary>
    public uint UniformOffsetAlignment => (uint)Math.Max(1, Context.Limits.MinUniformBufferOffsetAlignment);

    /// <summary>How many frames have been begun.</summary>
    public ulong FrameNumber { get; private set; }

    /// <summary>What the device did over the last frame.</summary>
    public GraphicsStatistics Statistics { get; } = new();

    /// <summary>How long (in milliseconds) the GPU spent on the last frame it finished, 0 where the card doesn't say.</summary>
    public double GpuFrameMilliseconds { get; private set; }

    /// <summary>
    /// How long the named stretches of the last frame the GPU finished took, in the order they were opened, see
    /// <see cref="BeginGpuScope"/>. Empty without timestamps.
    /// </summary>
    public ReadOnlySpan<GpuScope> GpuScopes => gpuScopes.AsSpan(0, gpuScopeCount);

    /// <summary>
    /// Vulkan's clip space has Y going down and depth from 0 to 1, the engine's cameras are built the OpenGL way
    /// (Y up, depth -1 to 1). Every projection is multiplied by this on its way into the camera block, and nothing
    /// else has to know.
    /// </summary>
    public static Matrix4x4 ClipCorrection { get; } = new(
        1.0f, 0.0f, 0.0f, 0.0f,
        0.0f, -1.0f, 0.0f, 0.0f,
        0.0f, 0.0f, 0.5f, 0.0f,
        0.0f, 0.0f, 0.5f, 1.0f);

    /// <summary>The value the next submission signals on the timeline, which is what a fence made now stands for.</summary>
    internal ulong NextSubmission => submitted + 1;

    /// <summary>Whether the GPU is done with a submission.</summary>
    internal bool IsDone(ulong submission) => submission <= completed;

    private FrameResources Frame => frames[frameIndex];

    /// <summary>
    /// Makes the device for a window's surface. The window has to have been made for Vulkan.
    /// </summary>
    public GraphicsDevice(IVkSurface surface, uint width, uint height, bool vsync)
    {
        Context = new VulkanContext(surface);
        Memory = new VulkanMemory(Context);
        Bindless = new BindlessTextures(Context);
        Layouts = new DescriptorLayouts(Context, Bindless);
        Pipelines = new PipelineCache(Context, Layouts);

        this.vsync = vsync;
        swapchain = new Swapchain(Context, Math.Max(1, width), Math.Max(1, height), vsync);

        var timelineType = new SemaphoreTypeCreateInfo
        {
            SType = StructureType.SemaphoreTypeCreateInfo,
            SemaphoreType = SemaphoreType.Timeline,
            InitialValue = 0
        };
        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo, PNext = &timelineType };
        VulkanContext.Check(Vk.CreateSemaphore(Device, in semaphoreInfo, null, out timeline), "making the timeline");

        for (int i = 0; i < FRAMES_IN_FLIGHT; i++)
            frames[i] = new FrameResources(Context, Memory, i);

        if (Context.TimestampsSupported)
        {
            var queryInfo = new QueryPoolCreateInfo
            {
                SType = StructureType.QueryPoolCreateInfo,
                QueryType = QueryType.Timestamp,
                QueryCount = FRAMES_IN_FLIGHT * QUERIES_PER_FRAME
            };
            VulkanContext.Check(Vk.CreateQueryPool(Device, in queryInfo, null, out timestamps), "making the timestamp queries");
            Context.Name(ObjectType.QueryPool, timestamps.Handle, "frame timestamps");
        }

        for (int i = 0; i < FRAMES_IN_FLIGHT; i++)
        {
            scopeNames[i] = new string[MAX_SCOPES];
            scopeDepths[i] = new int[MAX_SCOPES];
        }

        Description = $"Vulkan {Context.Properties.ApiVersion >> 22}.{(Context.Properties.ApiVersion >> 12) & 0x3FF} on {Context.DeviceName} (driver {Context.DriverVersion})";

        // The first frame is open from here on, so whatever is made before the first BeginFrame has somewhere to go
        frameIndex = 0;
        OpenFrame();

        current = this;
        Log.Info($"[Graphics] {Description}.");
    }

    /// <summary>Whether this machine has a feature, see <see cref="GraphicsFeature"/>.</summary>
    public bool Supports(GraphicsFeature feature) => feature switch
    {
        GraphicsFeature.PathTracedLighting => Context.SupportsStorageImage(Format.R16G16B16A16Sfloat) && Context.SupportsStorageImage(Format.R32G32B32A32Sfloat),
        GraphicsFeature.AsyncCompute => Context.HasAsyncCompute,
        GraphicsFeature.AsyncUploads => Context.HasTransferQueue,
        GraphicsFeature.GpuTimer => Context.TimestampsSupported,
        _ => false
    };

    /// <summary>
    /// Says in the log (once per feature) that something asked for a feature this machine hasn't got and what it is
    /// doing instead, for the places that fall back rather than fail.
    /// </summary>
    public void WarnUnsupported(GraphicsFeature feature, string fallback)
    {
        lock (warnedFeatures)
        {
            if (!warnedFeatures.Add(feature)) return;
        }

        Log.Warning($"[Graphics] {feature} isn't there on {Context.DeviceName}. {fallback}");
    }

    private readonly HashSet<GraphicsFeature> warnedFeatures = [];

    /// <summary>
    /// Has the device say what it (or the validation layer) makes of what it is asked to do, through a callback (the
    /// message, how worried it is, and its number for telling the known ones apart).
    /// </summary>
    public void OnDebugMessage(Action<string, DebugLevel, int> handler) => Context.DebugMessage = handler;

    /* The frame */

    /// <summary>
    /// Starts a frame for a window of a size. Takes the next picture of the swapchain (made anew if the window
    /// changed size) and starts the clocks. Nothing is drawn into the window until this has been called.
    /// </summary>
    public void BeginFrame(uint width, uint height)
    {
        FrameNumber++;

        if (width == 0 || height == 0)
        {
            Frame.HasImage = false;
            return;
        }

        if (swapchainStale || swapchain.Width != width || swapchain.Height != height || swapchain.VSync != vsync)
        {
            Context.WaitIdle();
            swapchain.Recreate(width, height, vsync);
            swapchainStale = false;
        }

        if (!swapchain.Acquire(Frame.ImageAvailable, out bool suboptimal))
        {
            // Out of date, so it is made anew and tried once more
            Context.WaitIdle();
            swapchain.Recreate(width, height, vsync);
            if (!swapchain.Acquire(Frame.ImageAvailable, out suboptimal))
            {
                Frame.HasImage = false;
                return;
            }
        }

        swapchainStale = suboptimal;
        Frame.HasImage = true;
        Frame.AcquireWaited = false;
        windowPendingClear = null;
    }

    /// <summary>
    /// Ends the frame. Whatever was recorded goes to the GPU, the picture goes to the screen, and the next frame is
    /// opened (waiting for the GPU to be done with the one before it, which is what keeps two in flight).
    /// </summary>
    public void EndFrame()
    {
        EndRendering();

        FrameResources frame = Frame;
        if (frame.HasImage && swapchain.Current >= 0)
        {
            if (windowPendingClear is { } pending)
            {
                // Cleared and then never drawn into, which is a frame of one colour
                int index = swapchain.Current;
                TransitionSwapchainImage(index, ImageLayout.TransferDstOptimal, PipelineStageFlags2.TransferBit, AccessFlags2.TransferWriteBit);
                var value = new ClearColorValue(pending.X, pending.Y, pending.Z, pending.W);
                var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
                Vk.CmdClearColorImage(cmd, swapchain.Images[index], ImageLayout.TransferDstOptimal, in value, 1, in range);
                windowPendingClear = null;
            }

            TransitionSwapchainImage(swapchain.Current, ImageLayout.PresentSrcKhr, PipelineStageFlags2.BottomOfPipeBit, AccessFlags2.None);
        }

        // Whatever was left open is closed with the frame
        while (openScopes.Count > 0) EndGpuScope();

        if (timestamps.Handle != 0)
            Vk.CmdWriteTimestamp2(cmd, PipelineStageFlags2.AllCommandsBit, timestamps, (uint)(frameIndex * QUERIES_PER_FRAME + 1));

        Submit(final: true);

        if (frame.HasImage && !swapchain.Present(frame.RenderFinished))
            swapchainStale = true;

        Statistics.Pipelines = Pipelines.Count;
        Statistics.MemoryInUse = (long)Memory.InUse;
        Statistics.MemoryBlocks = Memory.BlockCount;
        Statistics.EndFrame();

        frameIndex = (frameIndex + 1) % FRAMES_IN_FLIGHT;
        OpenFrame();
    }

    /// <summary>
    /// Helper method to make a frame's resources free to use again (waiting for the GPU to be done with the frame
    /// that last used them), read its GPU time, and begin recording into it.
    /// </summary>
    private void OpenFrame()
    {
        FrameResources frame = Frame;
        if (frame.TimelineValue != 0)
        {
            WaitTimeline(frame.TimelineValue, ulong.MaxValue);
            ReadGpuTime();
        }

        completed = Math.Max(completed, QueryCompleted());
        frame.Reset();

        cmd = frame.BeginCommandBuffer();
        ResetRecordingState();

        // Nothing of the last frame is to be read while this one writes, see the summary
        GlobalBarrier(PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryWriteBit | AccessFlags2.MemoryReadBit, PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryWriteBit | AccessFlags2.MemoryReadBit);

        scopeCounts[frameIndex] = 0;
        openScopes.Clear();

        if (timestamps.Handle != 0)
        {
            Vk.CmdResetQueryPool(cmd, timestamps, (uint)(frameIndex * QUERIES_PER_FRAME), QUERIES_PER_FRAME);
            Vk.CmdWriteTimestamp2(cmd, PipelineStageFlags2.TopOfPipeBit, timestamps, (uint)(frameIndex * QUERIES_PER_FRAME));
        }
    }

    private void ReadGpuTime()
    {
        if (timestamps.Handle == 0) return;

        int scopes = scopeCounts[frameIndex];
        uint count = (uint)(2 + scopes * 2);
        var results = stackalloc ulong[QUERIES_PER_FRAME];
        Result result = Vk.GetQueryPoolResults(Device, timestamps, (uint)(frameIndex * QUERIES_PER_FRAME), count, (nuint)(sizeof(ulong) * count), results, sizeof(ulong), QueryResultFlags.Result64Bit);
        if (result != Result.Success) return;

        double period = Context.TimestampPeriod / 1_000_000.0;
        double ms = (results[1] - results[0]) * period;
        if (ms >= 0.0 && ms < 10_000.0) GpuFrameMilliseconds = ms;

        gpuScopeCount = 0;
        for (int i = 0; i < scopes; i++)
        {
            ulong from = results[2 + i * 2], to = results[3 + i * 2];
            double took = to >= from ? (to - from) * period : 0.0;
            gpuScopes[gpuScopeCount++] = new GpuScope(scopeNames[frameIndex][i], took, scopeDepths[frameIndex][i]);
        }
    }

    /// <summary>
    /// Opens a named stretch of the frame on the GPU, timed from here to where the token is disposed of (or the end
    /// of the frame). What it took shows up in <see cref="GpuScopes"/> once the GPU has finished the frame, a frame
    /// or two later. Scopes nest. Past a few dozen in a frame the rest aren't timed.
    /// </summary>
    public GpuScopeToken BeginGpuScope(string name)
    {
        if (!IsAvailable) return default;

        // A scope is also what the debugger folds the frame up by, which goes on past the timed ones
        bool timed = timestamps.Handle != 0 && scopeCounts[frameIndex] < MAX_SCOPES;
        if (!timed && !Context.LabelsEnabled) return default;

        Context.BeginLabel(cmd, name);

        int index = -1;
        if (timed)
        {
            index = scopeCounts[frameIndex];
            scopeNames[frameIndex][index] = name;
            scopeDepths[frameIndex][index] = openScopes.Count;
            scopeCounts[frameIndex] = index + 1;
            Vk.CmdWriteTimestamp2(cmd, PipelineStageFlags2.AllCommandsBit, timestamps, (uint)(frameIndex * QUERIES_PER_FRAME + 2 + index * 2));
        }

        openScopes.Push(index);
        return new GpuScopeToken(this);
    }

    /// <summary>Closes the scope opened last, see <see cref="BeginGpuScope"/>.</summary>
    public void EndGpuScope()
    {
        if (openScopes.Count == 0) return;

        int index = openScopes.Pop();
        if (index >= 0)
            Vk.CmdWriteTimestamp2(cmd, PipelineStageFlags2.AllCommandsBit, timestamps, (uint)(frameIndex * QUERIES_PER_FRAME + 3 + index * 2));
        Context.EndLabel(cmd);
    }

    private ulong QueryCompleted()
    {
        ulong value = 0;
        Vk.GetSemaphoreCounterValue(Device, timeline, &value);
        return value;
    }

    /// <summary>
    /// Helper method to send everything recorded so far to the GPU. For the end of the frame, and for whoever has to
    /// wait for something that was only just recorded (a fence of this frame, a read back).
    /// </summary>
    /// <param name="final">Whether this is the end of the frame, which signals the swapchain.</param>
    private void Submit(bool final)
    {
        EndRendering();
        VulkanContext.Check(Vk.EndCommandBuffer(cmd), "ending a command buffer");

        FrameResources frame = Frame;
        ulong value = ++submitted;

        var waits = stackalloc SemaphoreSubmitInfo[1];
        uint waitCount = 0;
        if (frame.HasImage && !frame.AcquireWaited)
        {
            waits[0] = new SemaphoreSubmitInfo
            {
                SType = StructureType.SemaphoreSubmitInfo,
                Semaphore = frame.ImageAvailable,
                StageMask = PipelineStageFlags2.ColorAttachmentOutputBit | PipelineStageFlags2.TransferBit
            };
            waitCount = 1;
            frame.AcquireWaited = true;
        }

        var signals = stackalloc SemaphoreSubmitInfo[2];
        signals[0] = new SemaphoreSubmitInfo
        {
            SType = StructureType.SemaphoreSubmitInfo,
            Semaphore = timeline,
            Value = value,
            StageMask = PipelineStageFlags2.AllCommandsBit
        };
        uint signalCount = 1;
        if (final && frame.HasImage)
        {
            signals[1] = new SemaphoreSubmitInfo
            {
                SType = StructureType.SemaphoreSubmitInfo,
                Semaphore = frame.RenderFinished,
                StageMask = PipelineStageFlags2.AllCommandsBit
            };
            signalCount = 2;
        }

        var commandInfo = new CommandBufferSubmitInfo { SType = StructureType.CommandBufferSubmitInfo, CommandBuffer = cmd };
        var submitInfo = new SubmitInfo2
        {
            SType = StructureType.SubmitInfo2,
            WaitSemaphoreInfoCount = waitCount,
            PWaitSemaphoreInfos = waits,
            CommandBufferInfoCount = 1,
            PCommandBufferInfos = &commandInfo,
            SignalSemaphoreInfoCount = signalCount,
            PSignalSemaphoreInfos = signals
        };

        lock (Context.QueueLock)
            VulkanContext.Check(Vk.QueueSubmit2(Context.GraphicsQueue, 1, in submitInfo, default), "submitting a frame");

        frame.TimelineValue = value;

        if (!final)
        {
            cmd = frame.BeginCommandBuffer();
            ResetRecordingState();
        }
    }

    /// <summary>Sends what has been recorded so far to the GPU and carries on recording. What it returns is the timeline value to wait for.</summary>
    public ulong Flush()
    {
        Submit(final: false);
        return submitted;
    }

    private void WaitTimeline(ulong value, ulong timeout)
    {
        Semaphore semaphore = timeline;
        var waitInfo = new SemaphoreWaitInfo
        {
            SType = StructureType.SemaphoreWaitInfo,
            SemaphoreCount = 1,
            PSemaphores = &semaphore,
            PValues = &value
        };

        Vk.WaitSemaphores(Device, in waitInfo, timeout);
        completed = Math.Max(completed, QueryCompleted());
    }

    /// <summary>Waits for the GPU to be done with everything submitted so far. Slow, for shutdown and read backs.</summary>
    public void WaitIdle()
    {
        if (submitted > completed) WaitTimeline(submitted, ulong.MaxValue);
    }

    /// <summary>Whether frames wait for the screen. Takes at the next frame.</summary>
    public void SetVSync(bool enabled) => vsync = enabled;

    /* Fences, which are values of the timeline */

    /// <summary>Drops a fence behind everything submitted so far, see <see cref="WaitFence"/>.</summary>
    public nint CreateFence() => (nint)NextSubmission;

    /// <summary>Waits until the GPU has got past a fence, or a time (in nanoseconds) has gone by. True if it got past it.</summary>
    public bool WaitFence(nint fence, ulong timeoutNanoseconds)
    {
        ulong value = (ulong)fence;
        if (value <= completed) return true;

        // Behind something that hasn't even gone to the GPU yet, so it goes now or the wait would be for nothing
        if (value > submitted) Flush();

        WaitTimeline(value, timeoutNanoseconds);
        return value <= completed;
    }

    public void DeleteFence(nint fence)
    { }

    /// <summary>Has something freed once the GPU is done with the frame being recorded.</summary>
    internal void Retire(Action free) => Frame.Retired.Add(free);

    public void Dispose()
    {
        if (current != this) return;

        Log.Info("[Graphics] Shutting the device down.");
        Context.WaitIdle();

        // Everything that is still out there goes now, GPU and all
        foreach (var frame in frames) frame.Dispose();

        foreach (var sampler in samplersById.Values) Vk.DestroySampler(Device, sampler, null);
        samplersById.Clear();
        samplersBySettings.Clear();

        if (timestamps.Handle != 0) Vk.DestroyQueryPool(Device, timestamps, null);
        Vk.DestroySemaphore(Device, timeline, null);

        Pipelines.Dispose();
        Layouts.Dispose();
        Bindless.Dispose();
        swapchain.Dispose();
        Memory.Dispose();
        Context.Dispose();

        current = null;
    }
}
