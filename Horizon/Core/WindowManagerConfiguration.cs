using System.Numerics;

using Horizon.Core.Threading;

namespace Horizon.Core;

/// <summary>
/// Configuration for <see cref="WindowManager"/>.
/// </summary>
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