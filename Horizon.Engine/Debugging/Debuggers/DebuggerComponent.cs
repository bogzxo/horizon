using Horizon.Core;
using Horizon.Core.Components;

namespace Horizon.Engine.Debugging.Debuggers;

public abstract class DebuggerComponent : IGameComponent, IDisposable
{
    public string Name { get; set; } = "Generic Debugger Componenet";
    public Entity Parent { get; set; }
    public bool Enabled { get; set; }

    public bool Visible = false;

    public abstract void Initialize();

    public abstract void UpdateState(float dt);

    public abstract void UpdatePhysics(float dt);

    public virtual void Render(float dt, object? obj = null) { }

    public virtual void RenderUi(Egui.Ui root) { }

    public abstract void Dispose();
}