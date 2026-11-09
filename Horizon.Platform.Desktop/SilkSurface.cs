using System.Runtime.InteropServices;

using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;

namespace Horizon.Platform.Desktop;

/// <summary>
/// A desktop window's surface through Silk, whatever made the window (GLFW or SDL). It leans on nothing of the engine's
/// but the public surface contract.
/// <para>
/// On a Mac making the surface is SDL putting a Metal view into the window, and a view may only be put into a window by
/// the main thread. Done from the thread that draws nothing complains and every frame is drawn and presented, into a
/// layer the system never got told about, so the window stays blank. So there the making is handed to the thread of
/// the window, see DesktopHost.Initialize.
/// </para>
/// </summary>
internal sealed unsafe class SilkSurface : IPlatformSurface
{
    private readonly IVkSurface inner;
    private readonly Action<Action>? onWindowThread;
    private readonly Func<SurfaceSize> size;
    private readonly Func<bool> available;
    private readonly string[] extensions;

    /// <param name="inner">The window's own Vulkan surface maker.</param>
    /// <param name="onWindowThread">Runs work on the thread of the window and waits for it, null to make the surface wherever it's asked for.</param>
    /// <param name="size">The window's size in pixels.</param>
    /// <param name="available">Whether the window is still there to draw into.</param>
    public SilkSurface(IVkSurface inner, Action<Action>? onWindowThread, Func<SurfaceSize> size, Func<bool> available)
    {
        this.inner = inner;
        this.onWindowThread = onWindowThread;
        this.size = size;
        this.available = available;
        extensions = ExtensionsOf(inner);
    }

    public IReadOnlyList<string> VulkanInstanceExtensions => extensions;

    public bool IsAvailable => available();

    public SurfaceSize PixelSize => size();

    public ulong CreateVulkanSurface(nint instance, Func<string, nint> instanceFunction)
    {
        if (onWindowThread is null)
            return Make(instance);

        ulong made = 0;
        onWindowThread(() => made = Make(instance));
        return made;
    }

    // The allocator is left to the driver, so what kind of pointer it is doesn't matter
    private ulong Make(nint instance) => inner.Create<byte>(new VkHandle(instance), null).Handle;

    /// <summary>Helper method to read the extension names the window hands over as C strings.</summary>
    private static string[] ExtensionsOf(IVkSurface surface)
    {
        byte** names = surface.GetRequiredExtensions(out uint count);
        var read = new string[count];
        for (uint i = 0; i < count; i++)
            read[i] = Marshal.PtrToStringAnsi((nint)names[i])!;
        return read;
    }
}
