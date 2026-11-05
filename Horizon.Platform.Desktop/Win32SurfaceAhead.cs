namespace Horizon.Platform.Desktop;

/// <summary>
/// What any window on Windows wants of Vulkan, known before the window exists, so the device can be made while the
/// system makes the window (WindowManager.Device). The instance needs the same two extensions for every window, and
/// whether a queue can show pictures is asked of the card with vkGetPhysicalDeviceWin32PresentationSupportKHR, no
/// surface needed. The host hands it to the engine, see DesktopHost.SurfaceAhead.
/// </summary>
internal sealed unsafe class Win32SurfaceAhead : IPlatformSurfaceAhead
{
    // Set this in the environment to off and the device is made once the window is there, the way it is everywhere
    // else, for a driver that turns out not to like being got going early
    internal const string DEVICE_AHEAD_VARIABLE = "HORIZON_DEVICE_AHEAD";

    private const string PRESENTATION_SUPPORT = "vkGetPhysicalDeviceWin32PresentationSupportKHR";

    private static readonly string[] EXTENSIONS = ["VK_KHR_surface", "VK_KHR_win32_surface"];

    private Win32SurfaceAhead() { }

    /// <summary>
    /// The one for this machine, null where there's none. Anywhere but Windows, under SDL (whose windows go their own
    /// way), and with HORIZON_DEVICE_AHEAD=off.
    /// </summary>
    public static Win32SurfaceAhead? ForThisMachine(bool sdl)
    {
        if (!OperatingSystem.IsWindows() || sdl)
            return null;
        if (string.Equals(Environment.GetEnvironmentVariable(DEVICE_AHEAD_VARIABLE), "off", StringComparison.OrdinalIgnoreCase))
            return null;
        return new Win32SurfaceAhead();
    }

    public IReadOnlyList<string> VulkanInstanceExtensions => EXTENSIONS;

    public bool PresentsWithoutSurface(nint instance, nint physicalDevice, uint queueFamily, Func<string, nint> instanceFunction)
    {
        nint function = instanceFunction(PRESENTATION_SUPPORT);
        if (function == 0)
            return false;

        // VkBool32 vkGetPhysicalDeviceWin32PresentationSupportKHR(VkPhysicalDevice, uint32_t)
        var supports = (delegate* unmanaged<nint, uint, uint>)function;
        return supports(physicalDevice, queueFamily) != 0;
    }
}
