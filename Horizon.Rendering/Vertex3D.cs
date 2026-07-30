using System.Numerics;
using System.Runtime.InteropServices;

using Horizon.OpenGL;

using Silk.NET.OpenGL;

namespace Horizon.Rendering;

[StructLayout(LayoutKind.Sequential)] // explicitly set sequential layout
public struct Vertex3D : IVertex
{
    private Vector3 position; // 12 bytes

    private Vector3 normal; // 12 bytes

    private Vector2 texCoord; // 8 bytes
    public static ReadOnlySpan<VertexLayoutDescription> GetLayout() => new VertexLayoutDescription[]
    {
        new() {
            Index = 0,
            Size = sizeof(float) * 3,
            Count = 3,
            Offset = 0,
            Type = VertexAttribPointerType.Float,
            Instanced = false
        },
        new() {
            Index = 1,
            Size = sizeof(float) * 3,
            Count = 3,
            Offset = sizeof(float) * 3,
            Type = VertexAttribPointerType.Float,
            Instanced = false
        },
        new() {
            Index = 2,
            Size = sizeof(float) * 2,
            Count = 2,
            Offset = sizeof(float) * 6,
            Type = VertexAttribPointerType.Float,
            Instanced = false
        }
    };

    public Vertex3D(Vector3 position, Vector3 normal, Vector2 texCoord)
    {
        this.position = position;
        this.normal = normal;
        this.texCoord = texCoord;
    }

    public Vertex3D(
        float x,
        float y,
        float z,
        float nX = 0,
        float nY = 0,
        float nZ = 0,
        float tX = 0,
        float tY = 0
    )
    {
        this.position = new Vector3(x, y, z);
        this.normal = new Vector3(nX, nY, nZ);
        this.texCoord = new Vector2(tX, tY);
    }

    public Vector3 Position
    {
        readonly get => position;
        set => position = value;
    }

    public Vector3 Normal
    {
        readonly get => normal;
        set => normal = value;
    }

    public Vector2 TexCoord
    {
        readonly get => texCoord;
        set => texCoord = value;
    }
}