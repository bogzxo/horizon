using System.Numerics;

namespace Horizon.Core;

/// <summary>
/// Finds the outlines of what is solid in a grid of cells (the pixels of a sprite that aren't see-through, say) by
/// marching squares: the grid is gone over four neighbouring cells at a time, and which of the four are solid says
/// how the outline passes between them. What comes out is every closed line there is around something solid and
/// around the holes in it, running halfway between a solid cell and an empty one, with its corners cut at a slant.
/// <para>
/// Straight from the grid an outline has a point for every cell it passes, a few hundred for a small sprite. They
/// are thinned out afterwards: a point goes if the line doesn't stray further than the tolerance without it.
/// </para>
/// </summary>
public static class MarchingSquares
{
    // The sides of a square the outline can cross
    private const int TOP = 0, RIGHT = 1, BOTTOM = 2, LEFT = 3, NONE = -1;

    // Which sides the outline joins for each of the sixteen ways four cells can be solid, two lines at the most.
    // The cells count 8 (top left), 4 (top right), 2 (bottom right) and 1 (bottom left). Where only two opposite
    // ones are solid they are taken to be apart, which keeps two things that only touch at a corner two things
    private static readonly int[] Sides =
    [
        NONE, NONE, NONE, NONE,
        LEFT, BOTTOM, NONE, NONE,
        BOTTOM, RIGHT, NONE, NONE,
        LEFT, RIGHT, NONE, NONE,
        TOP, RIGHT, NONE, NONE,
        TOP, RIGHT, LEFT, BOTTOM,
        TOP, BOTTOM, NONE, NONE,
        LEFT, TOP, NONE, NONE,
        LEFT, TOP, NONE, NONE,
        TOP, BOTTOM, NONE, NONE,
        LEFT, TOP, BOTTOM, RIGHT,
        TOP, RIGHT, NONE, NONE,
        LEFT, RIGHT, NONE, NONE,
        BOTTOM, RIGHT, NONE, NONE,
        LEFT, BOTTOM, NONE, NONE,
        NONE, NONE, NONE, NONE
    ];

    /// <summary>
    /// Traces the outlines of everything solid in a grid.
    /// </summary>
    /// <param name="solid">The grid row by row, the first row being the top one.</param>
    /// <param name="width">How many cells a row has.</param>
    /// <param name="height">How many rows there are.</param>
    /// <param name="tolerance">How far (in cells) an outline may stray from the one the grid really has, 0 keeps every point.</param>
    /// <returns>
    /// Every outline as the points it goes through, the last of them joining up with the first. In cells from the
    /// top left corner of the grid, x to the right and y downwards: the middle of the first cell is (0.5, 0.5).
    /// </returns>
    public static List<Vector2[]> Trace(ReadOnlySpan<bool> solid, int width, int height, float tolerance = 0.75f)
    {
        var outlines = new List<Vector2[]>();
        if (width < 1 || height < 1 || solid.Length < width * height) return outlines;

        // Every piece of outline as the two points it joins. A point is where the outline crosses from one cell
        // to the next, which is halfway between them: kept in halves of a cell, so they are whole numbers
        var ends = new List<(int X, int Y)>();
        var at = new Dictionary<(int, int), (int First, int Second)>();

        // The squares reach one past the grid all round, what is solid right at the edge has an outline too
        for (int y = -1; y < height; y++)
        {
            for (int x = -1; x < width; x++)
            {
                int kind =
                    (IsSolid(solid, width, height, x, y) ? 8 : 0) |
                    (IsSolid(solid, width, height, x + 1, y) ? 4 : 0) |
                    (IsSolid(solid, width, height, x + 1, y + 1) ? 2 : 0) |
                    (IsSolid(solid, width, height, x, y + 1) ? 1 : 0);

                for (int line = 0; line < 2; line++)
                {
                    int from = Sides[kind * 4 + line * 2], to = Sides[kind * 4 + line * 2 + 1];
                    if (from == NONE) break;

                    int piece = ends.Count / 2;
                    ends.Add(PointOn(from, x, y));
                    ends.Add(PointOn(to, x, y));

                    Note(at, ends[^2], piece);
                    Note(at, ends[^1], piece);
                }
            }
        }

        // The pieces are strung together into closed lines: from a piece on to the one that shares its far end
        var used = new bool[ends.Count / 2];
        var points = new List<Vector2>();

        for (int start = 0; start < used.Length; start++)
        {
            if (used[start]) continue;

            points.Clear();
            int piece = start;
            (int X, int Y) point = ends[start * 2];

            while (!used[piece])
            {
                used[piece] = true;

                // In at one end and out at the other
                var (first, second) = (ends[piece * 2], ends[piece * 2 + 1]);
                point = first == point ? second : first;

                // In cells again, from the corner of the grid rather than the middle of its first cell
                points.Add(new Vector2(point.X * 0.5f + 0.5f, point.Y * 0.5f + 0.5f));

                var (one, other) = at[point];
                piece = one == piece ? other : one;
                if (piece < 0) break;
            }

            if (points.Count >= 3) outlines.Add(Simplify(points, tolerance));
        }

        return outlines;
    }

    private static bool IsSolid(ReadOnlySpan<bool> solid, int width, int height, int x, int y) =>
        x >= 0 && y >= 0 && x < width && y < height && solid[y * width + x];

    // Where the outline crosses a side of the square whose top left cell is (x, y), in halves of a cell from the middle of the first one
    private static (int, int) PointOn(int side, int x, int y) => side switch
    {
        TOP => (x * 2 + 1, y * 2),
        RIGHT => (x * 2 + 2, y * 2 + 1),
        BOTTOM => (x * 2 + 1, y * 2 + 2),
        _ => (x * 2, y * 2 + 1)
    };

    private static void Note(Dictionary<(int, int), (int First, int Second)> at, (int, int) point, int piece)
    {
        at[point] = at.TryGetValue(point, out var known) ? (known.First, piece) : (piece, -1);
    }

    /// <summary>
    /// Helper method to thin a closed line out (Ramer, Douglas and Peucker): between two points that are kept, the one
    /// furthest from the straight line between them is kept as well if it is further than the tolerance, and so on.
    /// </summary>
    private static Vector2[] Simplify(List<Vector2> points, float tolerance)
    {
        if (tolerance <= 0.0f || points.Count < 4) return [.. points];

        // A closed line has no ends to start from: the first point and the one furthest from it will do
        int far = 0;
        float furthest = 0.0f;
        for (int i = 1; i < points.Count; i++)
        {
            float distance = Vector2.DistanceSquared(points[0], points[i]);
            if (distance > furthest) (furthest, far) = (distance, i);
        }

        var keep = new bool[points.Count];
        keep[0] = keep[far] = true;

        Keep(points, keep, 0, far, tolerance);
        Keep(points, keep, far, points.Count, tolerance);

        var kept = new List<Vector2>();
        for (int i = 0; i < points.Count; i++)
        {
            if (keep[i]) kept.Add(points[i]);
        }

        return [.. kept];
    }

    // From one kept point to the next, the last of them can be the first point come round again
    private static void Keep(List<Vector2> points, bool[] keep, int from, int to, float tolerance)
    {
        if (to - from < 2) return;

        Vector2 start = points[from], end = points[to % points.Count];
        Vector2 along = end - start;
        float length = along.Length();

        int far = -1;
        float furthest = tolerance;
        for (int i = from + 1; i < to; i++)
        {
            Vector2 away = points[i] - start;
            float distance = length > 0.0001f ? MathF.Abs(away.X * along.Y - away.Y * along.X) / length : away.Length();
            if (distance > furthest) (furthest, far) = (distance, i);
        }

        if (far < 0) return;

        keep[far] = true;
        Keep(points, keep, from, far, tolerance);
        Keep(points, keep, far, to, tolerance);
    }
}
