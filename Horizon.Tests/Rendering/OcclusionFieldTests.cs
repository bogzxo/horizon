using System.Numerics;

using Horizon.Rendering.Lighting;

namespace Horizon.Tests;

public class OcclusionFieldTests
{
    // What sdf_rows.slang and sdf_columns.slang do, by brute force the way they do it, to hold the CPU's field up to
    private static float[] TheShadersField(OcclusionMap2D map, Func<int, int, bool> solidAt)
    {
        int width = (int)map.FieldSize.X, height = (int)map.FieldSize.Y;
        var rows = new (float Solid, float Open)[width * height];

        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            float toSolid = 1e6f, toOpen = 1e6f;
            for (int step = 0; step < width; step++)
            {
                int left = x - step, right = x + step;
                bool any = false;
                if (left >= 0) { any = true; if (solidAt(left, y)) toSolid = MathF.Min(toSolid, step); else toOpen = MathF.Min(toOpen, step); }
                if (right < width) { any = true; if (solidAt(right, y)) toSolid = MathF.Min(toSolid, step); else toOpen = MathF.Min(toOpen, step); }
                if (!any || (toSolid < 1e5f && toOpen < 1e5f)) break;
            }

            rows[x + y * width] = (toSolid, toOpen);
        }

        float texelWorld = MathF.Min(map.FieldTexelSize.X, map.FieldTexelSize.Y);
        var field = new float[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            float bestSolid = 1e12f, bestOpen = 1e12f;
            for (int row = 0; row < height; row++)
            {
                var (solid, open) = rows[x + row * width];
                float dy = row - y;
                bestSolid = MathF.Min(bestSolid, solid * solid + dy * dy);
                bestOpen = MathF.Min(bestOpen, open * open + dy * dy);
            }

            float distance = solidAt(x, y) ? -(MathF.Sqrt(bestOpen) - 0.5f) : MathF.Sqrt(bestSolid) - 0.5f;
            field[x + y * width] = distance * texelWorld;
        }

        return field;
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(7, 8)]
    public void The_field_worked_out_ahead_is_the_one_the_shaders_make(int seed, int texelsPerCell)
    {
        var random = new Random(seed);
        var map = new OcclusionMap2D(23, 17, new Vector2(-40, 10), new Vector2(2.0f, 3.0f), texelsPerCell);

        // Some walls in blobs and some loose cells, and a few rows left open all the way across
        for (int y = 0; y < map.Height; y++)
        for (int x = 0; x < map.Width; x++)
            map[x, y] = y % 5 != 4 && random.NextDouble() < (x > 8 && x < 14 ? 0.7 : 0.15);

        bool SolidAt(int x, int y) => map[Math.Min(x / texelsPerCell, map.Width - 1), Math.Min(y / texelsPerCell, map.Height - 1)];

        map.Prepare();
        Half[] prepared = Assert.IsType<Half[]>(map.PreparedField);
        float[] expected = TheShadersField(map, SolidAt);

        Assert.Equal(expected.Length, prepared.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            // Both end up as halves on the GPU, so they only have to agree to what a half holds
            Assert.Equal((float)(Half)expected[i], (float)prepared[i], 0.01f);
        }
    }

    [Fact]
    public void Two_grids_of_the_same_cells_share_the_field_and_another_grid_does_not()
    {
        static OcclusionMap2D Make(bool extra)
        {
            var map = new OcclusionMap2D(9, 7, Vector2.Zero, Vector2.One, 2);
            map[3, 3] = map[4, 3] = true;
            if (extra) map[0, 6] = true;
            map.Prepare();
            return map;
        }

        Half[]? first = Make(false).PreparedField, second = Make(false).PreparedField, other = Make(true).PreparedField;
        Assert.Same(first, second);
        Assert.NotSame(first, other);
    }

    [Fact]
    public void A_grid_with_nothing_solid_is_as_far_from_a_wall_as_a_half_goes()
    {
        var map = new OcclusionMap2D(4, 4, Vector2.Zero, Vector2.One, 1);
        map.Prepare();

        Assert.All(Assert.IsType<Half[]>(map.PreparedField), distance => Assert.True(Half.IsPositiveInfinity(distance)));
    }
}
