namespace Horizon.Platform;

/// <summary>
/// Something the platform can show pictures in, a desktop window or an Android SurfaceView. The engine asks it what
/// the Vulkan instance needs, has it make a Vulkan surface for its native window, and does everything else itself.
/// No Vulkan types in here, handles go over as plain numbers, so a host can be written without the graphics code.
/// <para>
/// Who owns what. The host owns the native window, and keeps it alive from <see cref="CreateVulkanSurface"/> until
/// the engine says it has let go (GraphicsDevice.DetachSurface returns, or the device is disposed). The engine owns
/// the Vulkan surface that comes back, and destroys it with vkDestroySurfaceKHR before it lets go. A host whose
/// native window goes away (Android's surfaceDestroyed) has the engine detach first and only then lets the window go.
/// </para>
/// </summary>
public interface IPlatformSurface
{
    /// <summary>The instance extensions a surface for this window needs, VK_KHR_surface and the one for the platform.</summary>
    IReadOnlyList<string> VulkanInstanceExtensions { get; }

    /// <summary>Whether there is a native window to make a surface for right now.</summary>
    bool IsAvailable { get; }

    /// <summary>How big the window is in pixels right now, nothing by nothing while it is minimised or gone.</summary>
    SurfaceSize PixelSize { get; }

    /// <summary>How the pictures are to be turned for the screen, see <see cref="SurfaceTransformPolicy"/>.</summary>
    SurfaceTransformPolicy TransformPolicy => SurfaceTransformPolicy.Current;

    /// <summary>
    /// Makes a VkSurfaceKHR for the native window in an instance and gives back its handle. The engine owns it from
    /// then on. The second argument finds an instance function by name (vkGetInstanceProcAddr), for a host that has to
    /// call its platform's vkCreate*SurfaceKHR itself. Called on the thread that draws.
    /// </summary>
    ulong CreateVulkanSurface(nint instance, Func<string, nint> instanceFunction);
}

/// <summary>
/// What a platform knows about its windows before there is one, so the instance and the device can be made while the
/// system is still making the window. Only desktop Windows has one now, see WindowManager.Device.
/// </summary>
public interface IPlatformSurfaceAhead
{
    /// <summary>The instance extensions any window of this platform will want.</summary>
    IReadOnlyList<string> VulkanInstanceExtensions { get; }

    /// <summary>
    /// Whether a queue family of a physical device can show pictures in a window of this platform, asked without a
    /// surface. The window's own surface is asked again once it is there.
    /// </summary>
    bool PresentsWithoutSurface(nint instance, nint physicalDevice, uint queueFamily, Func<string, nint> instanceFunction);
}

/// <summary>A size in pixels, of a surface.</summary>
public readonly record struct SurfaceSize(uint Width, uint Height)
{
    /// <summary>Whether there's nothing to draw into, a minimised window says nothing by nothing.</summary>
    public bool IsEmpty => Width == 0 || Height == 0;
}

/// <summary>How the swapchain's pictures are turned for the screen they're shown on.</summary>
public enum SurfaceTransformPolicy
{
    /// <summary>The way the surface says it is right now, which is the desktop's way and the way it always was.</summary>
    Current,

    /// <summary>Not turned at all, the compositor turns them. What a phone gets first, its picture comes out sideways otherwise.</summary>
    Identity
}
