using Android.Content;

using Horizon.Engine;
using Horizon.Graphics;
using Horizon.Logging;

namespace Horizon.Platform.Android;

/// <summary>
/// The engine of the process and what keeps it going while activities come and go. Android can tear an activity down
/// and make another for the same task (a configuration change the activity doesn't take itself, the system taking
/// back an activity in the background), and the engine, its device and everything loaded on it stay through that.
/// The new activity binds to the session and its SurfaceView's native window is handed to the same engine. An engine
/// is only ever made once a process, it isn't made again to fake coming back from the background.
/// <para>
/// Everything here is the main thread's. The surface's lifetime is the one place that waits, see <see cref="SurfaceDestroyed"/>.
/// </para>
/// </summary>
public sealed partial class AndroidSession
{
    private static AndroidSession? current;

    private HorizonActivity? activity;
    private GameEngine? engine;
    private AndroidHost? host;
    private AAudioOutput? audio;
    private Task? stopping;
    private Task audioWork = Task.CompletedTask;
    private readonly global::Android.OS.Handler main = new(global::Android.OS.Looper.MainLooper!);
    private volatile bool closeRequested;
    private bool quitAsked;

    /// <summary>The session of the process, null before the first activity made it.</summary>
    public static AndroidSession? Current => current;

    /// <summary>The application context.</summary>
    public Context Context { get; }

    /// <summary>What the process was set up with.</summary>
    public AndroidRuntime Runtime { get; }

    /// <summary>The platform the engine is made on.</summary>
    public AndroidPlatform Platform { get; }

    /// <summary>The engine, null before the first surface and after it stopped.</summary>
    public GameEngine? Engine => engine;

    /// <summary>The engine's host, null when there's no engine.</summary>
    public AndroidHost? Host => host;

    /// <summary>The engine's AAudio output for a device check, null with HORIZON_AUDIO=off or before the engine.</summary>
    public AAudioOutput? Audio => audio;

    /// <summary>What went wrong starting or running the engine, null while all's well.</summary>
    public Exception? Failure { get; private set; }

    /// <summary>What the screen was like the last time an activity looked.</summary>
    public AndroidDisplayInfo Screen { get; private set; }

    /// <summary>What happened to the surface so far, for a check and the log.</summary>
    public SurfaceRecord Surfaces { get; } = new();

    private AndroidSession(HorizonActivity first, AndroidRuntime runtime)
    {
        Context = first.ApplicationContext ?? first;
        Runtime = runtime;
        Platform = new AndroidPlatform(this);
        Screen = AndroidDisplayInfo.Of(first);
    }

    /// <summary>The process's session, made by the first activity. Main thread.</summary>
    internal static AndroidSession For(HorizonActivity activity, AndroidRuntime runtime) => current ??= new AndroidSession(activity, runtime);

    internal void Bind(HorizonActivity bound)
    {
        if (activity is not null && activity != bound) Log.Info("[Android] A new activity took over the running engine.");
        activity = bound;
        Screen = AndroidDisplayInfo.Of(bound);
        host?.DisplayChanged(Screen, resized: false);
    }

    internal void Unbind(HorizonActivity gone)
    {
        if (activity == gone) activity = null;
    }

    internal AndroidHost MakeHost(PlatformHostRequest request)
    {
        if (host is not null) throw new InvalidOperationException("The process has an engine already, Android runs one at a time.");
        host = new AndroidHost(this, request);
        return host;
    }

    internal void HostGone(AndroidHost gone)
    {
        if (host == gone) host = null;
    }

    internal AAudioOutput TrackAudio(AAudioOutput output)
    {
        audio = output;
        return output;
    }

    /// <summary>The screen turned or changed density. Main thread.</summary>
    internal void ScreenChanged(HorizonActivity from)
    {
        if (from != activity) return;
        Screen = AndroidDisplayInfo.Of(from);
        host?.DisplayChanged(Screen, resized: false);
    }

    // The engine made and started for the first native window. Anything that throws is shown, never a silent crash
    private void StartEngine(HorizonActivity starter, global::Android.Views.Surface surface, int width, int height)
    {
        try
        {
            GameEngineConfiguration configuration = starter.Configure();
            engine = starter.MakeEngine(configuration with { Platform = Platform });
            if (host is null) throw new InvalidOperationException("The engine was made without asking the Android platform for a host. Leave GameEngineConfiguration.Platform to HorizonActivity.");

            // A screenshot key's pictures go where adb pull finds them, not into the working directory (the root here)
            engine.ScreenshotDirectory = Runtime.Paths.Screenshots;

            Surfaces.Note($"surface {width}x{height} acquired for the first frame");
            host.Surface.Acquire(surface);
            heldBy = starter;
            if (width > 0 && height > 0) host.Surface.Resized(width, height);
            starter.LayOut();

            Scene scene = starter.MakeFirstScene();
            engine.Start(scene);
            Log.Info($"[Android] Engine started with {scene.GetType().Name}, the device is made on the render thread.");
        }
        catch (Exception e)
        {
            Fail("The engine couldn't start.", e);
        }
    }

    /// <summary>The engine asked to stop, said at once from whatever thread asked, before the main thread hears it.</summary>
    internal void MarkClosing() => closeRequested = true;

    /// <summary>
    /// The engine is asking to stop, because the game quit or its frames died. From the main thread's queue, never
    /// from inside a frame.
    /// </summary>
    internal void CloseRequested()
    {
        closeRequested = true;
        quitAsked = true;
        BeginStop();
    }

    // Stops the engine without blocking, and once it's stopped lets go of it here on the main thread
    private Task? BeginStop()
    {
        if (engine is not { } stopped) return stopping;
        if (stopping is not null) return stopping;

        // The main thread stays free while the frames finish, a last frame may still post work to it
        stopping = stopped.StopAsync();
        stopping.ContinueWith(task => main.Post(() => Stopped(task)), TaskScheduler.Default);
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
            Log.Error($"[Android] Disposing the engine threw. {e}");
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

        Log.Info("[Android] Engine stopped.");
        if (quitAsked) activity?.Finish();
    }

    /// <summary>The activity is finishing for good (Back, the recents list), the engine goes with it. Main thread.</summary>
    internal void ActivityFinishing(HorizonActivity finishing)
    {
        if (finishing != activity) return;
        BeginStop();
    }

    /// <summary>Something went wrong that the player has to be told about. The engine is stopped and the activity shows it.</summary>
    private void Fail(string what, Exception e)
    {
        Failure ??= e;
        Log.Fatal($"[Android] {what} {e}");
        Runtime.Logger.Flush();

        // Stopping first. Showing the failure takes the SurfaceView down, and its surfaceDestroyed must find the frames
        // on their way out rather than wait on a render thread that's never coming
        if (engine is not null && stopping is null)
        {
            if (engine.WindowManager.IsRunning) BeginStop();
            else
            {
                try { engine.Dispose(); } catch (Exception again) { Log.Error($"[Android] Disposing the engine that didn't start threw. {again.Message}"); }
                engine = null;
            }
        }

        activity?.ShowFailure(what, e);
    }

    /// <summary>The activity is in front and takes input (onResume), sound carries on.</summary>
    internal void Resumed(HorizonActivity from)
    {
        if (from != activity || engine is not { } running) return;
        Horizon.Audio.AudioDevice sound = running.Audio;
        audioWork = audioWork.ContinueWith(_ => sound.Resume(), TaskScheduler.Default);
    }

    /// <summary>
    /// The activity isn't in front any more (onPause). Sound holds where it is and anything held is let go of. The
    /// simulation and the frames carry on, that's the core lifecycle's to stop (stage 3e).
    /// </summary>
    internal void Paused(HorizonActivity from)
    {
        if (from != activity || engine is not { } running) return;
        host?.Input.CancelAll();

        // AAudio's pause waits for the stream to get there, which isn't the main thread's to sit through
        Horizon.Audio.AudioDevice sound = running.Audio;
        audioWork = audioWork.ContinueWith(_ => sound.Pause(), TaskScheduler.Default);
    }

    internal void VisibleChanged(HorizonActivity from, bool visible)
    {
        if (from == activity && host is not null && engine?.WindowManager.IsRunning == true) host.VisibleChanged(visible);
    }

    internal void FocusChanged(HorizonActivity from, bool focused)
    {
        if (from == activity && host is not null && engine?.WindowManager.IsRunning == true) host.FocusChanged(focused);
    }

    /// <summary>A GPU's name and what it's doing, for a check or the log. Null before the device is made.</summary>
    public static string? DescribeGraphics() => GraphicsDevice.IsAvailable ? GraphicsDevice.Current.Description : null;
}
