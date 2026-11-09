using Horizon.Logging;
using Horizon.Platform;

using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Horizon.Graphics.Vulkan;

/// <summary>What the swapchain said when a picture was taken off it or shown, sorted into what the device does about it.</summary>
internal enum SwapchainStatus
{
    /// <summary>All good.</summary>
    Ready,

    /// <summary>A picture was had (or shown), but the swapchain no longer fits the surface and wants making anew.</summary>
    Suboptimal,

    /// <summary>No picture, the swapchain doesn't fit the surface any more and has to be made anew first.</summary>
    OutOfDate,

    /// <summary>No picture, the surface itself is gone and has to be made again (VK_ERROR_SURFACE_LOST_KHR).</summary>
    SurfaceLost,

    /// <summary>No picture this time, nothing wrong (a timeout, or not ready), try again next frame.</summary>
    NotReady,

    /// <summary>The surface is nothing by nothing (a minimised window), so there is no swapchain to have.</summary>
    Empty
}

/// <summary>
/// The pictures the window shows, and which of them is being drawn. Made anew whenever the window changes size or
/// the vsync setting does (the old one is handed over so the driver can keep what it can), and let go of altogether
/// when the surface goes (<see cref="Release"/>), keeping its format so the pipelines made for it still fit.
/// </summary>
internal sealed unsafe class Swapchain : IDisposable
{
    private readonly VulkanContext context;

    public SwapchainKHR Handle { get; private set; }
    public Image[] Images { get; private set; } = [];
    public ImageView[] Views { get; private set; } = [];
    public ImageLayout[] Layouts { get; private set; } = [];
    public Format Format { get; private set; } = Format.B8G8R8A8Unorm;
    public Extent2D Extent { get; private set; }
    public bool VSync { get; private set; }

    /// <summary>Whether there is a swapchain right now, which there isn't without a surface or while it's nothing by nothing.</summary>
    public bool IsLive => Handle.Handle != 0;

    /// <summary>How many swapchains have been made, for the tests and the log.</summary>
    public int Generation { get; private set; }

    /// <summary>
    /// Whether the swapchain is the size it was asked to be whatever the surface is, which is the case on a Mac, where
    /// MoltenVK stretches it over the layer. It calls a swapchain like that suboptimal on every frame, and one that is
    /// made anew for being suboptimal would be made anew every frame, so there suboptimal is no news. The window
    /// says when its size changes and the swapchain is made anew for that, see GraphicsDevice.BeginFrame.
    /// </summary>
    private static bool SizedByHand => OperatingSystem.IsMacOS();

    /// <summary>The picture that was acquired for this frame, -1 while there isn't one.</summary>
    public int Current { get; private set; } = -1;

    // One render finished semaphore a picture, which the frame's last submit signals and the present of that picture
    // waits on. They can't be the frame's own, the present engine holds a semaphore until the picture comes back off
    // the swapchain, and that can be after the frame comes round again (the Vulkan guide's swapchain semaphore reuse).
    // A picture is only acquired again once its present is done with it, so the one of the acquired picture is free.
    // They're kept when the swapchain goes or is made anew, which is only ever done with the device idle, and grow
    // when a swapchain has more pictures than any before it
    private Semaphore[] presentSemaphores = [];

    /// <summary>The semaphore the picture acquired this frame is shown behind, see <see cref="presentSemaphores"/>.</summary>
    public Semaphore RenderFinished => Current >= 0 ? presentSemaphores[Current] : default;

    public uint Width => Extent.Width;
    public uint Height => Extent.Height;

    /// <summary>A swapchain for the context's surface, which is made by <see cref="Make"/>.</summary>
    public Swapchain(VulkanContext context)
    {
        this.context = context;
    }

    /// <summary>
    /// Sorts what Vulkan said about a picture into what's to be done. Suboptimal is no news where the swapchain is
    /// sized by hand, see <see cref="SizedByHand"/>. Anything that isn't about the swapchain or the surface is null,
    /// and whoever asked throws with it.
    /// </summary>
    public static SwapchainStatus? Classify(Result result, bool sizedByHand) => result switch
    {
        Result.Success => SwapchainStatus.Ready,
        Result.SuboptimalKhr => sizedByHand ? SwapchainStatus.Ready : SwapchainStatus.Suboptimal,
        Result.ErrorOutOfDateKhr => SwapchainStatus.OutOfDate,
        Result.ErrorSurfaceLostKhr => SwapchainStatus.SurfaceLost,
        Result.NotReady or Result.Timeout => SwapchainStatus.NotReady,
        _ => null
    };

    /// <summary>
    /// The way the pictures are turned. The surface's own transform unless the platform asked for none and the surface
    /// can do without, see <see cref="SurfaceTransformPolicy"/>.
    /// </summary>
    public static SurfaceTransformFlagsKHR PickTransform(SurfaceTransformPolicy policy, SurfaceTransformFlagsKHR supported, SurfaceTransformFlagsKHR current) =>
        policy == SurfaceTransformPolicy.Identity && (supported & SurfaceTransformFlagsKHR.IdentityBitKhr) != 0
            ? SurfaceTransformFlagsKHR.IdentityBitKhr
            : current;

    /// <summary>Prefers opaque composition, then lets the window system decide where opaque isn't offered.</summary>
    public static CompositeAlphaFlagsKHR PickCompositeAlpha(CompositeAlphaFlagsKHR supported)
    {
        ReadOnlySpan<CompositeAlphaFlagsKHR> preference = [CompositeAlphaFlagsKHR.OpaqueBitKhr, CompositeAlphaFlagsKHR.InheritBitKhr,
            CompositeAlphaFlagsKHR.PreMultipliedBitKhr, CompositeAlphaFlagsKHR.PostMultipliedBitKhr];
        foreach (var mode in preference)
            if ((supported & mode) != 0) return mode;
        throw new InvalidOperationException("The surface offers no supported alpha composition mode.");
    }

    /// <summary>
    /// Makes the swapchain, anew if there is one for a size or a vsync setting. The device has to be idle. Empty if the
    /// surface is nothing by nothing, which leaves no swapchain at all, SurfaceLost if the surface is gone.
    /// </summary>
    public SwapchainStatus Make(uint width, uint height, bool vsync)
    {
        SwapchainKHR old = Handle;
        DestroyViews();

        // MoltenVK calls a swapchain that was handed the one before it suboptimal for as long as it lives, whatever
        // size it is, and a suboptimal one is made anew, so resizing the window once had it remade every frame from
        // then on. On a Mac the old one goes first and the new one starts from nothing
        if (OperatingSystem.IsMacOS() && old.Handle != 0)
        {
            context.KhrSwapchain.DestroySwapchain(context.Device, old, null);
            old = default;
        }

        Handle = default;
        SwapchainStatus made = Create(width, height, vsync, old);
        if (old.Handle != 0) context.KhrSwapchain.DestroySwapchain(context.Device, old, null);
        return made;
    }

    private SwapchainStatus Create(uint width, uint height, bool vsync, SwapchainKHR old)
    {
        var vk = context.Vk;
        var surface = context.KhrSurface;
        Current = -1;
        Images = [];
        Layouts = [];

        Result asked = surface.GetPhysicalDeviceSurfaceCapabilities(context.PhysicalDevice, context.Surface, out SurfaceCapabilitiesKHR capabilities);
        if (asked == Result.ErrorSurfaceLostKhr)
            return SwapchainStatus.SurfaceLost;
        VulkanContext.Check(asked, "asking the surface what it can do");

        uint formatCount = 0;
        surface.GetPhysicalDeviceSurfaceFormats(context.PhysicalDevice, context.Surface, &formatCount, null);
        var formats = new SurfaceFormatKHR[formatCount];
        fixed (SurfaceFormatKHR* pointer = formats)
            surface.GetPhysicalDeviceSurfaceFormats(context.PhysicalDevice, context.Surface, &formatCount, pointer);

        uint modeCount = 0;
        surface.GetPhysicalDeviceSurfacePresentModes(context.PhysicalDevice, context.Surface, &modeCount, null);
        var modes = new PresentModeKHR[modeCount];
        fixed (PresentModeKHR* pointer = modes)
            surface.GetPhysicalDeviceSurfacePresentModes(context.PhysicalDevice, context.Surface, &modeCount, pointer);

        if (formatCount == 0)
            return SwapchainStatus.SurfaceLost;

        // Colours as they are, no gamma. The engine does its own sums in whatever space it likes
        SurfaceFormatKHR chosen = formats[0];
        foreach (var format in formats)
        {
            if (format.Format is Format.B8G8R8A8Unorm or Format.R8G8B8A8Unorm && format.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
            {
                chosen = format;
                break;
            }
        }

        // Vsync off means frames go out the moment they are done, which is immediate mode and nothing else. Mailbox
        // still waits for the blank on most drivers (it only swaps the newest frame in at the blank), so on a Windows
        // box it looked for all the world like the vsync switch did nothing. Mailbox is the fallback for a driver
        // without immediate, and then fifo, which every driver has
        PresentModeKHR mode = PresentModeKHR.MailboxKhr;
        if (!vsync)
        {
            if (modes.Contains(PresentModeKHR.ImmediateKhr)) mode = PresentModeKHR.ImmediateKhr;
            else if (modes.Contains(PresentModeKHR.MailboxKhr)) mode = PresentModeKHR.MailboxKhr;
            else if (modes.Contains(PresentModeKHR.FifoRelaxedKhr)) mode = PresentModeKHR.FifoRelaxedKhr;
        }

        // Mailbox where it's there, and fifo where it isn't, which every driver has (a phone mostly has nothing else)
        if (!modes.Contains(mode)) mode = PresentModeKHR.FifoKhr;

        // The size of the surface where the surface has one, which is the only size most drivers take. MoltenVK takes
        // any and stretches it over the layer, which is how a fullscreen window on a Retina screen is drawn at the
        // size the game was set to rather than at every pixel the screen has, see WindowManager.RenderSize
        Extent2D extent = capabilities.CurrentExtent.Width != uint.MaxValue && !OperatingSystem.IsMacOS()
            ? capabilities.CurrentExtent
            : new Extent2D(
                Math.Clamp(width, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
                Math.Clamp(height, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height));

        // A minimised window on Windows says its surface is nothing by nothing, and a swapchain can't be made at that
        if (extent.Width == 0 || extent.Height == 0)
            return SwapchainStatus.Empty;

        // One more than the least, so there is always one to draw into while another is on its way to the screen
        uint imageCount = capabilities.MinImageCount + 1;
        if (capabilities.MaxImageCount > 0 && imageCount > capabilities.MaxImageCount) imageCount = capabilities.MaxImageCount;

        var createInfo = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = context.Surface,
            MinImageCount = imageCount,
            ImageFormat = chosen.Format,
            ImageColorSpace = chosen.ColorSpace,
            ImageExtent = extent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit,
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = PickTransform(context.TransformPolicy, capabilities.SupportedTransforms, capabilities.CurrentTransform),
            CompositeAlpha = PickCompositeAlpha(capabilities.SupportedCompositeAlpha),
            PresentMode = mode,
            Clipped = true,
            OldSwapchain = old
        };

        Result result = context.KhrSwapchain.CreateSwapchain(context.Device, in createInfo, null, out SwapchainKHR handle);
        if (result == Result.ErrorSurfaceLostKhr)
            return SwapchainStatus.SurfaceLost;
        VulkanContext.Check(result, "making the swapchain");

        Handle = handle;
        Generation++;
        context.Name(ObjectType.SwapchainKhr, handle.Handle, $"swapchain {extent.Width} by {extent.Height}");
        Format = chosen.Format;
        Extent = extent;
        VSync = vsync;

        uint count = 0;
        context.KhrSwapchain.GetSwapchainImages(context.Device, Handle, &count, null);
        Images = new Image[count];
        fixed (Image* pointer = Images)
            context.KhrSwapchain.GetSwapchainImages(context.Device, Handle, &count, pointer);

        Views = new ImageView[count];
        Layouts = new ImageLayout[count];
        for (int i = 0; i < count; i++)
        {
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = Images[i],
                ViewType = ImageViewType.Type2D,
                Format = Format,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1)
            };

            VulkanContext.Check(vk.CreateImageView(context.Device, in viewInfo, null, out Views[i]), "making a swapchain image view");
            context.Name(ObjectType.Image, Images[i].Handle, $"swapchain picture {i}");
            context.Name(ObjectType.ImageView, Views[i].Handle, $"swapchain picture {i} view");
            Layouts[i] = ImageLayout.Undefined;
        }

        GrowPresentSemaphores((int)count);

        Log.Info($"[Vulkan] Swapchain of {count} pictures at {extent.Width} by {extent.Height}, {mode}, {Format}.");
        return SwapchainStatus.Ready;
    }

    /// <summary>
    /// Takes the next picture to draw into. Only Ready and Suboptimal have one, and only then is the semaphore going to
    /// be signalled, which is what keeps it free for the next try whatever else came back.
    /// </summary>
    public SwapchainStatus Acquire(Semaphore signal)
    {
        Current = -1;
        if (!IsLive)
            return SwapchainStatus.Empty;

        uint index = 0;
        Result result = context.KhrSwapchain.AcquireNextImage(context.Device, Handle, ulong.MaxValue, signal, default, &index);
        SwapchainStatus status = Classify(result, SizedByHand) ?? Fail(result, "acquiring a swapchain image");

        if (status is SwapchainStatus.Ready or SwapchainStatus.Suboptimal)
            Current = (int)index;
        return status;
    }

    /// <summary>
    /// Shows the picture that was drawn, behind its <see cref="RenderFinished"/>. Whatever comes back the picture is
    /// gone and the semaphore is waited on, the spec counts a present the surface turned down as queued all the same.
    /// </summary>
    public SwapchainStatus Present()
    {
        if (Current < 0) return SwapchainStatus.Ready;

        uint index = (uint)Current;
        Semaphore wait = presentSemaphores[index];
        SwapchainKHR handle = Handle;
        var presentInfo = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &wait,
            SwapchainCount = 1,
            PSwapchains = &handle,
            PImageIndices = &index
        };

        Result result;
        lock (context.QueueLock)
            result = context.KhrSwapchain.QueuePresent(context.GraphicsQueue, in presentInfo);

        Current = -1;
        return Classify(result, SizedByHand) ?? Fail(result, "presenting");
    }

    // Throws with what came back, for anything that isn't about the swapchain or the surface (a lost device)
    private static SwapchainStatus Fail(Result result, string doing)
    {
        VulkanContext.Check(result, doing);
        return SwapchainStatus.NotReady;
    }

    /// <summary>
    /// Lets go of the swapchain and its pictures, for a surface that is going or has gone. The format and the size stay
    /// what they were, so whatever was made for them carries on fitting. The device has to be idle.
    /// </summary>
    public void Release()
    {
        DestroyViews();
        if (Handle.Handle != 0) context.KhrSwapchain.DestroySwapchain(context.Device, Handle, null);
        Handle = default;
        Images = [];
        Layouts = [];
        Current = -1;
    }

    private void DestroyViews()
    {
        foreach (var view in Views) context.Vk.DestroyImageView(context.Device, view, null);
        Views = [];
    }

    // Helper method to have a present semaphore for every picture there is, the ones there are already kept
    private void GrowPresentSemaphores(int count)
    {
        if (presentSemaphores.Length >= count) return;

        int had = presentSemaphores.Length;
        var grown = new Semaphore[count];
        Array.Copy(presentSemaphores, grown, had);

        var info = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        for (int i = had; i < count; i++)
        {
            VulkanContext.Check(context.Vk.CreateSemaphore(context.Device, in info, null, out grown[i]), "making a present semaphore");
            context.Name(ObjectType.Semaphore, grown[i].Handle, $"swapchain picture {i} render finished");
        }

        presentSemaphores = grown;
    }

    /// <summary>Lets go of the swapchain and its present semaphores, once the device is idle.</summary>
    public void Dispose()
    {
        Release();
        foreach (var semaphore in presentSemaphores) context.Vk.DestroySemaphore(context.Device, semaphore, null);
        presentSemaphores = [];
    }
}
