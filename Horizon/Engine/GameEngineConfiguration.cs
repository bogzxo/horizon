using Horizon.Core;

namespace Horizon.Engine;

/// <summary>
/// What a <see cref="GameEngine"/> is told to be when it is made, what its window is like and what it listens to.
/// </summary>
public readonly struct GameEngineConfiguration
{
    public GameEngineConfiguration() { }

    public readonly WindowManagerConfiguration WindowConfiguration { get; init; }

    /// <summary>
    /// Whether the engine listens for gamepads plugged into the machine. An editor or a terminal has no use for them and
    /// turns this off, <c>Engine.Input.Gamepads</c> is still there but nothing is ever plugged into it. A keyboard read as
    /// a gamepad, a virtual one and the one of an input script still work, those aren't devices.
    /// </summary>
    public bool Gamepads { get; init; } = true;

    public static GameEngineConfiguration Default { get; } =
        new GameEngineConfiguration
        {
            WindowConfiguration = WindowManagerConfiguration.Default1600x900
        };
}
