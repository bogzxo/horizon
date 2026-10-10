using Horizon.Core.Primitives;

namespace Horizon.Core.Components;

/// <summary>
/// What an entity is given to do its work for it, something with a name, a switch and the usual turns.
/// </summary>
public interface IGameComponent : IRenderable, IUpdateable, IInstantiable
{
    /// <summary>
    /// Whether the component gets its turns.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// What the component is called, for whoever reads the log.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// The entity the component was added to.
    /// </summary>
    public Entity Parent { get; set; }
}