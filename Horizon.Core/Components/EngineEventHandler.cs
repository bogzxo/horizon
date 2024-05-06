namespace Horizon.Core.Components;

/// <summary>
/// Aggregate of more specific engine events.
/// </summary>
public class EngineEventHandler : IGameComponent
{
    public Action<float>? PreState;
    public Action<float>? PostState;

    public Action<float>? PrePhysics;
    public Action<float>? PostPhysics;

    public Action<float>? PreRender;
    public Action<float>? PostRender;

    public bool Enabled { get; set; }
    public string Name { get; set; }
    public Entity Parent { get; set; }

    public void Initialize()
    { }

    public void Render(float dt, object? obj = null)
    { }

    public void UpdatePhysics(float dt)
    { }

    public void UpdateState(float dt)
    { }
}