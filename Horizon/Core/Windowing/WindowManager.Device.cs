using Horizon.Graphics;
using Horizon.Graphics.Vulkan;
using Horizon.Core.Diagnostics;
using Horizon.Logging;
using Horizon.Platform;

namespace Horizon.Core;

// The GPU got going while everything else is. Making the instance and the device is a good part of what starting
// takes, and none of it needs the window until there's a surface to draw into. So where the platform knows ahead
// what its windows want (IPlatformHost.SurfaceAhead, a desktop's on Windows) it's started on a thread of its own the
// moment the window manager is made, and goes on while the game makes its first scene and the system makes the window. The thread that
// draws picks it up and gives it the window's surface. Everywhere else it's all made once the window is there, as it
// always was.
public partial class WindowManager
{
    // The instance, device and subsystems being made ahead of the window. Null where they aren't
    private Task<PreparedDevice?>? deviceAhead;

    /// <summary>Helper method to start making the device ahead of the window, see above. Once, as the window manager is made.</summary>
    private void StartDeviceAhead()
    {
        if (host.SurfaceAhead is { } ahead)
            deviceAhead = Task.Factory.StartNew(() => PrepareAhead(ahead), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private static PreparedDevice? PrepareAhead(IPlatformSurfaceAhead ahead)
    {
        PreparedDevice? made = PreparedDevice.Prepare(ahead);
        StartupTrace.Mark("device made ahead of the window");
        return made;
    }

    /// <summary>
    /// Helper method to make the GPU, on what was got going ahead of the window if anything was, the usual way if
    /// nothing was or it turned out no good for this window. The thread that draws.
    /// </summary>
    private GraphicsDevice MakeDevice(IPlatformSurface surface)
    {
        uint width = (uint)ViewportSize.X, height = (uint)ViewportSize.Y;
        PreparedDevice? ahead = TakeDeviceAhead();
        if (ahead is not null)
        {
            try
            {
                SurfaceAttachResult attached = ahead.AttachSurface(surface);
                if (attached == SurfaceAttachResult.Attached)
                {
                    Log.Info($"[{Name}] The device made ahead of the window took the window's surface.");
                    return new GraphicsDevice(ahead, surface, width, height, Display.VSync);
                }

                Log.Warning($"[{Name}] The device made ahead of the window can't draw into it ({attached}), making another.");
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
    private PreparedDevice? TakeDeviceAhead()
    {
        Task<PreparedDevice?>? making = Interlocked.Exchange(ref deviceAhead, null);
        return making?.GetAwaiter().GetResult();
    }
}
