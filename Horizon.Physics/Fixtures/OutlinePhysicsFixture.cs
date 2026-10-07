using System.Numerics;

namespace Horizon.Physics.Fixtures;

/// <summary>
/// A shape of any outline at all, as the pieces of straight line that go around it: the silhouette of a sprite, say
/// (see <c>MarchingSquares</c> for getting one). It can have holes and be in several parts, and it can be given
/// another outline at any time, which is how it follows an animation frame by frame.
/// <para>
/// Only particles collide with it. Bodies don't: testing one outline against another every step is a lot of work
/// for something a couple of circles do just as well. So a body keeps its plain fixtures for standing on the map,
/// and has one of these in <see cref="PhysicsBodyComponent2D.ParticleFixtures"/> for what rains down on it.
/// </para>
/// </summary>
public class OutlinePhysicsFixture(string tag = "") : IPhysicsFixture
{
    public string Tag { get; init; } = tag;
    public bool IsTouching { get; set; }
    public HashSet<IPhysicsFixture> ActiveContacts { get; } = new();
    public PhysicsFixtureShape Shape { get; init; } = PhysicsFixtureShape.Outline;

    // Two points for every piece of the outline, one after the other. Kept between outlines so setting one doesn't allocate
    private Vector2[] points = [];
    private int pointCount;

    /// <summary>The pieces of the outline, two points each, relative to the body.</summary>
    public ReadOnlySpan<Vector2> Segments => points.AsSpan(0, pointCount);

    /// <summary>The box around the outline, relative to the body. Nothing if there is no outline.</summary>
    public Vector2 Min { get; private set; }
    public Vector2 Max { get; private set; }

    // For the world, which reads them every step
    internal Vector2[] Points => points;
    internal int PointCount => pointCount;

    /// <summary>
    /// Gives the fixture another outline.
    /// </summary>
    /// <param name="segments">The pieces of the outline, two points each and relative to the body. None for no outline at all.</param>
    public void Set(ReadOnlySpan<Vector2> segments)
    {
        int count = segments.Length & ~1;
        if (points.Length < count) points = new Vector2[count];

        segments[..count].CopyTo(points);
        pointCount = count;

        Vector2 min = new(float.MaxValue), max = new(float.MinValue);
        for (int i = 0; i < count; i++)
        {
            min = Vector2.Min(min, points[i]);
            max = Vector2.Max(max, points[i]);
        }

        (Min, Max) = count > 0 ? (min, max) : (Vector2.Zero, Vector2.Zero);
    }

    // See the summary: bodies go by their other fixtures
    public bool TestIntersection(in IPhysicsFixture other, Vector2 positionOffset, Vector2 otherPositionOffset) => false;
}
