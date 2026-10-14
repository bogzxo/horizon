using System.Numerics;

namespace Horizon.Rendering.Particles;

/// <summary>
/// Describes a particle to spawn. Each particle travels at 50-100% of <paramref name="Speed"/>
/// (picked at random when it spawns), and the faster ones also burn out sooner.
/// </summary>
/// <param name="Steady">
/// Has the particle set off at exactly <paramref name="Direction"/> times <paramref name="Speed"/> instead, for
/// what has to go as fast as it is told (rain, which is at its speed long before it is seen). It still ages at
/// a rate of its own.
/// </param>
public record struct Particle2D(Vector2 Direction, Vector2 InitialPosition, float Speed = 32, bool Steady = false);
