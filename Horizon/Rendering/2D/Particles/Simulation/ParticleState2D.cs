using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Horizon.Rendering.Particles.Simulation;

/// <summary>
/// The simulated state of one particle. 24 bytes, laid out exactly like the std430
/// <c>Particle</c> struct in shaders/particle/simulate.slang.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct ParticleState2D
{
    public Vector2 Position;
    public Vector2 Velocity;

    /// <summary>1 at birth, falling to 0 at death. This is the value the shaders call "alive".</summary>
    public float Life;

    /// <summary>Per-particle ageing multiplier, so particles spawned together don't all die together.</summary>
    public float Rate;

    public static readonly uint SizeInBytes = (uint)Unsafe.SizeOf<ParticleState2D>();
}
