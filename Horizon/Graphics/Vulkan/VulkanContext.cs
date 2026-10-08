using System.Runtime.InteropServices;

using Horizon.Logging;

using Silk.NET.Core;
using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Horizon.Graphics.Vulkan;

/// <summary>
/// The instance, the physical device that was picked, the logical device and its queues. Made once by
/// <see cref="GraphicsDevice"/> and torn down with it. The card has to do Vulkan 1.3 with dynamic rendering,
/// synchronization2 and timeline semaphores, which every driver of the last few years does.
/// </summary>
internal sealed unsafe class VulkanContext : IDisposable
{
    private const string VALIDATION_LAYER = "VK_LAYER_KHRONOS_validation";
    private const string VALIDATION_VARIABLE = "HORIZON_VULKAN_VALIDATION";
    private const string PORTABILITY_ENUMERATION = "VK_KHR_portability_enumeration";
    private const string PORTABILITY_SUBSET = "VK_KHR_portability_subset";
    private const string DYNAMIC_RENDERING = "VK_KHR_dynamic_rendering";
    private const string SYNCHRONIZATION2 = "VK_KHR_synchronization2";
    private const string MAINTENANCE4 = "VK_KHR_maintenance4";

    public Vk Vk { get; }
    public Instance Instance { get; private set; }
    public PhysicalDevice PhysicalDevice { get; private set; }
    public Device Device { get; private set; }
    public SurfaceKHR Surface { get; private set; }

    public KhrSurface KhrSurface { get; private set; } = null!;
    public KhrSwapchain KhrSwapchain { get; private set; } = null!;

    public Queue GraphicsQueue { get; private set; }
    public Queue ComputeQueue { get; private set; }
    public Queue TransferQueue { get; private set; }

    public uint GraphicsFamily { get; private set; }
    public uint ComputeFamily { get; private set; }
    public uint TransferFamily { get; private set; }

    /// <summary>Whether the card has a compute queue that isn't the graphics one, for work that runs alongside the drawing.</summary>
    public bool HasAsyncCompute { get; private set; }

    /// <summary>Whether the card has a transfer queue of its own, for uploads off the render thread.</summary>
    public bool HasTransferQueue { get; private set; }

    public PhysicalDeviceProperties Properties { get; private set; }
    public PhysicalDeviceMemoryProperties MemoryProperties { get; private set; }
    public PhysicalDeviceLimits Limits => Properties.Limits;

    public string DeviceName { get; private set; } = string.Empty;
    public string DriverVersion { get; private set; } = string.Empty;
    public bool ValidationEnabled { get; private set; }
    public bool TimestampsSupported { get; private set; }
    public float TimestampPeriod { get; private set; }

    /// <summary>The depth and stencil format the card has, the 24 plus 8 one when it does, 32 plus 8 otherwise.</summary>
    public Format DepthStencilFormat { get; private set; }

    /// <summary>Whether the bindless table may be written to while a frame that doesn't read the slot is in flight.</summary>
    public bool UpdateUnusedWhilePending { get; private set; }

    /// <summary>How many sampled images and samplers a set written after bind may hold, which sizes the bindless table.</summary>
    public uint MaxUpdateAfterBindSampledImages { get; private set; } = uint.MaxValue;
    public uint MaxUpdateAfterBindSamplers { get; private set; } = uint.MaxValue;

    /// <summary>
    /// Whether the card is a portability one (MoltenVK on a Mac), which does most of Vulkan and says so through
    /// VK_KHR_portability_subset. The device is made with that on, as the spec wants.
    /// </summary>
    public bool IsPortability { get; private set; }

    // Every queue is submitted to under this, the render thread and whoever uploads on a transfer queue included
    public readonly Lock QueueLock = new();

    private ExtDebugUtils? debugUtils;
    private DebugUtilsMessengerEXT messenger;
    private PfnDebugUtilsMessengerCallbackEXT? debugCallback;

    /// <summary>What the validation layer (or the driver) had to say, for the engine's log.</summary>
    public Action<string, DebugLevel, int>? DebugMessage { get; set; }

    public VulkanContext(IVkSurface surfaceSource)
    {
        Vk = Vk.GetApi();

        CreateInstance(surfaceSource);
        CreateSurface(surfaceSource);
        PickPhysicalDevice();
        CreateDevice();
    }

    private void CreateInstance(IVkSurface surfaceSource)
    {
        string? setting = Environment.GetEnvironmentVariable(VALIDATION_VARIABLE);
        bool wantValidation = !string.Equals(setting, "off", StringComparison.OrdinalIgnoreCase) && setting != "0";
#if !DEBUG
        wantValidation = string.Equals(setting, "on", StringComparison.OrdinalIgnoreCase) || setting == "1";
#endif
        ValidationEnabled = wantValidation && HasLayer(VALIDATION_LAYER);

        byte** windowExtensions = surfaceSource.GetRequiredExtensions(out uint windowExtensionCount);
        var extensions = new List<string>();
        for (uint i = 0; i < windowExtensionCount; i++)
            extensions.Add(Marshal.PtrToStringAnsi((nint)windowExtensions[i])!);
        if (ValidationEnabled) extensions.Add(ExtDebugUtils.ExtensionName);

        // A Mac's Vulkan is MoltenVK, which the loader keeps out of sight as a "portability" driver unless it is
        // asked for by name. Without this a Mac says it has no GPU at all
        var flags = InstanceCreateFlags.None;
        if (HasInstanceExtension(PORTABILITY_ENUMERATION))
        {
            extensions.Add(PORTABILITY_ENUMERATION);
            flags |= InstanceCreateFlags.EnumeratePortabilityBitKhr;
        }

        var layers = ValidationEnabled ? new[] { VALIDATION_LAYER } : [];

        var appInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = (byte*)SilkMarshal.StringToPtr("Horizon"),
            ApplicationVersion = Vk.MakeVersion(1, 0, 0),
            PEngineName = (byte*)SilkMarshal.StringToPtr("Horizon"),
            EngineVersion = Vk.MakeVersion(1, 0, 0),
            ApiVersion = Vk.Version13
        };

        var createInfo = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            Flags = flags,
            PApplicationInfo = &appInfo,
            EnabledExtensionCount = (uint)extensions.Count,
            PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(extensions),
            EnabledLayerCount = (uint)layers.Length,
            PpEnabledLayerNames = layers.Length > 0 ? (byte**)SilkMarshal.StringArrayToPtr(layers) : null
        };

        Check(Vk.CreateInstance(in createInfo, null, out Instance instance), "creating the instance");
        Instance = instance;

        SilkMarshal.Free((nint)appInfo.PApplicationName);
        SilkMarshal.Free((nint)appInfo.PEngineName);
        SilkMarshal.Free((nint)createInfo.PpEnabledExtensionNames);
        if (createInfo.PpEnabledLayerNames != null) SilkMarshal.Free((nint)createInfo.PpEnabledLayerNames);

        if (!Vk.TryGetInstanceExtension(Instance, out KhrSurface khrSurface))
            throw new InvalidOperationException("The Vulkan instance has no surface extension, there is nothing to draw into.");
        KhrSurface = khrSurface;

        if (ValidationEnabled && Vk.TryGetInstanceExtension(Instance, out ExtDebugUtils utils))
        {
            debugUtils = utils;
            debugCallback = new PfnDebugUtilsMessengerCallbackEXT(OnDebugMessage);

            var messengerInfo = new DebugUtilsMessengerCreateInfoEXT
            {
                SType = StructureType.DebugUtilsMessengerCreateInfoExt,
                MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.WarningBitExt | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
                MessageType = DebugUtilsMessageTypeFlagsEXT.ValidationBitExt | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt | DebugUtilsMessageTypeFlagsEXT.GeneralBitExt,
                PfnUserCallback = debugCallback.Value
            };

            utils.CreateDebugUtilsMessenger(Instance, in messengerInfo, null, out messenger);
            Log.Info("[Vulkan] The validation layer is on, every mistake is going in the log.");
        }
    }

    private uint OnDebugMessage(DebugUtilsMessageSeverityFlagsEXT severity, DebugUtilsMessageTypeFlagsEXT type, DebugUtilsMessengerCallbackDataEXT* data, void* user)
    {
        var level = severity switch
        {
            DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt => DebugLevel.High,
            DebugUtilsMessageSeverityFlagsEXT.WarningBitExt => DebugLevel.Medium,
            DebugUtilsMessageSeverityFlagsEXT.InfoBitExt => DebugLevel.Low,
            _ => DebugLevel.Note
        };

        string message = Marshal.PtrToStringAnsi((nint)data->PMessage) ?? string.Empty;
        DebugMessage?.Invoke(message, level, data->MessageIdNumber);
        return Vk.False;
    }

    private bool HasInstanceExtension(string name)
    {
        uint count = 0;
        Vk.EnumerateInstanceExtensionProperties((byte*)null, &count, null);
        if (count == 0) return false;

        var properties = new ExtensionProperties[count];
        fixed (ExtensionProperties* pointer = properties)
            Vk.EnumerateInstanceExtensionProperties((byte*)null, &count, pointer);

        foreach (var property in properties)
        {
            if (Marshal.PtrToStringAnsi((nint)property.ExtensionName) == name) return true;
        }

        return false;
    }

    private HashSet<string> DeviceExtensionsOf(PhysicalDevice device)
    {
        uint count = 0;
        Vk.EnumerateDeviceExtensionProperties(device, (byte*)null, &count, null);
        var properties = new ExtensionProperties[count];
        if (count > 0)
        {
            fixed (ExtensionProperties* pointer = properties)
                Vk.EnumerateDeviceExtensionProperties(device, (byte*)null, &count, pointer);
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            if (Marshal.PtrToStringAnsi((nint)property.ExtensionName) is { } name) names.Add(name);
        }

        return names;
    }

    /// <summary>
    /// Whether a card will do. Vulkan 1.3, or 1.2 with dynamic rendering and synchronization2 as extensions, which
    /// is what an older MoltenVK has, and a queue that can draw and present.
    /// </summary>
    private bool IsUsable(PhysicalDevice candidate, in PhysicalDeviceProperties properties)
    {
        if (properties.ApiVersion < Vk.Version12) return false;
        if (properties.ApiVersion < Vk.Version13)
        {
            var extensions = DeviceExtensionsOf(candidate);
            if (!extensions.Contains(DYNAMIC_RENDERING) || !extensions.Contains(SYNCHRONIZATION2)) return false;
        }

        return FindQueues(candidate, out _, out _, out _);
    }

    private bool HasLayer(string name)
    {
        uint count = 0;
        Vk.EnumerateInstanceLayerProperties(&count, null);
        if (count == 0) return false;

        var layers = new LayerProperties[count];
        fixed (LayerProperties* pointer = layers)
            Vk.EnumerateInstanceLayerProperties(&count, pointer);

        foreach (var layer in layers)
        {
            if (Marshal.PtrToStringAnsi((nint)layer.LayerName) == name) return true;
        }

        return false;
    }

    private void CreateSurface(IVkSurface surfaceSource)
    {
        Surface = surfaceSource.Create<AllocationCallbacks>(Instance.ToHandle(), null).ToSurface();
    }

    private void PickPhysicalDevice()
    {
        uint count = 0;
        Vk.EnumeratePhysicalDevices(Instance, &count, null);
        if (count == 0) throw new InvalidOperationException("There is no GPU that does Vulkan on this machine.");

        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* pointer = devices)
            Vk.EnumeratePhysicalDevices(Instance, &count, pointer);

        // A discrete card over an integrated one over whatever is left, as long as it can present and do 1.3
        PhysicalDevice? best = null;
        int bestScore = -1;
        foreach (var candidate in devices)
        {
            Vk.GetPhysicalDeviceProperties(candidate, out var properties);
            if (!IsUsable(candidate, in properties)) continue;

            int score = properties.DeviceType switch
            {
                PhysicalDeviceType.DiscreteGpu => 3,
                PhysicalDeviceType.IntegratedGpu => 2,
                PhysicalDeviceType.VirtualGpu => 1,
                _ => 0
            };

            if (score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        PhysicalDevice = best ?? throw new InvalidOperationException(
            "No GPU on this machine does Vulkan 1.3 (or 1.2 with dynamic rendering and synchronization2) with a queue that can draw and present. " +
            (OperatingSystem.IsMacOS() ? "On a Mac that is MoltenVK, 1.2.6 or later, which the game brings with it or the Vulkan SDK installs." : "Is the driver up to date?"));

        Vk.GetPhysicalDeviceProperties(PhysicalDevice, out var chosen);
        Vk.GetPhysicalDeviceMemoryProperties(PhysicalDevice, out var memory);
        Properties = chosen;
        MemoryProperties = memory;
        DeviceName = Marshal.PtrToStringAnsi((nint)chosen.DeviceName) ?? "a GPU";
        DriverVersion = $"{chosen.DriverVersion >> 22}.{(chosen.DriverVersion >> 14) & 0xFF}";
        TimestampPeriod = chosen.Limits.TimestampPeriod;
        TimestampsSupported = chosen.Limits.TimestampComputeAndGraphics && TimestampPeriod > 0.0f;

        DepthStencilFormat = SupportsDepthStencil(Format.D24UnormS8Uint) ? Format.D24UnormS8Uint : Format.D32SfloatS8Uint;
    }

    private bool SupportsDepthStencil(Format format)
    {
        Vk.GetPhysicalDeviceFormatProperties(PhysicalDevice, format, out var properties);
        return (properties.OptimalTilingFeatures & FormatFeatureFlags.DepthStencilAttachmentBit) != 0;
    }

    /// <summary>Whether a format can be written by compute shaders as a storage image, which the path tracer needs of its.</summary>
    public bool SupportsStorageImage(Format format)
    {
        Vk.GetPhysicalDeviceFormatProperties(PhysicalDevice, format, out var properties);
        return (properties.OptimalTilingFeatures & FormatFeatureFlags.StorageImageBit) != 0;
    }

    private bool FindQueues(PhysicalDevice device, out uint graphics, out int compute, out int transfer)
    {
        uint count = 0;
        Vk.GetPhysicalDeviceQueueFamilyProperties(device, &count, null);
        var families = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* pointer = families)
            Vk.GetPhysicalDeviceQueueFamilyProperties(device, &count, pointer);

        graphics = uint.MaxValue;
        compute = -1;
        transfer = -1;

        for (uint i = 0; i < count; i++)
        {
            var flags = families[i].QueueFlags;
            KhrSurface.GetPhysicalDeviceSurfaceSupport(device, i, Surface, out Bool32 presents);

            if (graphics == uint.MaxValue && (flags & QueueFlags.GraphicsBit) != 0 && presents)
                graphics = i;
            else if (compute < 0 && (flags & QueueFlags.ComputeBit) != 0 && (flags & QueueFlags.GraphicsBit) == 0)
                compute = (int)i;
            else if (transfer < 0 && (flags & QueueFlags.TransferBit) != 0 && (flags & (QueueFlags.GraphicsBit | QueueFlags.ComputeBit)) == 0)
                transfer = (int)i;
        }

        return graphics != uint.MaxValue;
    }

    private void CreateDevice()
    {
        FindQueues(PhysicalDevice, out uint graphics, out int compute, out int transfer);
        GraphicsFamily = graphics;
        HasAsyncCompute = compute >= 0;
        HasTransferQueue = transfer >= 0;
        ComputeFamily = HasAsyncCompute ? (uint)compute : graphics;
        TransferFamily = HasTransferQueue ? (uint)transfer : graphics;

        var families = new List<uint> { graphics };
        if (HasAsyncCompute) families.Add((uint)compute);
        if (HasTransferQueue) families.Add((uint)transfer);

        float priority = 1.0f;
        var queueInfos = new DeviceQueueCreateInfo[families.Count];
        for (int i = 0; i < families.Count; i++)
        {
            queueInfos[i] = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = families[i],
                QueueCount = 1,
                PQueuePriorities = &priority
            };
        }

        // What there is, so that only what is there is asked for. A 1.2 card has dynamic rendering and
        // synchronization2 as extensions, with feature structs of their own that are the 1.3 ones by other names
        bool is13 = Properties.ApiVersion >= Vk.Version13;
        var deviceExtensions = DeviceExtensionsOf(PhysicalDevice);

        var availableSync2 = new PhysicalDeviceSynchronization2Features { SType = StructureType.PhysicalDeviceSynchronization2Features };
        var availableDynamic = new PhysicalDeviceDynamicRenderingFeatures { SType = StructureType.PhysicalDeviceDynamicRenderingFeatures, PNext = &availableSync2 };
        var available13 = new PhysicalDeviceVulkan13Features { SType = StructureType.PhysicalDeviceVulkan13Features };
        var available12 = new PhysicalDeviceVulkan12Features { SType = StructureType.PhysicalDeviceVulkan12Features, PNext = is13 ? &available13 : (void*)&availableDynamic };
        var available11 = new PhysicalDeviceVulkan11Features { SType = StructureType.PhysicalDeviceVulkan11Features, PNext = &available12 };
        var available = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &available11 };
        Vk.GetPhysicalDeviceFeatures2(PhysicalDevice, &available);

        bool dynamicRendering = is13 ? available13.DynamicRendering : availableDynamic.DynamicRendering;
        bool synchronization2 = is13 ? available13.Synchronization2 : availableSync2.Synchronization2;
        if (!dynamicRendering || !synchronization2 || !available12.TimelineSemaphore || !available12.DescriptorBindingPartiallyBound)
            throw new InvalidOperationException($"{DeviceName} hasn't got dynamic rendering, synchronization2, timeline semaphores and partially bound descriptors, which the engine can't do without.");

        // What a set written after bind may hold, for the bindless table
        var indexingProperties = new PhysicalDeviceDescriptorIndexingProperties { SType = StructureType.PhysicalDeviceDescriptorIndexingProperties };
        var properties2 = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &indexingProperties };
        Vk.GetPhysicalDeviceProperties2(PhysicalDevice, &properties2);
        MaxUpdateAfterBindSampledImages = indexingProperties.MaxDescriptorSetUpdateAfterBindSampledImages;
        MaxUpdateAfterBindSamplers = indexingProperties.MaxDescriptorSetUpdateAfterBindSamplers;
        UpdateUnusedWhilePending = available12.DescriptorBindingUpdateUnusedWhilePending;
        IsPortability = deviceExtensions.Contains(PORTABILITY_SUBSET);

        if (!available12.DescriptorIndexing || !available12.DescriptorBindingSampledImageUpdateAfterBind || !available12.ShaderSampledImageArrayNonUniformIndexing || !available12.RuntimeDescriptorArray)
            throw new InvalidOperationException($"{DeviceName} hasn't got descriptor indexing with update after bind, which the bindless textures can't do without.");

        if (!available11.ShaderDrawParameters)
            throw new InvalidOperationException($"{DeviceName} hasn't got shader draw parameters, which the instanced quads can't do without.");

        var features13 = new PhysicalDeviceVulkan13Features
        {
            SType = StructureType.PhysicalDeviceVulkan13Features,
            DynamicRendering = true,
            Synchronization2 = true,
            Maintenance4 = available13.Maintenance4
        };

        // The same two for a 1.2 card, through the extensions
        var featuresSync2 = new PhysicalDeviceSynchronization2Features { SType = StructureType.PhysicalDeviceSynchronization2Features, Synchronization2 = true };
        var featuresDynamic = new PhysicalDeviceDynamicRenderingFeatures { SType = StructureType.PhysicalDeviceDynamicRenderingFeatures, PNext = &featuresSync2, DynamicRendering = true };

        var features12 = new PhysicalDeviceVulkan12Features
        {
            SType = StructureType.PhysicalDeviceVulkan12Features,
            PNext = is13 ? &features13 : (void*)&featuresDynamic,
            TimelineSemaphore = true,
            DescriptorIndexing = true,
            DescriptorBindingPartiallyBound = true,
            DescriptorBindingSampledImageUpdateAfterBind = true,
            DescriptorBindingUpdateUnusedWhilePending = available12.DescriptorBindingUpdateUnusedWhilePending,
            ShaderSampledImageArrayNonUniformIndexing = true,
            RuntimeDescriptorArray = true,
            HostQueryReset = available12.HostQueryReset
        };

        var features11 = new PhysicalDeviceVulkan11Features
        {
            SType = StructureType.PhysicalDeviceVulkan11Features,
            PNext = &features12,
            ShaderDrawParameters = true
        };

        var features = new PhysicalDeviceFeatures2
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &features11,
            Features = new PhysicalDeviceFeatures
            {
                FragmentStoresAndAtomics = available.Features.FragmentStoresAndAtomics,
                VertexPipelineStoresAndAtomics = available.Features.VertexPipelineStoresAndAtomics,
                ShaderStorageImageWriteWithoutFormat = available.Features.ShaderStorageImageWriteWithoutFormat,
                ShaderStorageImageReadWithoutFormat = available.Features.ShaderStorageImageReadWithoutFormat,
                IndependentBlend = available.Features.IndependentBlend,
                ShaderInt64 = available.Features.ShaderInt64,
                FillModeNonSolid = available.Features.FillModeNonSolid,
                WideLines = available.Features.WideLines
            }
        };

        var extensions = new List<string> { KhrSwapchain.ExtensionName };
        if (!is13)
        {
            extensions.Add(DYNAMIC_RENDERING);
            extensions.Add(SYNCHRONIZATION2);
            if (deviceExtensions.Contains(MAINTENANCE4)) extensions.Add(MAINTENANCE4);
        }

        // The spec says a portability card has to be made with this on, and MoltenVK is one
        if (IsPortability) extensions.Add(PORTABILITY_SUBSET);

        fixed (DeviceQueueCreateInfo* queues = queueInfos)
        {
            var createInfo = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                PNext = &features,
                QueueCreateInfoCount = (uint)queueInfos.Length,
                PQueueCreateInfos = queues,
                EnabledExtensionCount = (uint)extensions.Count,
                PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(extensions)
            };

            Check(Vk.CreateDevice(PhysicalDevice, in createInfo, null, out Device device), "creating the device");
            Device = device;
            SilkMarshal.Free((nint)createInfo.PpEnabledExtensionNames);
        }

        if (!Vk.TryGetDeviceExtension(Instance, Device, out KhrSwapchain khrSwapchain))
            throw new InvalidOperationException("The device has no swapchain extension.");
        KhrSwapchain = khrSwapchain;

        Vk.GetDeviceQueue(Device, graphics, 0, out Queue graphicsQueue);
        GraphicsQueue = graphicsQueue;

        if (HasAsyncCompute)
        {
            Vk.GetDeviceQueue(Device, (uint)compute, 0, out Queue computeQueue);
            ComputeQueue = computeQueue;
        }
        else ComputeQueue = GraphicsQueue;

        if (HasTransferQueue)
        {
            Vk.GetDeviceQueue(Device, (uint)transfer, 0, out Queue transferQueue);
            TransferQueue = transferQueue;
        }
        else TransferQueue = GraphicsQueue;

        Log.Info($"[Vulkan] {DeviceName}, driver {DriverVersion}, Vulkan {Properties.ApiVersion >> 22}.{(Properties.ApiVersion >> 12) & 0x3FF}{(IsPortability ? " (portability)" : "")}. " +
                 $"Async compute {(HasAsyncCompute ? "yes" : "no")}, transfer queue {(HasTransferQueue ? "yes" : "no")}, timestamps {(TimestampsSupported ? "yes" : "no")}.");
    }

    /// <summary>Throws with what went wrong, for the calls that have no business failing.</summary>
    public static void Check(Result result, string doing)
    {
        if (result != Result.Success)
            throw new InvalidOperationException($"Vulkan said {result} while {doing}.");
    }

    public void WaitIdle()
    {
        lock (QueueLock)
            Vk.DeviceWaitIdle(Device);
    }

    public void Dispose()
    {
        if (Device.Handle != 0)
        {
            Vk.DeviceWaitIdle(Device);
            Vk.DestroyDevice(Device, null);
        }

        if (Surface.Handle != 0) KhrSurface.DestroySurface(Instance, Surface, null);
        if (messenger.Handle != 0) debugUtils?.DestroyDebugUtilsMessenger(Instance, messenger, null);
        if (Instance.Handle != 0) Vk.DestroyInstance(Instance, null);
    }
}
