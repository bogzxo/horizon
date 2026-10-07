namespace Horizon.Core.Components;

/// <summary>
/// Aggregate of more specific engine events.
/// </summary>
public class EngineEventHandler : GameComponent
{
    public Action<float>? PreState;
    public Action<float>? PostState;

    public Action<float>? PrePhysics;
    public Action<float>? PostPhysics;

    public Action<float>? PreRender;
    public Action<float>? PostRender;
}