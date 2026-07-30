using Horizon.Core;
using Horizon.Core.Components;

namespace Horizon.Engine.Debugging.Debuggers;

public abstract class DebuggerComponent : IGameComponent, IDisposable
{
    public string Name { get; set; }
    public Entity Parent { get; set; }
    public bool Enabled { get; set; }

    public bool Visible = false;

    public abstract void Initialize();

    public abstract void UpdateState(float dt);

    public abstract void UpdatePhysics(float dt);

    public abstract void Dispose();

    public abstract void Render(float dt, object? obj = null);
}