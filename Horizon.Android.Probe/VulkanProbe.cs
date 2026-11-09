using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Vulkan;
using Result = Silk.NET.Vulkan.Result;

namespace Horizon.Android.Probe;

internal static class VulkanProbe
{
    public static unsafe string Run()
    {
        using var vk = Vk.GetApi();
        var application = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            ApiVersion = Vk.Version10
        };
        var createInfo = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &application
        };
        Check(vk.CreateInstance(in createInfo, null, out var instance), "CreateInstance");
        try
        {
            uint count = 0;
            Check(vk.EnumeratePhysicalDevices(instance, ref count, null), "Count physical devices");
            if (count == 0) throw new InvalidOperationException("Vulkan reports no physical devices.");
            var devices = new PhysicalDevice[count];
            fixed (PhysicalDevice* pointer = devices)
                Check(vk.EnumeratePhysicalDevices(instance, ref count, pointer), "Enumerate physical devices");

            var report = new StringBuilder("PROBE PASSED\nSilk Vk.GetApi loaded Vulkan and created an instance.\n");
            for (int index = 0; index < count; index++)
            {
                vk.GetPhysicalDeviceProperties(devices[index], out var properties);
                string? name = Marshal.PtrToStringUTF8((nint)properties.DeviceName);
                uint version = properties.ApiVersion;
                report.AppendLine($"{name}, Vulkan {version >> 22}.{(version >> 12) & 1023}.{version & 4095}");
            }
            report.AppendLine("This proves loader startup only. Horizon device features and presentation are not tested.");
            return report.ToString();
        }
        finally
        {
            vk.DestroyInstance(instance, null);
        }
    }

    private static void Check(Result result, string operation)
    {
        if (result != Result.Success)
            throw new InvalidOperationException($"{operation} failed with {result}.");
    }
}
