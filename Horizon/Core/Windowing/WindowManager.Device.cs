using Horizon.Graphics;
using Horizon.Graphics.Vulkan;
using Horizon.Core.Diagnostics;
using Horizon.Logging;

using Silk.NET.Core.Contexts;

namespace Horizon.Core;

// The GPU got going while everything else is. Making the instance and the device is a good part of what starting
// takes, and none of it needs the window until there's a surface to draw into. So on Windows it's started on a
// thread of its own the moment the window manager is made, and goes on while the game makes its first scene and the
// system makes the window. The thread that draws picks it up and gives it the window's surface. Everywhere else it's
// all made once the window is there, as it always was.
public partial class WindowManager
{
    // The instance and the device, being made ahead of the window. Null where they aren't
    private Task<VulkanContext?>? deviceAhead;

    // Set this in the environment to off and the device is made once the window is there, the way it is everywhere
    // else, for a driver that turns out not to like being got going early
    private const string DEVICE_AHEAD_VARIABLE = "HORIZON_DEVICE_AHEAD";

    /// <summary>Whether the device is got going before the window is there, see above.</summary>
    private static bool DeviceAhead => OperatingSystem.IsWindows() && !UsesSdl &&
        !string.Equals(Environment.GetEnvironmentVariable(DEVICE_AHEAD_VARIABLE), "off", StringComparison.OrdinalIgnoreCase);

    /// <summary>Helper method to start making the device ahead of the window, see above. Once, as the window manager is made.</summary>
    private void StartDeviceAhead()
    {
        if (DeviceAhead)
            deviceAhead = Task.Factory.StartNew(PrepareAhead, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private static VulkanContext? PrepareAhead()
    {
        VulkanContext? made = VulkanContext.Prepare();
        StartupTrace.Mark("device made ahead of the window");
        return made;
    }

    /// <summary>
    /// Helper method to make the GPU, on what was got going ahead of the window if anything was, the usual way if
    /// nothing was or it turned out no good for this window. The thread that draws.
    /// </summary>
    private GraphicsDevice MakeDevice(IVkSurface surface)
    {
        uint width = (uint)ViewportSize.X, height = (uint)ViewportSize.Y;
        VulkanContext? ahead = TakeDeviceAhead();
        if (ahead is not null)
        {
            try
            {
                if (ahead.Attach(surface))
                    return new GraphicsDevice(ahead, width, height, Display.VSync);

                Log.Warning($"[{Name}] The device made ahead of the window can't draw into it, making another.");
            }
            catch (Exception e)
            {
                Log.Warning($"[{Name}] The device made ahead of the window fell over getting its surface, making another. {e.Message}");
            }

            ahead.Dispose();
        }

        return new GraphicsDevice(surface, width, height, Display.VSync);
    }

    /// <summary>Helper method to wait for the device being made ahead and take it, null if there's none.</summary>
    private VulkanContext? TakeDeviceAhead()
    {
        Task<VulkanContext?>? making = Interlocked.Exchange(ref deviceAhead, null);
        return making?.GetAwaiter().GetResult();
    }
}
