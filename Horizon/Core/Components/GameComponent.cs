namespace Horizon.Core.Components;

/// <summary>
/// Which per-tick lifecycle callbacks a <see cref="GameComponent"/> actually implements, so <see cref="Entity"/>
/// can skip empty <see cref="GameComponent.UpdateState"/>, <see cref="GameComponent.UpdatePhysics"/>, and
/// <see cref="GameComponent.Capture"/> walks without reflection.
/// </summary>
[Flags]
public enum ComponentCapabilities : byte
{
    None = 0,
    UpdateState = 1 << 0,
    UpdatePhysics = 1 << 1,
    Capture = 1 << 2,
    All = UpdateState | UpdatePhysics | Capture
}

/// <summary>
/// A component that does nothing until it is told what to do. It has the name, the parent and the switch every
/// component needs, and every one of its turns is empty, so whoever makes a component from this only writes the
/// turns it has something to do in.
/// <code><![CDATA[
/// public sealed class Spinner : GameComponent
/// {
///     public override void UpdateState(float dt) => Parent.GetComponent<TransformComponent2D>()!.Rotation += 90.0f * dt;
/// }
/// ]]></code>
/// <see cref="IGameComponent"/> is still there for whatever has to descend from something else.
/// </summary>
public abstract class GameComponent : IGameComponent, ISnapshotSource
{
    /// <summary>
    /// Which per-tick turns this component participates in. Defaults to <see cref="ComponentCapabilities.All"/>
    /// for backward compatibility with subclasses that do not override it; components that only need a subset
    /// (such as transforms or physics-only controllers) override this to let <see cref="Entity"/> skip empty walks.
    /// </summary>
    public virtual ComponentCapabilities Capabilities => ComponentCapabilities.All;
    /// <summary>
    /// Whether the component gets its turns. Off until it has been set up, and whenever somebody switches it off after.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// What the component is called in the log. The name of its class unless it says otherwise.
    /// </summary>
    public string Name
    {
        get => _name ??= GetType().Name;
        set => _name = value;
    }

    private string? _name;

    /// <summary>
    /// The entity the component was added to, which is there by the time it is set up.
    /// </summary>
    public Entity Parent { get; set; } = null!;

    /// <summary>
    /// Called once on the render thread before the first turn, this is where it makes what it needs on the GPU.
    /// </summary>
    public virtual void Initialize() { }

    public virtual void UpdateState(float dt) { }

    public virtual void UpdatePhysics(float dt) { }

    public virtual void Render(float dt) { }

    /// <summary>
    /// Publishes what the component draws, see <see cref="ISnapshotSource.Capture"/>. Nothing unless it draws from
    /// snapshots of its own. Simulation thread, at the end of every tick.
    /// </summary>
    public virtual void Capture() { }
}
