using System.Numerics;
using System.Runtime.InteropServices;

using Horizon.Graphics;

namespace Horizon.Rendering.Meshes;

/// <summary>
/// A vertex of a <see cref="Mesh3D"/>. Where it is, which way its surface faces (a unit vector, for the lighting),
/// where on the texture it sits and a colour of its own, which is multiplied into whatever else the mesh is
/// painted with (white for none). 48 bytes, laid out for the GPU by <see cref="GetLayout"/>, see mesh.slang.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct Vertex3D : IVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 TexCoords;
    public Vector4 Color;

    private static readonly VertexLayoutDescription[] Layout =
    [
        VertexLayoutDescription.Float(0, 3, 0),
        VertexLayoutDescription.Float(1, 3, sizeof(float) * 3),
        VertexLayoutDescription.Float(2, 2, sizeof(float) * 6),
        VertexLayoutDescription.Float(3, 4, sizeof(float) * 8),
    ];

    public static ReadOnlySpan<VertexLayoutDescription> GetLayout() => Layout;

    public Vertex3D(Vector3 position, Vector3 normal, Vector2 texCoords)
        : this(position, normal, texCoords, Vector4.One) { }

    public Vertex3D(Vector3 position, Vector3 normal, Vector2 texCoords, Vector4 color)
    {
        Position = position;
        Normal = normal;
        TexCoords = texCoords;
        Color = color;
    }
}
