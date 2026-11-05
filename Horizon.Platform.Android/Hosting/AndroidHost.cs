using System.Numerics;

using Android.Content;
using Android.OS;

using Horizon.Logging;

namespace Horizon.Platform.Android;

/// <summary>
/// The engine on a phone, one per engine. Android owns the process and its main thread and calls the activity back
/// for everything, so there's no loop here (no <see cref="IPlatformLoop"/>). The main thread is the platform's, work
/// posted to it from the render or the simulation thread goes through the main looper's Handler, and Invoke waits for
/// it there. The activity feeds this its surface, its input and what it hears about the screen, see HorizonActivity.
/// <para>
/// What it offers is only what's true on a phone. There's no window to title, frame, resize or capture a pointer in,
/// so <see cref="Window"/> is null and the features are the clipboard and the pads. The surface is one
/// <see cref="AndroidSurface"/> for the engine's whole life, the SurfaceView's native windows come and go under it.
/// </para>
/// </summary>
public sealed class AndroidHost : IPlatformHost
{
    private readonly PlatformDispatcher dispatcher;
    private readonly Handler main;
    private readonly AndroidSession session;
    private IPlatformEvents? events;
    private AndroidInput? input;
    private int wakePosted;
    private volatile bool disposed;

    // Where the SurfaceView is in the activity's window and how its pixels go to the surface's, for the pointers
    private Vector2 viewOrigin, viewScale = Vector2.One;

    private volatile AndroidDisplayInfo screen;
    private volatile PlatformDisplay display;
    private bool focused, minimized;

    internal AndroidHost(AndroidSession session, PlatformHostRequest request)
    {
        this.session = session;
        Context = session.Context;
        ReadsGamepads = request.ReadsGamepads;
        main = new Handler(Looper.MainLooper!);
        dispatcher = new PlatformDispatcher(Wake);

        Features = PlatformFeatures.Clipboard | PlatformFeatures.AutomaticRenderScale | (request.ReadsGamepads ? PlatformFeatures.Gamepads : 0);
        screen = session.Screen;
        display = PlatformDisplay.Of(screen.ScreenSize) with { DisplayScale = screen.Density };
        Surface = new AndroidSurface();
    }

    /// <summary>The application's context, never an activity's, it outlives every one of them.</summary>
    public Context Context { get; }

    public PlatformFeatures Features { get; }

    /// <summary>A phone has no window of the desktop's.</summary>
    public IPlatformWindow? Window => null;

    /// <summary>Nothing is known of a surface before there is one, the device is made once the first one is.</summary>
    public IPlatformSurfaceAhead? SurfaceAhead => null;

    /// <summary>The SurfaceView's native window, the same object all along.</summary>
    public AndroidSurface Surface { get; }

    IPlatformSurface IPlatformHost.Surface => Surface;

    public AndroidInput Input => input ?? throw new InvalidOperationException("The Android host has no input until it's initialized.");

    IPlatformInput IPlatformHost.Input => Input;

    public PlatformState State { get; private set; } = null!;

    /// <summary>What the screen is like now.</summary>
    public AndroidDisplayInfo Screen => screen;

    /// <summary>What the engine was last told about sizes.</summary>
    public PlatformDisplay Display => display;

    internal bool ReadsGamepads { get; }

    /// <summary>The engine's speakers, AAudio. The engine checks HORIZON_AUDIO first and never asks when it says off.</summary>
    public IAudioOutput? CreateAudioOutput() => session.TrackAudio(new AAudioOutput());

    public void Attach(IPlatformEvents events)
    {
        if (this.events is not null) throw new InvalidOperationException("The Android host is attached already.");
        this.events = events;
    }

    public void Initialize()
    {
        if (events is null) throw new InvalidOperationException("The Android host has to be attached before it's initialized.");
        if (Looper.MainLooper?.IsCurrentThread != true) throw new InvalidOperationException("The Android host is the main thread's, start the engine from the activity.");

        dispatcher.BindToCurrentThread();
        input = new AndroidInput(this);
        display = Measure();
        State = new PlatformState(display, focused, minimized);
    }

    public bool IsPlatformThread => dispatcher.IsPlatformThread;

    public void Post(Action work) => dispatcher.Post(work);

    public void Invoke(Action work) => dispatcher.Invoke(work);

    /// <summary>Has the main thread do what's posted, once however many times it's asked before it gets there.</summary>
    public void Wake()
    {
        if (disposed || Interlocked.Exchange(ref wakePosted, 1) != 0) return;
        if (!main.Post(DoWork)) Interlocked.Exchange(ref wakePosted, 0);
    }

    /// <summary>
    /// The engine wants to stop (it was told to, or its frames died), which the session sees to on the main thread. From
    /// any thread. Asked by the game (Exit, an editor's File, Exit) it's a desktop window's X, whoever said they want a
    /// say (WindowManager.CloseRequested, something unsaved) gets it on the main thread first, and nothing about closing
    /// is touched unless they say yes. They ask again themselves once they're happy.
    /// </summary>
    public void RequestClose()
    {
        if (disposed) return;

        // The render thread only asks when its frames died, which nobody gets a say in, the frames are over either way.
        // surfaceDestroyed may be waiting on the main thread for them, so that's said at once, as it always was
        if (Thread.CurrentThread.Name == RENDER_THREAD)
        {
            session.MarkClosing();
            main.Post(session.CloseRequested);
            return;
        }

        main.Post(() =>
        {
            if (disposed) return;
            if (events is { } heard && !heard.MayClose()) return;
            session.MarkClosing();
            session.CloseRequested();
        });
    }

    // What WindowManager calls the thread that draws, the one that asks to close when its frames die
    private const string RENDER_THREAD = "Render";

    /// <summary>Does what was posted to the main thread, here and now. The main thread, also while it waits on the render thread.</summary>
    internal void DoWork()
    {
        Interlocked.Exchange(ref wakePosted, 0);
        if (disposed) return;
        dispatcher.Drain();
        events?.EventsProcessed();
    }

    /// <summary>Input came in, which wakes an engine that went quiet. Main thread.</summary>
    internal void HeardInput()
    {
        events?.InputHeard();
        events?.EventsProcessed();
    }

    /// <summary>Where the SurfaceView is in the activity's window and how big, in the pixels touches come in. Main thread.</summary>
    internal void ViewLaidOut(int left, int top, int width, int height)
    {
        viewOrigin = new Vector2(left, top);
        SurfaceSize size = Surface.PixelSize;
        viewScale = width > 0 && height > 0 && !size.IsEmpty ? new Vector2(size.Width / (float)width, size.Height / (float)height) : Vector2.One;
    }

    /// <summary>A touch's place in the activity's window as a pixel of what's drawn into.</summary>
    internal Vector2 ToPixels(float x, float y) => (new Vector2(x, y) - viewOrigin) * viewScale;

    /// <summary>The surface changed size, or the screen turned or changed density. Main thread.</summary>
    internal void DisplayChanged(AndroidDisplayInfo now, bool resized)
    {
        screen = now;
        PlatformDisplay measured = Measure();
        if (measured == display && !resized) return;
        display = measured;
        events?.DisplayChanged(measured, resized);
        events?.RequestFrames();
    }

    internal void FocusChanged(bool now)
    {
        focused = now;
        if (!now) input?.CancelAll();
        events?.FocusChanged(now);
    }

    internal void VisibleChanged(bool visible)
    {
        minimized = !visible;
        if (!visible) input?.CancelAll();
        events?.MinimizedChanged(!visible);
    }

    // The surface's own size when there is one, the last size known when it's gone, frames go on offscreen at that
    private PlatformDisplay Measure()
    {
        SurfaceSize size = Surface.PixelSize;
        Vector2 pixels = size.IsEmpty ? display.ViewportSize : new Vector2(size.Width, size.Height);
        if (pixels.X <= 0.0f || pixels.Y <= 0.0f) pixels = screen.ScreenSize;
        return new PlatformDisplay(pixels, pixels, pixels, screen.ScreenSize, Vector2.One, screen.Density);
    }

    /// <summary>
    /// Last, on the main thread, after the engine stopped and its device let go of the surface. The native window still
    /// held goes, so do the pads' listeners.
    /// </summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        dispatcher.Close();
        input?.Close();
        Surface.Dispose();
        session.HostGone(this);
        Log.Info("[Android] Host disposed.");
    }
}
