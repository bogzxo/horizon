using System.Numerics;

using Horizon.Core;

namespace Horizon.Platform;

/// <summary>
/// What runs the engine on a platform, the native window or view, its surface, its input and its speakers, and the
/// thread all of that belongs to. One per engine, made fresh for it by an <see cref="IPlatform"/> and disposed by the
/// window manager once the GPU has let go of the surface. No native types in here.
/// <para>
/// The order is <see cref="Attach"/> as the window manager is made, <see cref="Initialize"/> on the platform's thread
/// as it starts, which is when <see cref="Surface"/>, <see cref="Input"/> and <see cref="State"/> are there, and
/// <see cref="Dispose"/> last, on the platform's thread, after the engine stopped. Nothing here blocks the platform's
/// thread waiting for the engine. A desktop also has <see cref="IPlatformLoop"/>, the loop it pumps until its window
/// closes. A host that is called back by its system (an Android activity) has none, it starts the engine and returns.
/// </para>
/// </summary>
public interface IPlatformHost : IDisposable
{
    /// <summary>What there is beyond a surface, see <see cref="PlatformFeatures"/>.</summary>
    PlatformFeatures Features { get; }

    /// <summary>The desktop window, null where there is none.</summary>
    IPlatformWindow? Window { get; }

    /// <summary>What is known about surfaces before there is one, so the GPU can be made early. Null where nothing is.</summary>
    IPlatformSurfaceAhead? SurfaceAhead { get; }

    /// <summary>What frames are shown in. There from <see cref="Initialize"/> on.</summary>
    IPlatformSurface Surface { get; }

    /// <summary>The keys, the pointer and the text. There from <see cref="Initialize"/> on.</summary>
    IPlatformInput Input { get; }

    /// <summary>What the window or view was like as it came up. There from <see cref="Initialize"/> on, and never changes.</summary>
    PlatformState State { get; }

    /// <summary>Makes the platform's speakers, null for none. Whoever is handed it owns it and disposes it.</summary>
    IAudioOutput? CreateAudioOutput();

    /// <summary>Tells the host who hears what it has to say. Once, before <see cref="Initialize"/>.</summary>
    void Attach(IPlatformEvents events);

    /// <summary>
    /// Gets the native window or view going and makes this thread the platform's. Once, on the thread that is to be
    /// the platform's, before any frame.
    /// </summary>
    void Initialize();

    /// <summary>Whether the calling thread is the platform's.</summary>
    bool IsPlatformThread { get; }

    /// <summary>Has something done on the platform's thread when it next gets round to it. From any thread.</summary>
    void Post(Action work);

    /// <summary>
    /// Has something done on the platform's thread and waits for it, done right away when called on that thread. From any
    /// thread but never while the platform's thread waits on the caller.
    /// </summary>
    void Invoke(Action work);

    /// <summary>Wakes the platform's thread if it's waiting for the system. From any thread.</summary>
    void Wake();

    /// <summary>Asks the window or view to close, which stops the engine once the platform has its say. From any thread.</summary>
    void RequestClose();
}

/// <summary>A host with a loop of its own that the program hands its thread to, which is a desktop's.</summary>
public interface IPlatformLoop
{
    /// <summary>Hears the system and does posted work until the window closes. The platform's thread.</summary>
    void Run();

    /// <summary>Goes on doing posted work, and nothing else, until a task is done. The platform's thread, after <see cref="Run"/>.</summary>
    void PumpUntil(Task done);

    /// <summary>One last listen to the system once everything stopped, so the window hears it's closed.</summary>
    void Finish();
}

/// <summary>
/// What the engine is told by the host, on the platform's thread unless it says otherwise. Implemented by the window
/// manager, a host only calls it.
/// </summary>
public interface IPlatformEvents
{
    /// <summary>The size of what is drawn into, the window, the screen or the scale changed. Resized when the surface did.</summary>
    void DisplayChanged(PlatformDisplay display, bool resized);

    /// <summary>The window got the keyboard or lost it.</summary>
    void FocusChanged(bool focused);

    /// <summary>The window went down to the taskbar or came back.</summary>
    void MinimizedChanged(bool minimized);

    /// <summary>Input of any kind came in, which wakes an application that went quiet.</summary>
    void InputHeard();

    /// <summary>Everything the system had to say was heard and handed on. The moment for the clipboard.</summary>
    void EventsProcessed();

    /// <summary>The engine wants frames drawn for a moment (the window changed under it).</summary>
    void RequestFrames();

    /// <summary>The engine is woken for work that finished somewhere else (a key heard everywhere).</summary>
    void Wake();

    /// <summary>Somebody asked the window to close (its X), false keeps it open.</summary>
    bool MayClose();

    /// <summary>Whether nothing is going on in the engine, so the platform's thread may wait for the system. From the platform's thread.</summary>
    bool MayWaitForEvents { get; }
}

/// <summary>
/// How big things are, all of it as of one moment.
/// </summary>
/// <param name="ViewportSize">What is drawn into, in pixels.</param>
/// <param name="WindowSize">The window in pixels, which is what is drawn into everywhere but a stretched Mac fullscreen.</param>
/// <param name="PointSize">The window in whatever the system counts it and the pointer in, points on a Mac.</param>
/// <param name="ScreenSize">The screen the window is on, in the same pixels as the viewport.</param>
/// <param name="PixelScale">Pixels drawn into to one of the system's units across and down.</param>
/// <param name="DisplayScale">Pixels to one of the system's units at 100% on the screen the window is on, 1.5 at 150%.</param>
public sealed record PlatformDisplay(Vector2 ViewportSize, Vector2 WindowSize, Vector2 PointSize, Vector2 ScreenSize, Vector2 PixelScale, float DisplayScale)
{
    /// <summary>A display of a size and nothing scaled, for a host that has nothing better to say yet.</summary>
    public static PlatformDisplay Of(Vector2 size) => new(size, size, size, size, Vector2.One, 1.0f);
}

/// <summary>What the window or view was like as it came up.</summary>
public sealed record PlatformState(PlatformDisplay Display, bool Focused, bool Minimized);

/// <summary>What the engine asks a platform for its host.</summary>
/// <param name="Window">How the window is to be.</param>
/// <param name="Behaviour">What saves work while nothing happens, which decides how the platform's thread waits.</param>
/// <param name="ReadsGamepads">Whether controllers are read at all, off and the system isn't even asked about one.</param>
public sealed record PlatformHostRequest(WindowManagerConfiguration Window, ApplicationBehaviour Behaviour, bool ReadsGamepads);
