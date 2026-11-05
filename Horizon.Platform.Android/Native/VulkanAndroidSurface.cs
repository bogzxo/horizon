using System.Runtime.InteropServices;

namespace Horizon.Platform.Android.Native;

/// <summary>
/// vkCreateAndroidSurfaceKHR called by hand through the instance's function pointer, so nothing here names a type of
/// the graphics code. Layouts and numbers are vulkan_android.h's and vulkan_core.h's, VK_KHR_android_surface is
/// extension 9 so its create info is 1000008000. The function takes the window with a reference of its own, which
/// vkDestroySurfaceKHR gives back (docs.vulkan.org, vkCreateAndroidSurfaceKHR).
/// </summary>
internal static unsafe class VulkanAndroidSurface
{
    public const string SURFACE_EXTENSION = "VK_KHR_surface";
    public const string ANDROID_SURFACE_EXTENSION = "VK_KHR_android_surface";
    public const string CREATE_FUNCTION = "vkCreateAndroidSurfaceKHR";

    private const int STRUCTURE_TYPE_ANDROID_SURFACE_CREATE_INFO = 1000008000;

    public const int SUCCESS = 0;

    // VkAndroidSurfaceCreateInfoKHR. On a 64 bit ABI that's sType at 0, pNext at 8, flags at 16 and window at 24,
    // 32 bytes, and sequential layout gives the same padding the C compiler does on 32 bit ARM as well
    [StructLayout(LayoutKind.Sequential)]
    private struct CreateInfo
    {
        public int Type;
        public nint Next;
        public uint Flags;
        public nint Window;
    }

    /// <summary>Makes a surface for a native window, giving back VkResult and the handle.</summary>
    public static int Create(nint function, nint instance, nint window, out ulong surface)
    {
        var info = new CreateInfo { Type = STRUCTURE_TYPE_ANDROID_SURFACE_CREATE_INFO, Next = 0, Flags = 0, Window = window };
        ulong made = 0;

        // VkResult (VkInstance, const VkAndroidSurfaceCreateInfoKHR*, const VkAllocationCallbacks*, VkSurfaceKHR*),
        // and VKAPI_CALL on Android arm64 and x86_64 is the platform's plain C convention
        var create = (delegate* unmanaged<nint, CreateInfo*, nint, ulong*, int>)function;
        int result = create(instance, &info, 0, &made);
        surface = made;
        return result;
    }

    /// <summary>The name of a VkResult the function can give back, for an error that says something.</summary>
    public static string Describe(int result) => result switch
    {
        -1 => "VK_ERROR_OUT_OF_HOST_MEMORY",
        -2 => "VK_ERROR_OUT_OF_DEVICE_MEMORY",
        -3 => "VK_ERROR_INITIALIZATION_FAILED",
        -7 => "VK_ERROR_EXTENSION_NOT_PRESENT",
        -13 => "VK_ERROR_UNKNOWN",
        -1000000001 => "VK_ERROR_NATIVE_WINDOW_IN_USE_KHR",
        -1000011001 => "VK_ERROR_VALIDATION_FAILED_EXT",
        _ => "VkResult " + result
    };
}
