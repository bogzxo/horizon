using CoreFoundation;

using Horizon.Engine;
using Horizon.Graphics;
using Horizon.Logging;

namespace Horizon.Platform.iOS;

/// <summary>
/// The engine of the process and what it's told by UIKit. iOS never tears the view down under a running app the way
/// Android does its activities, so this is simpler than AndroidSession. There's one view, the engine is made the first
/// time it's laid out with a size, and the only thing that comes and goes is the app being in front or not.
/// <para>
/// An app in the background must not touch the GPU at all, Metal kills one that does. So going to the background takes
/// the engine's swapchain and Vulkan surface off the view's layer (the same detach Android does in surfaceDestroyed)
/// and coming back puts them on again. Everything here is the main thread's.
/// </para>
/// </summary>
public sealed partial class IosSession
{
    private static IosSession? current;

    private HorizonAppDelegate? app;
    private HorizonMetalView? view;
    private GameEngine? engine;
    private IosHost? host;
    private IosAudioOutput? audio;
    private Task? stopping;
    private Task audioWork = Task.CompletedTask;
    private volatile bool closeRequested;
    private bool background;

    /// <summary>The session of the process, null before the app delegate made it.</summary>
    public static IosSession? Current => current;

    /// <summary>What the process was set up with.</summary>
    public IosRuntime Runtime { get; }

    /// <summary>The platform the engine is made on.</summary>
    public IosPlatform Platform { get; }

    /// <summary>The engine, null before the view was first laid out and after it stopped.</summary>
    public GameEngine? Engine => engine;

    /// <summary>The engine's host, null when there's no engine.</summary>
    public IosHost? Host => host;

    /// <summary>The engine's speakers for a device check, null with HORIZON_AUDIO=off or before the engine.</summary>
    public IosAudioOutput? Audio => audio;

    /// <summary>What went wrong starting or running the engine, null while all's well.</summary>
    public Exception? Failure { get; private set; }

    /// <summary>What the screen was like the last time the view was laid out.</summary>
    public IosDisplayInfo Screen { get; private set; }

    /// <summary>What happened to the layer so far, for a check and the log.</summary>
    public SurfaceRecord Surfaces { get; } = new();

    private IosSession(HorizonAppDelegate app, IosRuntime runtime)
    {
        this.app = app;
        Runtime = runtime;
        Platform = new IosPlatform(this);
        Screen = IosDisplayInfo.Of(null);
    }

    /// <summary>The process's session, made by the app delegate. Main thread.</summary>
    internal static IosSession For(HorizonAppDelegate app, IosRuntime runtime) => current ??= new IosSession(app, runtime);

    internal IosHost MakeHost(PlatformHostRequest request)
    {
        if (host is not null) throw new InvalidOperationException("The process has an engine already, iOS runs one at a time.");
        host = new IosHost(this, request);
        return host;
    }

    internal void HostGone(IosHost gone)
    {
        if (host == gone) host = null;
    }

    internal IosAudioOutput TrackAudio(IosAudioOutput output)
    {
        audio = output;
        return output;
    }

    /// <summary>
    /// The view was laid out, the first time it's in a window with a size and every turn after. Starts the engine the
    /// first time, tells it the new size after that. Main thread.
    /// </summary>
    internal void ViewLaidOut(HorizonMetalView laidOut)
    {
        view = laidOut;
        Screen = IosDisplayInfo.Of(laidOut);
        if (Failure is not null || stopping is not null || Screen.ViewPixels.X <= 0 || Screen.ViewPixels.Y <= 0) return;

        if (engine is null)
        {
            StartEngine(laidOut);
            return;
        }

        if (host is not { } running) return;
        if (running.Surface.Holding)
        {
            running.Surface.Resized((uint)Screen.ViewPixels.X, (uint)Screen.ViewPixels.Y);
            running.DisplayChanged(Screen, resized: true);
            Surfaces.Note($"layer resized to {Screen.ViewPixels.X}x{Screen.ViewPixels.Y}");
        }
        else if (!background) Attach(running, laidOut);
    }

    // The engine made and started for the view. Anything that throws is shown, never a silent crash
    private void StartEngine(HorizonMetalView first)
    {
        if (app is not { } starter) return;
        try
        {
            GameEngineConfiguration configuration = starter.Configure(Screen);
            engine = starter.MakeEngine(configuration with { Platform = Platform });
            if (host is null) throw new InvalidOperationException("The engine was made without asking the iOS platform for a host. Leave GameEngineConfiguration.Platform to HorizonAppDelegate.");

            // A screenshot key's pictures go where simctl get_app_container finds them, not into the read only bundle
            engine.ScreenshotDirectory = Runtime.Screenshots;

            host.Surface.Acquire(first.MetalLayer, (uint)Screen.ViewPixels.X, (uint)Screen.ViewPixels.Y);
            Surfaces.Note($"layer {Screen.ViewPixels.X}x{Screen.ViewPixels.Y} acquired for the first frame");

            Scene scene = starter.MakeFirstScene();
            engine.Start(scene);
            Log.Info($"[iOS] Engine started with {scene.GetType().Name}, the device is made on the render thread.");
        }
        catch (Exception e)
        {
            Fail("The engine couldn't start.", e);
        }
    }

    /// <summary>The engine asked to stop, said at once from whatever thread asked, before the main thread hears it.</summary>
    internal void MarkClosing() => closeRequested = true;

    /// <summary>The engine is asking to stop, because the game quit or its frames died. From the main queue, never from inside a frame.</summary>
    internal void CloseRequested()
    {
        closeRequested = true;
        BeginStop();
    }

    // Stops the engine without blocking, and once it's stopped lets go of it here on the main thread
    private Task? BeginStop()
    {
        if (engine is not { } stopped) return stopping;
        if (stopping is not null) return stopping;

        // The main thread stays free while the frames finish, a last frame may still post work to it
        stopping = stopped.StopAsync();
        stopping.ContinueWith(task => DispatchQueue.MainQueue.DispatchAsync(() => Stopped(task)), TaskScheduler.Default);
        return stopping;
    }

    private void Stopped(Task task)
    {
        Exception? died = task.Exception?.GetBaseException();
        try
        {
            engine?.Dispose();
        }
        catch (Exception e)
        {
            Log.Error($"[iOS] Disposing the engine threw. {e}");
            died ??= e;
        }

        engine = null;
        audio = null;
        Runtime.Logger.Flush();

        if (died is not null)
        {
            Fail("The engine stopped with an error.", died);
            return;
        }

        // An iOS app doesn't quit itself, the game's Exit leaves a screen that says it's over
        Log.Info("[iOS] Engine stopped.");
        app?.ShowStopped();
    }

    /// <summary>The app is being terminated, the engine goes with it as far as it gets in the time iOS gives. Main thread.</summary>
    internal void Terminating()
    {
        if (BeginStop() is { } stop) stop.Wait(TimeSpan.FromSeconds(2));
        Runtime.Logger.Flush(500);
    }

    /// <summary>Something went wrong that the player has to be told about. The engine is stopped and the app shows it.</summary>
    private void Fail(string what, Exception e)
    {
        Failure ??= e;
        Log.Fatal($"[iOS] {what} {e}");
        Runtime.Logger.Flush();

        if (engine is not null && stopping is null)
        {
            if (engine.WindowManager.IsRunning) BeginStop();
            else
            {
                try { engine.Dispose(); } catch (Exception again) { Log.Error($"[iOS] Disposing the engine that didn't start threw. {again.Message}"); }
                engine = null;
            }
        }

        app?.ShowFailure(what, e);
    }

    /// <summary>The app is in front and takes input (applicationDidBecomeActive), sound carries on.</summary>
    internal void Activated()
    {
        if (engine is not { } running || host is null) return;
        if (running.WindowManager.IsRunning) host.FocusChanged(true);
        Horizon.Audio.AudioDevice sound = running.Audio;
        audioWork = audioWork.ContinueWith(_ => sound.Resume(), TaskScheduler.Default);
    }

    /// <summary>
    /// The app isn't in front any more (applicationWillResignActive, a call, the notification centre pulled down). Sound
    /// holds where it is and anything held is let go of. Frames carry on until it's really in the background.
    /// </summary>
    internal void Deactivated()
    {
        if (engine is not { } running || host is null) return;
        host.Input.CancelAll();
        if (running.WindowManager.IsRunning) host.FocusChanged(false);
        Horizon.Audio.AudioDevice sound = running.Audio;
        audioWork = audioWork.ContinueWith(_ => sound.Pause(), TaskScheduler.Default);
    }

    /// <summary>The app went to the background. The GPU is let go of before this returns, see the class remarks. Main thread.</summary>
    internal void EnteredBackground()
    {
        background = true;
        if (host is not { } running) return;
        if (engine?.WindowManager.IsRunning == true) running.VisibleChanged(false);
        Detach(running);
        Runtime.Logger.Flush(500);
    }

    /// <summary>The app is coming back to the front, the layer goes back to the engine. Main thread.</summary>
    internal void EnteringForeground()
    {
        background = false;
        if (host is not { } running || stopping is not null) return;
        if (!running.Surface.Holding && view is { } shown) Attach(running, shown);
        if (engine?.WindowManager.IsRunning == true) running.VisibleChanged(true);
    }

    /// <summary>A GPU's name and what it's doing, for a check or the log. Null before the device is made.</summary>
    public static string? DescribeGraphics() => GraphicsDevice.IsAvailable ? GraphicsDevice.Current.Description : null;
}
