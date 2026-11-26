using Horizon.Content;
using Horizon.Logging;
using Horizon.Platform;

namespace Horizon.Graphics.Vulkan;

/// <summary>
/// A Vulkan context and its device-independent subsystems (memory allocator, bindless table, descriptor layouts,
/// pipeline cache) constructed ahead of window creation, alongside warming the shader packs from content.
/// Lives inside <c>Horizon.Graphics.Vulkan</c> so <c>Horizon.Core</c> never references <c>Silk.NET.Vulkan</c>.
/// </summary>
internal sealed class PreparedDevice : IDisposable
{
    public VulkanContext Context { get; }
    public VulkanMemory Memory { get; }
    public BindlessTextures Bindless { get; }
    public DescriptorLayouts Layouts { get; }
    public PipelineCache Pipelines { get; }

    private PreparedDevice(VulkanContext context, VulkanMemory memory, BindlessTextures bindless, DescriptorLayouts layouts, PipelineCache pipelines)
    {
        Context = context;
        Memory = memory;
        Bindless = bindless;
        Layouts = layouts;
        Pipelines = pipelines;
    }

    /// <summary>
    /// Prepares the Vulkan context and its core subsystems ahead of the window, while warming shader packs in parallel.
    /// Returns null if the platform cannot prepare ahead or preparation fails.
    /// </summary>
    public static PreparedDevice? Prepare(IPlatformSurfaceAhead? ahead)
    {
        if (ahead is null) return null;

        // Warm the shader pack set concurrently with Vulkan instance/device creation
        IContentFiles content = ContentFiles.Source;
        Task.Run(() =>
        {
            try
            {
                _ = ShaderPackSet.For(content);
            }
            catch (Exception)
            {
                // Any pack problem is logged by ShaderPackSet.Load itself
            }
        });

        VulkanContext? context = VulkanContext.Prepare(ahead);
        if (context is null) return null;

        VulkanMemory? memory = null;
        BindlessTextures? bindless = null;
        DescriptorLayouts? layouts = null;
        PipelineCache? pipelines = null;
        try
        {
            memory = new VulkanMemory(context);
            bindless = new BindlessTextures(context);
            layouts = new DescriptorLayouts(context, bindless);
            pipelines = new PipelineCache(context, layouts);
            return new PreparedDevice(context, memory, bindless, layouts, pipelines);
        }
        catch (Exception e)
        {
            Log.Warning($"[Vulkan] Couldn't finish preparing device subsystems ahead of the window: {e.Message}");
            pipelines?.Dispose();
            layouts?.Dispose();
            bindless?.Dispose();
            memory?.Dispose();
            context.Dispose();
            return null;
        }
    }

    public SurfaceAttachResult AttachSurface(IPlatformSurface surface) => Context.AttachSurface(surface);

    public void Dispose()
    {
        Pipelines.Dispose();
        Layouts.Dispose();
        Bindless.Dispose();
        Memory.Dispose();
        Context.Dispose();
    }
}
