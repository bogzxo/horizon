using System.Numerics;

using CoreFoundation;

using Foundation;

using Horizon.Logging;

namespace Horizon.Platform.iOS;

/// <summary>
/// The engine on an iPhone, one per engine. UIKit owns the process and its main thread and calls the app back for
/// everything, so there's no loop here (no <see cref="IPlatformLoop"/>). The main thread is the platform's, work posted
/// to it from the render or the simulation thread goes through the main dispatch queue, and Invoke waits for it there.
/// The view controller feeds this its layer, its touches and keys and what it hears about the screen.
/// <para>
/// What it offers is only what's true on a phone. There's no window to title, frame, resize or capture a pointer in,
/// so <see cref="Window"/> is null and the features are the clipboard, the pads and the automatic render scale. The surface is one
/// <see cref="IosSurface"/> for the engine's whole life, it lets go of the view's layer while the app is in the
/// background and takes it again when it comes back.
/// </para>
/// </summary>
public sealed class IosHost : IPlatformHost
{
    private readonly PlatformDispatcher dispatcher;
    private readonly IosSession session;
    private IPlatformEvents? events;
    private IosInput? input;
    private int wakePosted;
    private volatile bool disposed;

    private volatile IosDisplayInfo screen;
    private volatile PlatformDisplay display;
    private bool focused, minimized;

    internal IosHost(IosSession session, PlatformHostRequest request)
    {
        this.session = session;
        ReadsGamepads = request.ReadsGamepads;
        dispatcher = new PlatformDispatcher(Wake);

        Features = PlatformFeatures.Clipboard | PlatformFeatures.AutomaticRenderScale | (request.ReadsGamepads ? PlatformFeatures.Gamepads : 0);
        screen = session.Screen;
        display = PlatformDisplay.Of(screen.ViewPixels) with { DisplayScale = screen.Scale };
        Surface = new IosSurface();
    }

    public PlatformFeatures Features { get; }

    /// <summary>A phone has no window of the desktop's.</summary>
    public IPlatformWindow? Window => null;

    /// <summary>Nothing is known of a surface before there is one, the device is made once the layer is there.</summary>
    public IPlatformSurfaceAhead? SurfaceAhead => null;

    /// <summary>The view's Metal layer, the same object all along.</summary>
    public IosSurface Surface { get; }

    IPlatformSurface IPlatformHost.Surface => Surface;

    public IosInput Input => input ?? throw new InvalidOperationException("The iOS host has no input until it's initialized.");

    IPlatformInput IPlatformHost.Input => Input;

    public PlatformState State { get; private set; } = null!;

    /// <summary>What the screen is like now.</summary>
    public IosDisplayInfo Screen => screen;

    /// <summary>What the engine was last told about sizes.</summary>
    public PlatformDisplay Display => display;

    internal bool ReadsGamepads { get; }

    /// <summary>The engine's speakers, AVAudioEngine. The engine checks HORIZON_AUDIO first and never asks when it says off.</summary>
    public IAudioOutput? CreateAudioOutput() => session.TrackAudio(new IosAudioOutput());

    public void Attach(IPlatformEvents events)
    {
        if (this.events is not null) throw new InvalidOperationException("The iOS host is attached already.");
        this.events = events;
    }

    public void Initialize()
    {
        if (events is null) throw new InvalidOperationException("The iOS host has to be attached before it's initialized.");
        if (!NSThread.IsMain) throw new InvalidOperationException("The iOS host is the main thread's, start the engine from the app delegate.");

        dispatcher.BindToCurrentThread();
        input = new IosInput(this);
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
        DispatchQueue.MainQueue.DispatchAsync(DoWork);
    }

    /// <summary>
    /// The engine wants to stop (it was told to, or its frames died), which the session sees to on the main thread. From
    /// any thread. Whoever said they want a say (WindowManager.CloseRequested, something unsaved) gets it on the main
    /// thread first. An iOS app never quits itself, so stopping the engine is as far as it goes.
    /// </summary>
    public void RequestClose()
    {
        if (disposed) return;

        // The render thread only asks when its frames died, which nobody gets a say in. A background detach may be
        // waiting on the main thread for them, so that's said at once
        if (Thread.CurrentThread.Name == RENDER_THREAD)
        {
            session.MarkClosing();
            DispatchQueue.MainQueue.DispatchAsync(session.CloseRequested);
            return;
        }

        DispatchQueue.MainQueue.DispatchAsync(() =>
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

    /// <summary>A place in the view in points as a pixel of what's drawn into.</summary>
    internal Vector2 ToPixels(Vector2 points) => points * screen.Scale;

    /// <summary>The view changed size, or the screen turned. Main thread.</summary>
    internal void DisplayChanged(IosDisplayInfo now, bool resized)
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

    // The layer's own size when there is one, the last size known when it's gone, frames go on offscreen at that.
    // Touches are handed over in pixels already, so the window is counted in pixels too and nothing is scaled twice,
    // the way the Android host does it. The scale only goes in as the display's, for the UI's sizes
    private PlatformDisplay Measure()
    {
        SurfaceSize size = Surface.PixelSize;
        Vector2 pixels = size.IsEmpty ? display.ViewportSize : new Vector2(size.Width, size.Height);
        if (pixels.X <= 0.0f || pixels.Y <= 0.0f) pixels = screen.ViewPixels;
        return new PlatformDisplay(pixels, pixels, pixels, screen.ScreenPixels, Vector2.One, screen.Scale);
    }

    /// <summary>Last, on the main thread, after the engine stopped and its device let go of the layer.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        dispatcher.Close();
        input?.Close();
        if (Surface.Holding) Surface.Release();
        session.HostGone(this);
        Log.Info("[iOS] Host disposed.");
    }
}
