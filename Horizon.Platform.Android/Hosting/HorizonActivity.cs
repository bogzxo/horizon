using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Views;

using Horizon.Engine;
using Horizon.Logging;

namespace Horizon.Platform.Android;

/// <summary>
/// The activity a game on a phone derives from. It sets the process up (logcat and debug.log, the run's HORIZON_*
/// settings, the APK's assets as content, the app's folders as storage), puts a fullscreen SurfaceView up and hands
/// its native window to the engine, which it makes and starts the first time there is one. Everything after that is
/// Android calling back, input, the surface coming and going, the app going to the back and coming to the front.
/// <code>
/// [Activity(MainLauncher = true, Exported = true, ConfigurationChanges = HorizonActivity.HANDLED_CHANGES, Theme = HorizonActivity.THEME)]
/// public sealed class GameActivity : HorizonActivity
/// {
///     public override Scene MakeFirstScene() => new MainMenuScene();
/// }
/// </code>
/// The engine belongs to the process's <see cref="AndroidSession"/>, not to this activity, so an activity Android
/// makes again (a configuration change it wasn't told to take itself) finds the engine running and gives it its
/// surface. The engine stops only when the activity finishes for good or the game quits.
/// </summary>
public abstract partial class HorizonActivity : Activity
{
    /// <summary>
    /// The configuration changes the activity takes itself rather than being made again, a turn, a resize, a keyboard
    /// plugged in, a density change. Put in the derived activity's [Activity] attribute.
    /// </summary>
    public const ConfigChanges HANDLED_CHANGES = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout |
        ConfigChanges.SmallestScreenSize | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation |
        ConfigChanges.UiMode | ConfigChanges.Density | ConfigChanges.Touchscreen;

    /// <summary>A theme with no title bar or action bar, the SurfaceView is all there is.</summary>
    public const string THEME = "@android:style/Theme.DeviceDefault.NoActionBar.Fullscreen";

    private SurfaceView? view;
    private SurfaceCallbacks? callbacks;

    /// <summary>The process's session, there from OnCreate on unless setting the process up failed.</summary>
    protected AndroidSession? Session { get; private set; }

    /// <summary>How the engine is made, a window the size of the screen as it was when the activity came up. Called once a process.</summary>
    public virtual GameEngineConfiguration Configure()
    {
        AndroidDisplayInfo screen = AndroidDisplayInfo.Of(this);
        return new GameEngineConfiguration
        {
            WindowConfiguration = Horizon.Core.WindowManagerConfiguration.Default1600x900 with
            {
                WindowSize = screen.ScreenSize,
                WindowTitle = Title ?? "Horizon",
                Fullscreen = true,
                VSync = true
            }
        };
    }

    /// <summary>Makes the engine out of the configuration, whose platform is set to Android. Called once a process.</summary>
    public virtual GameEngine MakeEngine(GameEngineConfiguration configuration) => new(configuration);

    /// <summary>The scene the engine starts in. Called once a process, right before the engine starts.</summary>
    public abstract Scene MakeFirstScene();

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        AndroidRuntime runtime;
        try
        {
            runtime = AndroidBootstrap.Start(this, Intent);
        }
        catch (Exception e)
        {
            Log.Fatal($"[Android] The app couldn't be set up. {e}");
            ShowFailure("The app couldn't be set up.", e);
            return;
        }

        Session = AndroidSession.For(this, runtime);
        Session.Bind(this);

        if (Session.Failure is { } failed)
        {
            ShowFailure("The engine had stopped with an error.", failed);
            return;
        }

        view = new HorizonSurfaceView(this, () => Session?.Engine is not null ? Session.Host?.Input : null);
        callbacks = new SurfaceCallbacks(this);
        view.Holder!.AddCallback(callbacks);
        view.LayoutChange += (_, _) => LayOut();
        SetContentView(view);
        GoFullscreen();
        view.RequestFocus();

        ListenForBack();
    }

    /// <summary>
    /// Has the engine know where the SurfaceView is and how big, for turning touches into pixels. Called after every
    /// layout and every new surface.
    /// </summary>
    internal void LayOut()
    {
        if (view is null || Session?.Host is not { } host) return;
        int[] origin = new int[2];
        view.GetLocationInWindow(origin);
        host.ViewLaidOut(origin[0], origin[1], view.Width, view.Height);
    }

    /// <summary>Asks the game to stop, the same as Exit, and the activity finishes once it has.</summary>
    public void RequestQuit()
    {
        if (Session?.Engine is { } engine) engine.Exit();
        else Finish();
    }

    protected override void OnStart()
    {
        base.OnStart();
        Session?.VisibleChanged(this, true);
    }

    protected override void OnResume()
    {
        base.OnResume();
        Session?.Resumed(this);
    }

    protected override void OnPause()
    {
        Session?.Paused(this);
        base.OnPause();
    }

    protected override void OnStop()
    {
        Session?.VisibleChanged(this, false);
        ProcessRuntime?.Logger.Flush(500);
        base.OnStop();
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus) GoFullscreen();
        Session?.FocusChanged(this, hasFocus);
    }

    public override void OnConfigurationChanged(global::Android.Content.Res.Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        Session?.ScreenChanged(this);
    }

    public override void OnTrimMemory([GeneratedEnum] TrimMemory level)
    {
        base.OnTrimMemory(level);

        // The engine has no LowMemory event to hand this on to yet, see android-host-results.md
        Log.Warning($"[Android] The system is short of memory ({level}).");
    }

    protected override void OnDestroy()
    {
        StopListeningForBack();
        if (Session is { } session)
        {
            if (IsFinishing) session.ActivityFinishing(this);
            session.Unbind(this);
        }

        view?.Holder?.RemoveCallback(callbacks!);
        callbacks?.Dispose();
        callbacks = null;
        view = null;
        base.OnDestroy();
    }

    private AndroidRuntime? ProcessRuntime => Session?.Runtime;

    // What the SurfaceView's holder says, all on the main thread
    private sealed class SurfaceCallbacks(HorizonActivity activity) : Java.Lang.Object, ISurfaceHolderCallback
    {
        public void SurfaceCreated(ISurfaceHolder holder)
        {
            if (holder.Surface is not { IsValid: true } surface) return;
            global::Android.Graphics.Rect? frame = holder.SurfaceFrame;
            activity.Session?.SurfaceCreated(activity, surface, frame?.Width() ?? 0, frame?.Height() ?? 0);
        }

        public void SurfaceChanged(ISurfaceHolder holder, [GeneratedEnum] global::Android.Graphics.Format format, int width, int height) =>
            activity.Session?.SurfaceChanged(activity, width, height);

        public void SurfaceDestroyed(ISurfaceHolder holder) => activity.Session?.SurfaceDestroyed(activity);
    }
}
