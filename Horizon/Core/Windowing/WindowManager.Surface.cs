using System.Collections.Concurrent;

using Horizon.Core.Threading;
using Horizon.Graphics;
using Horizon.Logging;
using Horizon.Platform;

namespace Horizon.Core;

// The surface the frames are shown in going away and coming back while the game runs, which is what a phone does
// every time the game goes to the background. Whoever runs the platform asks for it from any thread, and the thread
// that draws does it between two frames, so no frame ever has a picture of a swapchain that is being let go of. While
// there's no surface frames go on, drawing into render targets and showing nothing, a few dozen a second at most.
// Stopping the frames and the simulation altogether is the lifecycle's job, stage 3e of notes/android-plan.md.
public partial class WindowManager
{
    // The window's surface, what the device is given again after it was taken away
    private IPlatformSurface? surface;

    private readonly ConcurrentQueue<SurfaceRequest> surfaceRequests = new();

    // How often frames are drawn at most while there's nothing to show them in
    private const double FRAMES_WITHOUT_SURFACE = 30.0;

    /// <summary>
    /// Has the device let go of the window's surface between two frames, keeping itself and everything made on it,
    /// see <see cref="GraphicsDevice.DetachSurface"/>. Done when the task is, with false if there was none to let go
    /// of. From any thread, for whoever runs the platform and for tests that take the surface away on purpose.
    /// </summary>
    public Task<bool> RequestSurfaceDetach()
    {
        var request = new SurfaceRequest(attach: false);
        surfaceRequests.Enqueue(request);
        RequestFrames();
        return request.Detached.Task;
    }

    /// <summary>
    /// Gives the device the window's surface again between two frames, see <see cref="GraphicsDevice.AttachSurface"/>.
    /// From any thread.
    /// </summary>
    public Task<SurfaceAttachResult> RequestSurfaceAttach()
    {
        var request = new SurfaceRequest(attach: true);
        surfaceRequests.Enqueue(request);
        RequestFrames();
        return request.Attached.Task;
    }

    /// <summary>Helper method to do whatever was asked of the surface. The thread that draws, between frames.</summary>
    private void DoSurfaceRequests()
    {
        while (surfaceRequests.TryDequeue(out SurfaceRequest? request))
        {
            try
            {
                if (request.Attach)
                    request.Attached.TrySetResult(surface is null ? SurfaceAttachResult.Unavailable : Graphics!.AttachSurface(surface));
                else
                    request.Detached.TrySetResult(Graphics!.DetachSurface());
            }
            catch (Exception e)
            {
                Log.Error($"[{Name}] The surface couldn't be {(request.Attach ? "given back" : "taken away")}. {e.Message}");
                request.Fail(e);
                throw;
            }
        }
    }

    /// <summary>Helper method to let whoever is still waiting on the surface know there are no frames left to do it in.</summary>
    private void CancelSurfaceRequests()
    {
        while (surfaceRequests.TryDequeue(out SurfaceRequest? request))
            request.Cancel();
    }

    /// <summary>Helper method to hold frames back while there's nothing to show them in, which nothing else does then.</summary>
    private void WaitWithoutSurface()
    {
        if (Graphics is null || Graphics.SurfaceState == SurfaceState.Attached || !surfaceRequests.IsEmpty)
            return;

        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        LoopTiming.WaitUntil(now + 1.0 / FRAMES_WITHOUT_SURFACE, spin: false);
    }

    private sealed class SurfaceRequest(bool attach)
    {
        public bool Attach { get; } = attach;
        public TaskCompletionSource<bool> Detached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<SurfaceAttachResult> Attached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Fail(Exception e)
        {
            Detached.TrySetException(e);
            Attached.TrySetException(e);
        }

        public void Cancel()
        {
            Detached.TrySetCanceled();
            Attached.TrySetCanceled();
        }
    }
}
