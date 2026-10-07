using Horizon.Core;
using Horizon.Core.Components;

namespace Horizon.Engine.Debugging.Debuggers;

public abstract class DebuggerComponent : GameComponent, IDisposable
{
    public bool Visible = false;

    public abstract void Dispose();
}