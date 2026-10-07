using System.Numerics;

using Horizon.Core.Components;
using Horizon.Rendering;

namespace Horizon.Tests;

public class TransformComponent2DTests
{
    // How the model matrix was worked out before it was written out by hand
    private static Matrix4x4 Expected(Vector2 position, float rotation, Vector2 size, float z, Vector2 origin) =>
        Matrix4x4.CreateTranslation(origin.X, origin.Y, 0f)
        * Matrix4x4.CreateScale(size.X, size.Y, 1.0f)
        * Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, float.DegreesToRadians(rotation)))
        * Matrix4x4.CreateTranslation(position.X, position.Y, z);

    [Theory]
    [InlineData(0.0f, 0.0f, 0.0f, 1.0f, 1.0f, Origin.Center)]
    [InlineData(120.0f, -40.0f, 30.0f, 64.0f, 32.0f, Origin.TopLeft)]
    [InlineData(-5.5f, 7.25f, 271.0f, -48.0f, 96.0f, Origin.BottomRight)]
    [InlineData(1000.0f, 1000.0f, -45.0f, 3.0f, -3.0f, Origin.Left)]
    public void The_model_matrix_is_what_it_always_was(float x, float y, float rotation, float width, float height, Origin origin)
    {
        var transform = new TransformComponent2D
        {
            Position = new Vector2(x, y),
            Rotation = rotation,
            Size = new Vector2(width, height),
            Origin = origin,
            ZOffset = 0.25f
        };

        Vector2 offset = origin switch
        {
            Origin.TopLeft => new Vector2(0.5f, -0.5f),
            Origin.BottomRight => new Vector2(-0.5f, 0.5f),
            Origin.Left => new Vector2(0.5f, 0.0f),
            _ => Vector2.Zero
        };

        Matrix4x4 expected = Expected(new Vector2(x, y), rotation, new Vector2(width, height), 0.25f, offset);
        Matrix4x4 actual = transform.ModelMatrix;

        for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
                Assert.Equal(expected[row, column], actual[row, column], 0.001f);
    }

    [Fact]
    public void Changing_the_depth_or_origin_changes_the_matrix()
    {
        var transform = new TransformComponent2D { Size = new Vector2(10.0f) };
        Matrix4x4 before = transform.ModelMatrix;

        transform.ZOffset = 3.0f;
        Assert.Equal(3.0f, transform.ModelMatrix.M43);

        transform.Origin = Origin.TopLeft;
        Assert.NotEqual(before.M41, transform.ModelMatrix.M41);
    }
}
