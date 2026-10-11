namespace Horizon.Core.Components;

/// <summary>
/// A component that publishes what it draws at the end of every tick, so it can be drawn from that while the game goes
/// on being simulated (see <see cref="Threading.SnapshotClock"/>). Entities don't need this, they have
/// <see cref="Entity.Capture"/>, and a <see cref="GameComponent"/> has <see cref="GameComponent.Capture"/>.
/// </summary>
public interface ISnapshotSource
{
    /// <summary>
    /// Publishes what the component is to be drawn as into the capture that is going on. Simulation thread, at the end
    /// of every tick, only while the component is switched on (one that isn't published isn't drawn).
    /// </summary>
    void Capture();
}
