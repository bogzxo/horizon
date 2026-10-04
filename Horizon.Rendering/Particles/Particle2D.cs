using System.Numerics;

namespace Horizon.Rendering.Particles;

/// <summary>
/// Describes a particle to spawn. Each particle travels at 50-100% of <paramref name="Speed"/>
/// (picked at random when it spawns), and the faster ones also burn out sooner.
/// </summary>
public record struct Particle2D(Vector2 Direction, Vector2 InitialPosition, float Speed = 32);
