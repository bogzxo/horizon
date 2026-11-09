using CoreAnimation;
using Silk.NET.Vulkan;

namespace Horizon.Platform.iOS;

/// <summary>The host keeps the Metal layer alive until the engine has detached its Vulkan surface.</summary>
public sealed class IosSurface : IPlatformSurface
{
    private static readonly string[] extensions = ["VK_KHR_surface", "VK_EXT_metal_surface"];
    private readonly Lock gate = new();
    private CAMetalLayer? layer;
    private bool withdrawn;
    private long size;

    public IReadOnlyList<string> VulkanInstanceExtensions => extensions;
    public SurfaceTransformPolicy TransformPolicy => SurfaceTransformPolicy.Current;
    public bool IsAvailable { get { lock (gate) return layer is not null && !withdrawn; } }
    public SurfaceSize PixelSize
    {
        get
        {
            long value = Interlocked.Read(ref size);
            return new((uint)value, (uint)(value >> 32));
        }
    }

    /// <summary>Called on the UIKit thread with a layer owned by the host's view.</summary>
    public void Acquire(CAMetalLayer value, uint width, uint height)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (gate)
        {
            if (layer is not null) throw new InvalidOperationException("Detach and release the previous Metal layer first.");
            if (value.Handle == 0) throw new ObjectDisposedException(nameof(value));
            layer = value;
            withdrawn = false;
            Resized(width, height);
        }
    }

    /// <summary>Pixels, already converted from UIKit points by the host.</summary>
    public void Resized(uint width, uint height) => Interlocked.Exchange(ref size, unchecked((long)(((ulong)height << 32) | width)));

    /// <summary>Stops new surface creation while the host waits for the render thread to detach.</summary>
    public void Withdraw() { lock (gate) withdrawn = true; }

    /// <summary>Called only after the engine acknowledges detachment. The view still owns the layer.</summary>
    public void Release()
    {
        lock (gate)
        {
            layer = null;
            withdrawn = true;
            Resized(0, 0);
        }
    }

    public unsafe ulong CreateVulkanSurface(nint instance, Func<string, nint> instanceFunction)
    {
        lock (gate)
        {
            if (layer is null || withdrawn) throw new InvalidOperationException("The Metal layer is unavailable.");
            nint address = instanceFunction("vkCreateMetalSurfaceEXT");
            if (address == 0) throw new NotSupportedException("MoltenVK did not expose VK_EXT_metal_surface.");
            var create = new MetalSurfaceCreateInfoEXT
            {
                SType = StructureType.MetalSurfaceCreateInfoExt,
                PLayer = (nint*)layer.Handle
            };
            ulong surface = 0;
            var call = (delegate* unmanaged[Cdecl]<nint, MetalSurfaceCreateInfoEXT*, void*, ulong*, Result>)address;
            Result result = call(instance, &create, null, &surface);
            GC.KeepAlive(layer);
            if (result != Result.Success || surface == 0)
                throw new InvalidOperationException($"Creating the Metal Vulkan surface failed with {result}.");
            return surface;
        }
    }
}
