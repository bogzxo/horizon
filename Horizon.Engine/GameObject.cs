using Horizon.Core;

namespace Horizon.Engine;

/// <summary>
/// An entity that knows the engine it lives in. Scenes, cameras, renderers and most things a game adds to a scene are these.
/// </summary>
public abstract class GameObject : Entity
{
    public static GameEngine Engine { get; internal set; } = null!;
}
