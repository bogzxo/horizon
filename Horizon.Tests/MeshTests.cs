using System.Numerics;

using Horizon.Core.Components;
using Horizon.Engine;
using Horizon.Rendering.Meshes;

namespace Horizon.Tests;

public class MeshTests
{
    [Theory]
    [InlineData("cube")]
    [InlineData("sphere")]
    [InlineData("cylinder")]
    public void Every_face_of_a_closed_shape_goes_round_anticlockwise_seen_from_outside(string shape)
    {
        var (vertices, indices) = Built(shape);

        // A triangle going round anticlockwise seen from outside has its cross product pointing out, away from the
        // middle of the shape, and its normals agree with it
        for (int i = 0; i < indices.Length; i += 3)
        {
            Vertex3D a = vertices[indices[i]], b = vertices[indices[i + 1]], c = vertices[indices[i + 2]];
            Vector3 facing = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
            Vector3 centre = (a.Position + b.Position + c.Position) / 3.0f;

            Assert.True(Vector3.Dot(facing, centre) > 0.0f, $"triangle {i / 3} of the {shape} goes round the wrong way");
            Assert.True(Vector3.Dot(facing, a.Normal + b.Normal + c.Normal) > 0.0f, $"triangle {i / 3} of the {shape} has its normals facing in");
        }
    }

    [Fact]
    public void A_cube_has_six_faces_and_a_sphere_closes_up()
    {
        var (vertices, indices) = Built("cube");
        Assert.Equal(24, vertices.Length);
        Assert.Equal(12, indices.Length / 3);

        var (ball, ballIndices) = Built("sphere");
        Assert.Equal(24 * 16 * 2, ballIndices.Length / 3);
        Assert.All(ball, vertex => Assert.Equal(0.5f, vertex.Position.Length(), 4));
    }

    [Fact]
    public void Normals_worked_out_from_the_triangles_face_the_way_they_go_round()
    {
        // A flat quad in the XZ plane going round anticlockwise seen from above
        Vertex3D[] vertices =
        [
            new(new Vector3(-1, 0, 1), Vector3.Zero, Vector2.Zero),
            new(new Vector3(1, 0, 1), Vector3.Zero, Vector2.Zero),
            new(new Vector3(1, 0, -1), Vector3.Zero, Vector2.Zero),
            new(new Vector3(-1, 0, -1), Vector3.Zero, Vector2.Zero),
        ];

        Mesh3D.ComputeNormals(vertices, [0, 1, 2, 0, 2, 3]);
        Assert.All(vertices, vertex => Assert.Equal(Vector3.UnitY, vertex.Normal));
    }

    [Fact]
    public void A_transform_is_scaled_then_turned_then_moved()
    {
        var transform = new TransformComponent3D
        {
            Scale = new Vector3(2.0f),
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2.0f),
            Position = new Vector3(10.0f, 0.0f, 0.0f)
        };

        // A point a unit along X is doubled, turned a quarter round Y (to -Z) and moved ten along X
        Vector3 moved = Vector3.Transform(Vector3.UnitX, transform.Model);
        Assert.Equal(10.0f, moved.X, 4);
        Assert.Equal(0.0f, moved.Y, 4);
        Assert.Equal(-2.0f, moved.Z, 4);

        int epoch = transform.Epoch;
        transform.Position = Vector3.Zero;
        Assert.Equal(epoch + 1, transform.Epoch);
    }

    [Fact]
    public void Looking_at_something_faces_it()
    {
        var transform = new TransformComponent3D { Position = new Vector3(0.0f, 0.0f, 5.0f) };
        transform.LookAt(Vector3.Zero);

        Assert.Equal(-1.0f, transform.Forward.Z, 4);
        Assert.Equal(0.0f, transform.Forward.X, 4);
    }

    [Fact]
    public void A_camera_looks_where_it_is_told_and_its_lens_keeps_a_cube_a_cube()
    {
        var camera = new Camera3D(2.0f) { Position = new Vector3(0.0f, 0.0f, 10.0f) };
        camera.LookAt(new Vector3(0.0f, 0.0f, 0.0f));
        Assert.Equal(-Vector3.UnitZ, camera.Look);

        // Twice as wide as tall, so a unit across takes half the screen a unit up does
        Assert.Equal(camera.Projection.M22 / 2.0f, camera.Projection.M11, 4);

        // Orthographic, the picture is as tall as it is told
        camera.OrthographicSize = 4.0f;
        Assert.Equal(0.5f, camera.Projection.M22, 4);
    }

    private static (Vertex3D[] Vertices, uint[] Indices) Built(string shape)
    {
        Mesh3D mesh = shape switch
        {
            "cube" => Mesh3D.Cube(1.0f),
            "sphere" => Mesh3D.Sphere(),
            _ => Mesh3D.Cylinder()
        };

        Assert.NotNull(mesh.Pending);
        return mesh.Pending.Value;
    }
}
