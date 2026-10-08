using System.Numerics;

using Horizon.OpenGL;

namespace Horizon.Rendering.Particles;

/// <summary>A corner of the quad every particle is drawn as.</summary>
public readonly record struct ParticleVertex(Vector2 Position) : IVertex
{
    public static uint SizeInBytes { get; } = sizeof(float) * 2;

    private static readonly VertexLayoutDescription[] Layout = [VertexLayoutDescription.Float(0, 2, 0)];

    public static ReadOnlySpan<VertexLayoutDescription> GetLayout() => Layout;
}