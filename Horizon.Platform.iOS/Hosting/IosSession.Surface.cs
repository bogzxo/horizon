using System.Diagnostics;

using Horizon.Graphics;
using Horizon.Logging;

namespace Horizon.Platform.iOS;

// The view's Metal layer going to the engine and coming back. The layer itself never goes away, the view owns it for
// the app's whole life, but an app in the background that submits GPU work is killed by Metal. So the main thread
// waits in applicationDidEnterBackground until the render thread has destroyed its swapchain and VkSurface
// (WindowManager.RequestSurfaceDetach), doing whatever the render thread posts it in the meantime so the two can't
// wait on each other. iOS gives that callback about five seconds, which is plenty for a frame or two.
public sealed partial class IosSession
{
    // How often a wait for the render thread that's taking long says so in the log
    private const int SLOW_DETACH_MILLISECONDS = 1000;

    private void Attach(IosHost running, HorizonMetalView shown)
    {
        try
        {
            running.Surface.Acquire(shown.MetalLayer, (uint)Screen.ViewPixels.X, (uint)Screen.ViewPixels.Y);
        }
        catch (Exception e)
        {
            Log.Error($"[iOS] The layer couldn't be taken again. {e.Message}");
            return;
        }

        running.DisplayChanged(Screen, resized: true);
        int generation = running.Surface.Generation;
        Surfaces.Note($"layer {running.Surface.PixelSize.Width}x{running.Surface.PixelSize.Height} acquired, generation {generation}");

        if (engine is not { } live) return;
        live.WindowManager.RequestSurfaceAttach().ContinueWith(task =>
        {
            string result = task.IsCompletedSuccessfully ? task.Result.ToString() : task.IsCanceled ? "cancelled, the frames stopped" : task.Exception!.GetBaseException().Message;
            Surfaces.Note($"attach of generation {generation}, {result}");
            if (task.IsCompletedSuccessfully && task.Result != SurfaceAttachResult.Attached && task.Result != SurfaceAttachResult.AlreadyAttached)
                Log.Warning($"[iOS] The engine didn't take the layer back, {result}.");
        }, TaskScheduler.Default);
    }

    // Doesn't return before the render thread has let go of the swapchain on the layer, or has stopped altogether
    private void Detach(IosHost running)
    {
        if (!running.Surface.Holding) return;

        // No new VkSurface from here on, and PixelSize is nothing, so nothing is acquired or presented on a size
        running.Surface.Withdraw();

        if (engine is not { } live || !live.WindowManager.IsRunning)
        {
            Surfaces.Note("layer withdrawn with no frames running, kept until the host goes");
            return;
        }

        long started = Stopwatch.GetTimestamp();
        Task<bool> detach = live.WindowManager.RequestSurfaceDetach();
        bool released = WaitForRenderThread(running, detach, started);

        double waited = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (released)
        {
            running.Surface.Release();
            Surfaces.Note($"layer detached in {waited:0} ms and released for the background");
        }
        else Surfaces.Note($"the frames stopped after {waited:0} ms instead, the layer is kept until the host goes");
    }

    // True once the engine detached, false once its frames stopped instead
    private bool WaitForRenderThread(IosHost running, Task<bool> detach, long started)
    {
        long nextWarning = SLOW_DETACH_MILLISECONDS;
        while (true)
        {
            if (detach.IsCompleted) return detach.IsCompletedSuccessfully;

            // A render thread that died or quit asks to close, and its surface requests may never be done then.
            // Stopping is what tells for sure when its frames are over
            if (closeRequested && stopping is null) BeginStop();
            if (stopping is { IsCompleted: true }) return detach.IsCompletedSuccessfully;

            // The render thread may be waiting on the main thread, which is this one
            running.DoWork();
            detach.Wait(4);

            double waited = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (waited >= nextWarning)
            {
                Log.Warning($"[iOS] Going to the background has waited {waited:0} ms for the render thread to let go of the layer.");
                nextWarning += SLOW_DETACH_MILLISECONDS;
            }
        }
    }
}

/// <summary>What happened to the layer, in order, with the time, for a device check and the log.</summary>
public sealed class SurfaceRecord
{
    private readonly Lock gate = new();
    private readonly List<string> lines = [];

    /// <summary>Everything so far.</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (gate) return [.. lines];
        }
    }

    internal void Note(string what)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} {what}";
        lock (gate) lines.Add(line);
        Log.Info("[iOS] " + what);
    }
}
