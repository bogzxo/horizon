using Horizon.Core;
using Horizon.Core.Components;

namespace Horizon.Webhost;

public class WebhostComponent : IGameComponent
{
    public bool Enabled { get; set; }
    public string Name { get; set; } = "Webost Component";
    public Entity Parent { get; set; }

    public void Initialize()
    {
        
    }

    public void Render(float dt, object? obj = null)
    {
        
    }

    public void UpdatePhysics(float dt)
    {
        
    }

    public void UpdateState(float dt)
    {
        
    }
}
