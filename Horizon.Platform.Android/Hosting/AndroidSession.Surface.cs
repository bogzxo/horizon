using System.Diagnostics;

using Horizon.Graphics;
using Horizon.Logging;

namespace Horizon.Platform.Android;

// The SurfaceView's native window coming and going under the running engine. Android's contract for surfaceDestroyed
// is "After returning from this call, you should no longer try to access this surface. If you have a rendering thread
// that directly accesses the surface, you must ensure that thread is no longer touching the Surface before returning
// from this function." (developer.android.com, SurfaceHolder.Callback). Holding an ANativeWindow reference of our own
// keeps the memory there, it doesn't make drawing into it after the callback right. So the main thread waits in there
// until the render thread has destroyed its swapchain and VkSurface (WindowManager.RequestSurfaceDetach), doing
// whatever the render thread posts it in the meantime so the two can't wait on each other.
public sealed partial class AndroidSession
{
    // How often a wait for the render thread that's taking long says so in the log
    private const int SLOW_DETACH_MILLISECONDS = 1000;

    // A native window that came in while the old one was still held (only after a detach that never completed)
    private global::Android.Views.Surface? waiting;

    // Whose SurfaceView the window held came from, a destroyed callback of any other activity's isn't about it
    private HorizonActivity? heldBy;

    /// <summary>surfaceCreated. Starts the engine on the first one, gives the engine the new window on every one after. Main thread.</summary>
    internal void SurfaceCreated(HorizonActivity from, global::Android.Views.Surface surface, int width, int height)
    {
        if (from != activity || Failure is not null || stopping is not null) return;

        if (engine is null)
        {
            StartEngine(from, surface, width, height);
            return;
        }

        if (host is not { } running) return;
        if (running.Surface.Holding)
        {
            // The old window is still the engine's, the new one waits for it to be let go of
            waiting = surface;
            Surfaces.Note("surface came while the old one was still held, it waits");
            return;
        }

        Attach(running, surface, width, height);
    }

    private void Attach(AndroidHost running, global::Android.Views.Surface surface, int width, int height)
    {
        try
        {
            running.Surface.Acquire(surface);
            heldBy = activity;
        }
        catch (Exception e)
        {
            Log.Error($"[Android] The new native window couldn't be taken. {e.Message}");
            return;
        }

        if (width > 0 && height > 0) running.Surface.Resized(width, height);
        activity?.LayOut();
        running.DisplayChanged(Screen, resized: true);
        Surfaces.Note($"surface {running.Surface.PixelSize.Width}x{running.Surface.PixelSize.Height} acquired, generation {running.Surface.Generation}");

        int generation = running.Surface.Generation;
        engine!.WindowManager.RequestSurfaceAttach().ContinueWith(task =>
        {
            string result = task.IsCompletedSuccessfully ? task.Result.ToString() : task.IsCanceled ? "cancelled, the frames stopped" : task.Exception!.GetBaseException().Message;
            Surfaces.Note($"attach of generation {generation}, {result}");
            if (task.IsCompletedSuccessfully && task.Result != SurfaceAttachResult.Attached && task.Result != SurfaceAttachResult.AlreadyAttached)
                Log.Warning($"[Android] The engine didn't take the new surface, {result}.");
        }, TaskScheduler.Default);
    }

    /// <summary>surfaceChanged, a new size (a turn, the first layout). Main thread.</summary>
    internal void SurfaceChanged(HorizonActivity from, int width, int height)
    {
        if (from != activity || host is not { } running || !running.Surface.Holding) return;
        running.Surface.Resized(width, height);
        activity?.LayOut();
        running.DisplayChanged(Screen, resized: true);
        Surfaces.Note($"surface changed to {width}x{height}");
    }

    /// <summary>
    /// surfaceDestroyed. Doesn't return before the render thread has let go of the swapchain on the window, or has
    /// stopped altogether, see the remarks at the top of this file. Main thread.
    /// </summary>
    internal void SurfaceDestroyed(HorizonActivity from)
    {
        if (from == activity) waiting = null;
        if (from != heldBy || host is not { } running || !running.Surface.Holding) return;

        // No new VkSurface from here on, and PixelSize is nothing, so nothing is acquired or presented on a size
        running.Surface.Withdraw();

        if (engine is not { } live || !live.WindowManager.IsRunning)
        {
            // Never started, so nobody draws, the window goes with the host
            Surfaces.Note("surface destroyed with no frames running, kept until the host goes");
            return;
        }

        long started = Stopwatch.GetTimestamp();
        Task<bool> detach = live.WindowManager.RequestSurfaceDetach();
        bool released = WaitForRenderThread(running, detach, started);

        double waited = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (released)
        {
            running.Surface.Release();
            heldBy = null;
            Surfaces.Note($"surface destroyed, detached in {waited:0} ms and released");
        }
        else
        {
            // The frames stopped before they got to the detach. Nobody draws any more, but the swapchain still exists
            // until the device goes, so the window is kept until the host is disposed after it
            Surfaces.Note($"surface destroyed, the frames stopped after {waited:0} ms, kept until the host goes");
        }

        // A window that came in while this one was held gets its turn now
        if (waiting is { } next && !running.Surface.Holding && activity is not null)
        {
            waiting = null;
            Attach(running, next, 0, 0);
        }
    }

    // True once the engine detached, false once its frames stopped instead. Waits as long as it takes, a render thread
    // that never answers is an ANR, which is Android saying what's true, rather than this returning on a surface in use
    private bool WaitForRenderThread(AndroidHost running, Task<bool> detach, long started)
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
                Log.Warning($"[Android] surfaceDestroyed has waited {waited:0} ms for the render thread to let go of the surface.");
                nextWarning += SLOW_DETACH_MILLISECONDS;
            }
        }
    }
}

/// <summary>What happened to the surface, in order, with the time, for a device check and the log.</summary>
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
        Log.Info("[Android] " + what);
    }
}
