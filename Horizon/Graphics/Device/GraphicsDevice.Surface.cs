using Horizon.Graphics.Vulkan;
using Horizon.Logging;
using Horizon.Platform;

namespace Horizon.Graphics;

// The surface the frames are shown in, which can go away and come back while the device and everything made on it
// stays. A phone takes it away whenever the game goes to the background, and gives a new one when it's back. Taking
// it away waits for every frame in flight, lets go of the swapchain and the surface and keeps the instance, the card,
// the device, the memory and every texture, pipeline and descriptor. Frames go on without it, drawing into render
// targets and showing nothing, until a surface is given again. The driver losing the surface by itself
// (VK_ERROR_SURFACE_LOST_KHR) is the same thing done for it, with the same platform surface made again after.
public sealed partial class GraphicsDevice
{
    private readonly SurfaceLifecycle surfaceLife = new();

    // What the surface is made from, null while there's none (taken away by the host)
    private IPlatformSurface? surfaceSource;

    /// <summary>Where the device stands with its surface, see <see cref="Horizon.Graphics.SurfaceState"/>.</summary>
    public SurfaceState SurfaceState => surfaceLife.State;

    /// <summary>Whether there is a surface with a swapchain to show frames in right now.</summary>
    public bool HasSurface => surfaceLife.State == SurfaceState.Attached && swapchain.IsLive;

    /// <summary>Whether the frame being recorded got a picture of the swapchain, which is whether drawing into the window goes anywhere.</summary>
    public bool HasWindowImage => Frame.HasImage;

    /// <summary>How big the swapchain's pictures are, what they were last while there's no swapchain.</summary>
    public uint SwapchainWidth => swapchain.Width;

    /// <summary>How big the swapchain's pictures are, what they were last while there's no swapchain.</summary>
    public uint SwapchainHeight => swapchain.Height;

    /// <summary>How many swapchains have been made, which goes up with every resize and every new surface.</summary>
    public int SwapchainGeneration => swapchain.Generation;

    /// <summary>How many times the driver said the surface was gone.</summary>
    public int SurfaceLosses => surfaceLife.Losses;

    /// <summary>
    /// Takes the surface away, for a host whose native window is going (a phone's app going to the background).
    /// Waits for the GPU to be done with every frame in flight and every present, then lets go of the swapchain and
    /// destroys the Vulkan surface. Once this returns the host may let its native window go. The device and everything
    /// made on it stays. False if there was no surface to take. The thread that draws, between EndFrame and BeginFrame.
    /// </summary>
    public bool DetachSurface()
    {
        surfaceLife.RequireBetweenFrames("taken away");
        if (surfaceLife.State == SurfaceState.Detached)
            return false;

        ReleaseSurface();
        surfaceSource = null;
        surfaceLife.Detached();
        Log.Info("[Graphics] The surface was taken away, the device carries on without it.");
        return true;
    }

    /// <summary>
    /// Gives the device a surface again after <see cref="DetachSurface"/> (or one the driver lost), and makes its
    /// swapchain if it has a size. The instance has to have the extensions the surface wants and the queue the device
    /// draws with has to be able to show pictures in it, nothing is changed if not. The thread that draws, between
    /// EndFrame and BeginFrame.
    /// </summary>
    public SurfaceAttachResult AttachSurface(IPlatformSurface surface)
    {
        surfaceLife.RequireBetweenFrames("given");
        if (surfaceLife.State == SurfaceState.Attached)
            return SurfaceAttachResult.AlreadyAttached;

        SurfaceAttachResult result = Context.AttachSurface(surface);
        if (result != SurfaceAttachResult.Attached)
        {
            Log.Warning($"[Graphics] A surface was given and couldn't be drawn into ({result}), the device carries on without one.");
            return result;
        }

        surfaceSource = surface;
        surfaceLife.Attached();
        Log.Info("[Graphics] A surface was given, frames are shown again.");

        // Made now at the size the platform says, and again at the first frame if the window is another size. A window
        // that changed while there was no surface is news this swapchain already has
        surfaceChanged = false;
        SurfaceSize size = surface.PixelSize;
        if (!size.IsEmpty)
            MakeSwapchain(size.Width, size.Height);
        return SurfaceAttachResult.Attached;
    }

    /// <summary>
    /// Helper method to make the swapchain for a size, waiting for the GPU to be done with the one there is first.
    /// True if there's one to draw into now. A surface that turns out gone is let go of.
    /// </summary>
    private bool MakeSwapchain(uint width, uint height)
    {
        if (swapchain.IsLive)
            Context.WaitIdle();

        SwapchainStatus made = swapchain.Make(width, height, vsync);
        (askedWidth, askedHeight) = (width, height);
        swapchainStale = false;

        if (made == SwapchainStatus.SurfaceLost)
        {
            LoseSurface();
            return false;
        }

        return made == SwapchainStatus.Ready;
    }

    /// <summary>Helper method for the driver saying the surface is gone, which lets go of it so it can be made again.</summary>
    private void LoseSurface()
    {
        if (surfaceLife.State != SurfaceState.Attached)
            return;

        Log.Warning("[Graphics] The driver says the surface is gone, letting go of it to make it again.");
        ReleaseSurface();
        surfaceLife.Lost();
    }

    /// <summary>Helper method to have a go at making a lost surface again, from the platform surface it was made from.</summary>
    private void RecoverSurface()
    {
        if (surfaceSource is null || Context.AttachSurface(surfaceSource) != SurfaceAttachResult.Attached)
        {
            surfaceLife.RecoverFailed();
            return;
        }

        surfaceLife.Attached();
        Log.Info("[Graphics] The lost surface was made again.");
    }

    /// <summary>
    /// Helper method to let go of the swapchain and the surface once the GPU is done with them. Every queue goes idle
    /// first, which takes in the presents still waiting on their semaphores, so nothing waits on a picture that's gone
    /// and the acquire and present semaphores are all unsignalled for whatever swapchain comes next. The present ones
    /// stay with the swapchain object and are destroyed with the device.
    /// </summary>
    private void ReleaseSurface()
    {
        Context.WaitIdle();
        completed = Math.Max(completed, QueryCompleted());

        swapchain.Release();
        Context.DetachSurface();
        swapchainStale = false;
        windowPendingClear = null;
    }
}
