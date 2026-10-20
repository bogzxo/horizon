using System.Numerics;

using Horizon.Core.Threading;

namespace Horizon.Core;

/// <summary>
/// Configuration for <see cref="WindowManager"/>.
/// </summary>
/// <summary>What a window is run for, see <see cref="WindowManagerConfiguration.Mode"/>.</summary>
public enum RunMode
{
    // Every frame the screen shows, every tick at the rate, whatever is going on
    Game,

    // An editor or a tool, as little as it can get away with while nothing is happening, see ApplicationBehaviour
    Application
}

/// <summary>
/// The ways a window can save itself work while nothing is happening, which an application wants and a game doesn't.
/// </summary>
[Flags]
public enum ApplicationBehaviour
{
    None = 0,

    /// <summary>
    /// A frame is drawn only when something wants one, a UI that painted something new (and the frames that blend it in),
    /// the window resized, exclusive work, the mouse with a cursor of the engine's. Otherwise the thread that draws
    /// sleeps, with a frame a second so nothing can be left stale for long.
    /// </summary>
    DrawOnChange = 1,

    /// <summary>
    /// The simulation ticks a few times a second while there has been no input and no UI has changed for a moment,
    /// each of those ticks as long as the time that went by. Input wakes it at once.
    /// </summary>
    SlowWhenIdle = 2,

    /// <summary>The window's thread waits for the system to have something to say rather than asking a thousand times a second.</summary>
    WaitForEvents = 4,

    All = DrawOnChange | SlowWhenIdle | WaitForEvents
}

public readonly struct WindowManagerConfiguration
{
    public readonly Vector2 WindowSize { get; init; }
    public readonly string WindowTitle { get; init; }
    public readonly bool Fullscreen { get; init; }

    /// <summary>
    /// Whether the window starts out filling the screen it is on, for tools that want all the room there is.
    /// <see cref="WindowSize"/> is then what it goes back to when it is restored.
    /// </summary>
    public readonly bool Maximized { get; init; }

    /// <summary>
    /// Whether the window is made without the system's frame, no title bar and no border, so the UI can draw its own
    /// (<c>TitleBar</c>, with the window's buttons and dragged to move it) the way VS Code does. The edges are dragged by
    /// a compositor that says <c>ResizesWindow</c>.
    /// </summary>
    public readonly bool CustomFrame { get; init; }

    /// <summary>
    /// A picture for the window's corner and the taskbar, a .png (anything ImageSharp reads, which isn't an .ico), from
    /// the exe's folder unless the path says where. Null for the system's own. A Mac takes its Dock icon from the app
    /// bundle and ignores this.
    /// </summary>
    public readonly string? WindowIcon { get; init; }

    /// <summary>
    /// What the desktop knows the program's windows by on Linux, its app id on Wayland and its class on X11, which is
    /// the name of its .desktop file (hex-dawn for hex-dawn.desktop). A Wayland window has no icon of its own, the
    /// desktop shows the one its .desktop file names, found by this. Null for the program's name.
    /// </summary>
    public readonly string? AppId { get; init; }

    /// <summary>
    /// What the window is run for. A game (the default) draws every frame the screen shows and ticks at its rate
    /// whatever is going on. An application (an editor, a tool) does what <see cref="Behaviour"/> says, which unless it
    /// says otherwise is all of what makes it cost next to nothing while nobody is using it.
    /// </summary>
    public readonly RunMode Mode { get; init; } = RunMode.Game;

    /// <summary>
    /// Which of the ways of being an application are on, null for what the <see cref="Mode"/> comes with (all of them
    /// for an application, none for a game). For a game that wants one of them, or a tool that wants one off.
    /// </summary>
    public readonly ApplicationBehaviour? Behaviour { get; init; }

    /// <summary>
    /// Whether frames wait for the screen to be ready for them, so none is shown torn across two and no more are
    /// drawn than can be seen. On unless it is switched off.
    /// </summary>
    public readonly bool VSync { get; init; } = true;

    /// <summary>
    /// The most frames that are drawn a second, 0 for no limit (which with <see cref="VSync"/> on is as many as the screen shows).
    /// </summary>
    public readonly double FramesPerSecond { get; init; } = 0.0;

    /// <summary>How many times a second the state of the game is updated, on the simulation thread.</summary>
    public readonly double UpdatesPerSecond { get; init; } = 120.0;

    /// <summary>
    /// How many times a second the physics is stepped, on the simulation thread along with the updates. Every step is
    /// told the same length of time (one over this), whatever the machine is busy with.
    /// </summary>
    public readonly double PhysicsUpdatesPerSecond { get; init; } = 120.0;

    /// <summary>
    /// How frames show the simulation, see <see cref="PresentationMode"/>. Interpolated
    /// unless said otherwise, smooth at any frame rate, at most a tick behind.
    /// </summary>
    public readonly PresentationMode Presentation { get; init; } = PresentationMode.Interpolated;

    // What isn't said is as above, which takes a constructor to hold for a struct
    public WindowManagerConfiguration() { }

    public static WindowManagerConfiguration Default1600x900 { get; } =
        new WindowManagerConfiguration
        {
            WindowSize = new Vector2(1600, 900),
            WindowTitle = "Horizon Engine"
        };
}