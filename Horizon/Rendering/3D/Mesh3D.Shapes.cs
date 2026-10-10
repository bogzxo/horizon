using System.Numerics;

namespace Horizon.Rendering.Meshes;

// The shapes a mesh can be made as without anybody writing a vertex. Every one is built with its triangles going
// round anticlockwise as seen from outside, so it culls its backs properly, and its normals pointing out.
public partial class Mesh3D
{
    /// <summary>A box, so big a side, around its middle. Six faces with their own normals, so it has edges.</summary>
    public static Mesh3D Cube(float size = 1.0f) => Box(new Vector3(size));

    /// <summary>A box this big each way, around its middle.</summary>
    public static Mesh3D Box(Vector3 size)
    {
        Vector3 h = size * 0.5f;
        var vertices = new List<Vertex3D>(24);
        var indices = new List<uint>(36);

        // A face is its normal, the way across it and the way up it. The corners go round anticlockwise seen from
        // the normal's side, which is how the whole engine tells a front from a back
        void Face(Vector3 normal, Vector3 across, Vector3 up)
        {
            uint first = (uint)vertices.Count;
            Vector3 middle = normal * h;
            Vector3 a = across * h, u = up * h;

            vertices.Add(new Vertex3D(middle - a - u, normal, new Vector2(0.0f, 1.0f)));
            vertices.Add(new Vertex3D(middle + a - u, normal, new Vector2(1.0f, 1.0f)));
            vertices.Add(new Vertex3D(middle + a + u, normal, new Vector2(1.0f, 0.0f)));
            vertices.Add(new Vertex3D(middle - a + u, normal, new Vector2(0.0f, 0.0f)));

            indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        }

        Face(Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY);
        Face(-Vector3.UnitZ, -Vector3.UnitX, Vector3.UnitY);
        Face(Vector3.UnitX, -Vector3.UnitZ, Vector3.UnitY);
        Face(-Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY);
        Face(Vector3.UnitY, Vector3.UnitX, -Vector3.UnitZ);
        Face(-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ);

        return Built(vertices, indices);
    }

    /// <summary>A flat sheet in the XZ plane facing up, so big, with its texture laid over it so many times.</summary>
    public static Mesh3D Plane(float width, float depth, float repeats = 1.0f)
    {
        Vector3 a = new(width * 0.5f, 0.0f, 0.0f), d = new(0.0f, 0.0f, depth * 0.5f);
        Vertex3D[] vertices =
        [
            new(-a + d, Vector3.UnitY, new Vector2(0.0f, repeats)),
            new(a + d, Vector3.UnitY, new Vector2(repeats, repeats)),
            new(a - d, Vector3.UnitY, new Vector2(repeats, 0.0f)),
            new(-a - d, Vector3.UnitY, new Vector2(0.0f, 0.0f)),
        ];

        return Built(vertices, [0, 1, 2, 0, 2, 3]);
    }

    /// <summary>A ball of a radius, so many segments round and so many from pole to pole. 24 and 16 is smooth enough for most things.</summary>
    public static Mesh3D Sphere(float radius = 0.5f, int segments = 24, int rings = 16)
    {
        segments = Math.Max(3, segments);
        rings = Math.Max(2, rings);

        var vertices = new List<Vertex3D>((segments + 1) * (rings + 1));
        var indices = new List<uint>(segments * rings * 6);

        for (int ring = 0; ring <= rings; ring++)
        {
            float v = ring / (float)rings;
            float polar = v * MathF.PI;
            float y = MathF.Cos(polar), r = MathF.Sin(polar);

            for (int segment = 0; segment <= segments; segment++)
            {
                float u = segment / (float)segments;
                float around = u * MathF.Tau;
                var normal = new Vector3(r * MathF.Cos(around), y, r * MathF.Sin(around));
                vertices.Add(new Vertex3D(normal * radius, normal, new Vector2(u, v)));
            }
        }

        for (int ring = 0; ring < rings; ring++)
        {
            for (int segment = 0; segment < segments; segment++)
            {
                uint a = (uint)(ring * (segments + 1) + segment);
                uint b = a + (uint)segments + 1;

                // Round anticlockwise seen from outside, with the seam on the far side so the texture runs
                indices.AddRange([a, a + 1, b, a + 1, b + 1, b]);
            }
        }

        return Built(vertices, indices);
    }

    /// <summary>A can standing up, of a radius and a height around its middle, so many segments round.</summary>
    public static Mesh3D Cylinder(float radius = 0.5f, float height = 1.0f, int segments = 24)
    {
        segments = Math.Max(3, segments);
        float h = height * 0.5f;

        var vertices = new List<Vertex3D>();
        var indices = new List<uint>();

        // The side, a ring of quads with the normals pointing out
        for (int segment = 0; segment <= segments; segment++)
        {
            float u = segment / (float)segments;
            float around = u * MathF.Tau;
            var normal = new Vector3(MathF.Cos(around), 0.0f, MathF.Sin(around));
            vertices.Add(new Vertex3D(normal * radius + new Vector3(0.0f, h, 0.0f), normal, new Vector2(u, 0.0f)));
            vertices.Add(new Vertex3D(normal * radius - new Vector3(0.0f, h, 0.0f), normal, new Vector2(u, 1.0f)));
        }

        for (int segment = 0; segment < segments; segment++)
        {
            uint top = (uint)segment * 2, bottom = top + 1;
            indices.AddRange([top, top + 2, bottom, top + 2, bottom + 2, bottom]);
        }

        // The two lids, a fan each from a vertex in the middle
        for (int side = 1; side >= -1; side -= 2)
        {
            Vector3 normal = Vector3.UnitY * side;
            uint middle = (uint)vertices.Count;
            vertices.Add(new Vertex3D(normal * h, normal, new Vector2(0.5f, 0.5f)));

            for (int segment = 0; segment <= segments; segment++)
            {
                float around = segment / (float)segments * MathF.Tau;
                var rim = new Vector3(MathF.Cos(around), 0.0f, MathF.Sin(around));
                vertices.Add(new Vertex3D(rim * radius + normal * h, normal, new Vector2(0.5f + rim.X * 0.5f, 0.5f + rim.Z * 0.5f)));
            }

            for (uint segment = 0; segment < (uint)segments; segment++)
            {
                uint a = middle + 1 + segment, b = a + 1;
                if (side > 0) indices.AddRange([middle, b, a]);
                else indices.AddRange([middle, a, b]);
            }
        }

        return Built(vertices, indices);
    }

    /// <summary>
    /// Works out a normal for every vertex from the triangles that share it, for a mesh you built the positions of
    /// and can't be bothered with the normals. Flat where the triangles are flat, smooth where they share vertices.
    /// </summary>
    public static void ComputeNormals(Span<Vertex3D> vertices, ReadOnlySpan<uint> indices)
    {
        for (int i = 0; i < vertices.Length; i++) vertices[i].Normal = Vector3.Zero;

        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            ref Vertex3D a = ref vertices[(int)indices[i]];
            ref Vertex3D b = ref vertices[(int)indices[i + 1]];
            ref Vertex3D c = ref vertices[(int)indices[i + 2]];

            // Anticlockwise round, so the cross product points out of the front
            Vector3 normal = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
            a.Normal += normal;
            b.Normal += normal;
            c.Normal += normal;
        }

        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i].Normal = vertices[i].Normal.LengthSquared() > 1e-12f ? Vector3.Normalize(vertices[i].Normal) : Vector3.UnitY;
        }
    }

    private static Mesh3D Built(List<Vertex3D> vertices, List<uint> indices)
    {
        var mesh = new Mesh3D();
        mesh.Upload(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(vertices), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(indices));
        return mesh;
    }

    private static Mesh3D Built(Vertex3D[] vertices, uint[] indices)
    {
        var mesh = new Mesh3D();
        mesh.Upload(vertices, indices);
        return mesh;
    }
}
