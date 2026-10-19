using System.Numerics;

namespace Horizon.Core;

/// <summary>
/// How the window is shown and how often it is drawn, which is everything about it that can be changed while the game runs.
/// Hand one to <see cref="WindowManager.Apply"/>, from an options screen say.
/// <code>
/// engine.WindowManager.Apply(engine.WindowManager.Display with { Fullscreen = true });
/// </code>
/// </summary>
public readonly record struct DisplaySettings
{
    /// <summary>Whether the window takes up the whole screen it is on, at the resolution that screen is set to.</summary>
    public bool Fullscreen { get; init; }

    /// <summary>The size of the window for as long as it isn't fullscreen, in pixels.</summary>
    public Vector2 WindowSize { get; init; }

    /// <summary>Whether frames wait for the screen to be ready for them, see <see cref="WindowManagerConfiguration.VSync"/>.</summary>
    public bool VSync { get; init; }

    /// <summary>The most frames that are drawn a second, 0 for no limit.</summary>
    public double FramesPerSecond { get; init; }
}
