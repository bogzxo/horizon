using Horizon.Logging;

using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Horizon.Graphics.Vulkan;

/// <summary>
/// The pictures the window shows, and which of them is being drawn. Made anew whenever the window changes size or
/// the vsync setting does (the old one is handed over so the driver can keep what it can).
/// </summary>
internal sealed unsafe class Swapchain : IDisposable
{
    private readonly VulkanContext context;

    public SwapchainKHR Handle { get; private set; }
    public Image[] Images { get; private set; } = [];
    public ImageView[] Views { get; private set; } = [];
    public ImageLayout[] Layouts { get; private set; } = [];
    public Format Format { get; private set; }
    public Extent2D Extent { get; private set; }
    public bool VSync { get; private set; }

    /// <summary>
    /// Whether the swapchain is the size it was asked to be whatever the surface is, which is the case on a Mac, where
    /// MoltenVK stretches it over the layer. It calls a swapchain like that suboptimal on every frame, and one that is
    /// made anew for being suboptimal would be made anew every frame, so there suboptimal is no news. The window
    /// says when its size changes and the swapchain is made anew for that, see GraphicsDevice.BeginFrame.
    /// </summary>
    private static bool SizedByHand => OperatingSystem.IsMacOS();

    /// <summary>The picture that was acquired for this frame, -1 while there isn't one.</summary>
    public int Current { get; private set; } = -1;

    public uint Width => Extent.Width;
    public uint Height => Extent.Height;

    public Swapchain(VulkanContext context, uint width, uint height, bool vsync)
    {
        this.context = context;
        Create(width, height, vsync, default);
    }

    private void Create(uint width, uint height, bool vsync, SwapchainKHR old)
    {
        var vk = context.Vk;
        var surface = context.KhrSurface;

        surface.GetPhysicalDeviceSurfaceCapabilities(context.PhysicalDevice, context.Surface, out SurfaceCapabilitiesKHR capabilities);

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
        // The size of the surface where the surface has one, which is the only size most drivers take. MoltenVK takes
        // any and stretches it over the layer, which is how a fullscreen window on a Retina screen is drawn at the
        // size the game was set to rather than at every pixel the screen has, see WindowManager.RenderSize
        Extent2D extent = capabilities.CurrentExtent.Width != uint.MaxValue && !OperatingSystem.IsMacOS()
            ? capabilities.CurrentExtent
            : new Extent2D(
                Math.Clamp(width, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
                Math.Clamp(height, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height));

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
            PreTransform = capabilities.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = mode,
            Clipped = true,
            OldSwapchain = old
        };

        VulkanContext.Check(context.KhrSwapchain.CreateSwapchain(context.Device, in createInfo, null, out SwapchainKHR handle), "making the swapchain");
        Handle = handle;
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

        Current = -1;
        Log.Info($"[Vulkan] Swapchain of {count} pictures at {extent.Width} by {extent.Height}, {mode}, {Format}.");
    }

    /// <summary>Makes the swapchain anew for a size or a vsync setting. The device has to be idle.</summary>
    public void Recreate(uint width, uint height, bool vsync)
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

        Create(width, height, vsync, old);
        if (old.Handle != 0) context.KhrSwapchain.DestroySwapchain(context.Device, old, null);
    }

    /// <summary>Takes the next picture to draw into. False (and nothing taken) if the swapchain is out of date.</summary>
    public bool Acquire(Semaphore signal, out bool suboptimal)
    {
        uint index = 0;
        Result result = context.KhrSwapchain.AcquireNextImage(context.Device, Handle, ulong.MaxValue, signal, default, &index);
        suboptimal = result == Result.SuboptimalKhr && !SizedByHand;

        if (result is Result.Success or Result.SuboptimalKhr)
        {
            Current = (int)index;
            return true;
        }

        if (result == Result.ErrorOutOfDateKhr)
        {
            Current = -1;
            return false;
        }

        VulkanContext.Check(result, "acquiring a swapchain image");
        return false;
    }

    /// <summary>Shows the picture that was drawn. False if the swapchain is out of date and has to be made anew.</summary>
    public bool Present(Semaphore wait)
    {
        if (Current < 0) return true;

        uint index = (uint)Current;
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
        if (result == Result.ErrorOutOfDateKhr || (result == Result.SuboptimalKhr && !SizedByHand)) return false;
        if (result != Result.SuboptimalKhr) VulkanContext.Check(result, "presenting");
        return true;
    }

    private void DestroyViews()
    {
        foreach (var view in Views) context.Vk.DestroyImageView(context.Device, view, null);
        Views = [];
    }

    public void Dispose()
    {
        DestroyViews();
        if (Handle.Handle != 0) context.KhrSwapchain.DestroySwapchain(context.Device, Handle, null);
        Handle = default;
    }
}
