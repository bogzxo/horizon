namespace Horizon.Rendering.Particles.Simulation;

/// <summary>
/// A contiguous run of instances inside a simulator's instance buffer.
/// </summary>
public readonly record struct ParticleRange(uint First, uint Count);
