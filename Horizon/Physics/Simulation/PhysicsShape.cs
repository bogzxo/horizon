using System.Numerics;

using Horizon.Physics.Fixtures;

namespace Horizon.Physics.Simulation;

/// <summary>
/// A fixture as it sits in the world right now (its body position already added), in the form the particles collide against.
/// </summary>
internal struct PhysicsShape
{
    public Vector2 Min, Max;        // The box around the shape, for rectangles this is the shape itself
    public Vector2 Velocity;        // How fast the body it belongs to is moving, zero for the map
    public float Radius;
    public bool IsCircle;

    // For an outline, its pieces (two points each, relative to the origin) and how many points of the array are its
    public Vector2[]? Outline;
    public int OutlineCount;
    public Vector2 Origin;

    public readonly Vector2 Centre => (Min + Max) * 0.5f;

    /// <summary>
    /// Helper method to place a fixture in the world, false for shapes the particles can't collide with.
    /// </summary>
    public static bool TryCreate(IPhysicsFixture fixture, Vector2 bodyPosition, Vector2 bodyVelocity, out PhysicsShape shape)
    {
        switch (fixture)
        {
            case CirclePhysicsFixture circle:
                Vector2 centre = bodyPosition + circle.Position;
                shape = new PhysicsShape
                {
                    Min = centre - new Vector2(circle.Radius),
                    Max = centre + new Vector2(circle.Radius),
                    Velocity = bodyVelocity,
                    Radius = circle.Radius,
                    IsCircle = true
                };
                return true;

            case RectanglePhysicsFixture rectangle:
                shape = new PhysicsShape
                {
                    Min = bodyPosition + rectangle.Bounds.Position,
                    Max = bodyPosition + rectangle.Bounds.Position + rectangle.Bounds.Size,
                    Velocity = bodyVelocity
                };
                return true;

            case OutlinePhysicsFixture outline when outline.PointCount > 0:
                shape = new PhysicsShape
                {
                    Min = bodyPosition + outline.Min,
                    Max = bodyPosition + outline.Max,
                    Velocity = bodyVelocity,
                    Outline = outline.Points,
                    OutlineCount = outline.PointCount,
                    Origin = bodyPosition
                };
                return true;

            default:
                shape = default;
                return false;
        }
    }

    /// <summary>
    /// Helper method to measure a point against an outline, whether it is inside and the nearest point of the outline.
    /// </summary>
    private readonly bool MeasureOutline(Vector2 point, out Vector2 nearest, out float distanceSquared)
    {
        Vector2 local = point - Origin;
        bool inside = false;

        nearest = local;
        distanceSquared = float.MaxValue;

        for (int i = 0; i + 1 < OutlineCount; i += 2)
        {
            Vector2 a = Outline![i], b = Outline[i + 1];

            // Inside is having an odd number of pieces of the outline to one side
            if ((a.Y > local.Y) != (b.Y > local.Y) && local.X < a.X + (local.Y - a.Y) / (b.Y - a.Y) * (b.X - a.X))
            {
                inside = !inside;
            }

            Vector2 along = b - a;
            float lengthSquared = along.LengthSquared();
            float share = lengthSquared > 0.0f ? Math.Clamp(Vector2.Dot(local - a, along) / lengthSquared, 0.0f, 1.0f) : 0.0f;
            Vector2 closest = a + along * share;

            float found = Vector2.DistanceSquared(local, closest);
            if (found < distanceSquared) (distanceSquared, nearest) = (found, closest);
        }

        return inside;
    }

    /// <summary>
    /// Tests if a point is inside of the shape.
    /// </summary>
    public readonly bool Contains(Vector2 point)
    {
        if (point.X <= Min.X || point.X >= Max.X || point.Y <= Min.Y || point.Y >= Max.Y) return false;

        if (Outline is not null) return MeasureOutline(point, out _, out _);

        return !IsCircle || Vector2.DistanceSquared(point, Centre) < Radius * Radius;
    }

    /// <summary>
    /// Tests a circle (a particle) against the shape.
    /// </summary>
    /// <param name="normal">The way the circle has to be pushed to get out of the shape.</param>
    /// <param name="depth">How far it has to be pushed.</param>
    public readonly bool Overlaps(Vector2 position, float radius, out Vector2 normal, out float depth)
    {
        const float epsilon = 0.0001f;

        normal = Vector2.UnitY;
        depth = 0.0f;

        // Cheap test first, nearly every shape we get asked about is nowhere near
        if (position.X + radius < Min.X || position.X - radius > Max.X ||
            position.Y + radius < Min.Y || position.Y - radius > Max.Y) return false;

        if (Outline is not null)
        {
            bool inside = MeasureOutline(position, out Vector2 edge, out float nearestSquared);
            if (!inside && nearestSquared >= radius * radius) return false;

            // Out by the nearest way there is, away from the outline from outside of it and through it from inside
            float gap = MathF.Sqrt(nearestSquared);
            Vector2 towards = Origin + edge - position;

            if (gap > epsilon) normal = inside ? towards / gap : -towards / gap;
            depth = inside ? gap + radius : radius - gap;
            return true;
        }

        if (IsCircle)
        {
            Vector2 away = position - Centre;
            float distanceSquared = away.LengthSquared();
            float reach = radius + Radius;

            if (distanceSquared >= reach * reach) return false;

            float distance = MathF.Sqrt(distanceSquared);
            if (distance > epsilon) normal = away / distance;
            depth = reach - distance;
            return true;
        }

        // Find the closest point of the rectangle, we are touching if that is within our radius
        Vector2 closest = Vector2.Clamp(position, Min, Max);
        Vector2 offset = position - closest;
        float offsetSquared = offset.LengthSquared();

        if (offsetSquared > radius * radius) return false;

        if (offsetSquared > epsilon)
        {
            float distance = MathF.Sqrt(offsetSquared);
            normal = offset / distance;
            depth = radius - distance;
            return true;
        }

        // Our centre is inside the rectangle, leave through whichever side is nearest
        float left = position.X - Min.X, right = Max.X - position.X;
        float down = position.Y - Min.Y, up = Max.Y - position.Y;
        float nearest = MathF.Min(MathF.Min(left, right), MathF.Min(down, up));

        if (nearest == up) normal = Vector2.UnitY;
        else if (nearest == down) normal = -Vector2.UnitY;
        else if (nearest == left) normal = -Vector2.UnitX;
        else normal = Vector2.UnitX;

        depth = nearest + radius;
        return true;
    }
}
