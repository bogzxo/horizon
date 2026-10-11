using Horizon.Core;

namespace Horizon.Engine;

/// <summary>
/// What a <see cref="GameEngine"/> is told to be when it is made, which so far is what its window is like.
/// </summary>
public readonly struct GameEngineConfiguration
{
    public readonly WindowManagerConfiguration WindowConfiguration { get; init; }

    public static GameEngineConfiguration Default { get; } =
        new GameEngineConfiguration
        {
            WindowConfiguration = WindowManagerConfiguration.Default1600x900
        };
}
