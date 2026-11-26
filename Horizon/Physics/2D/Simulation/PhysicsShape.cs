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
    public int OutlineVersion;
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
                    OutlineVersion = outline.Version,
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

    /// <summary>
    /// Tests whether a line segment starting outside the shape enters or crosses it, returning the earliest hit
    /// fraction in [0, 1] and the outward surface normal at entry.
    /// </summary>
    public readonly bool TryIntersectSegment(Vector2 from, Vector2 to, out float t, out Vector2 normal)
    {
        t = 0.0f;
        normal = default;

        float segMinX = MathF.Min(from.X, to.X), segMaxX = MathF.Max(from.X, to.X);
        float segMinY = MathF.Min(from.Y, to.Y), segMaxY = MathF.Max(from.Y, to.Y);
        if (segMaxX < Min.X || segMinX > Max.X || segMaxY < Min.Y || segMinY > Max.Y) return false;

        Vector2 d = to - from;

        if (Outline is not null)
        {
            float bestT = float.MaxValue;
            Vector2 bestNormal = default;
            Vector2 localFrom = from - Origin;

            for (int i = 0; i + 1 < OutlineCount; i += 2)
            {
                Vector2 a = Outline[i], b = Outline[i + 1];
                Vector2 e = b - a;
                float denom = d.X * e.Y - d.Y * e.X;
                if (MathF.Abs(denom) < 1e-8f) continue;

                Vector2 diff = a - localFrom;
                float hitT = (diff.X * e.Y - diff.Y * e.X) / denom;
                float hitU = (diff.X * d.Y - diff.Y * d.X) / denom;
                if (hitT < 0.0f || hitT > 1.0f || hitU < 0.0f || hitU > 1.0f || hitT >= bestT) continue;

                float eLen = e.Length();
                if (eLen < 1e-8f) continue;

                Vector2 n = new Vector2(-e.Y, e.X) / eLen;
                if (Vector2.Dot(n, d) > 0.0f) n = -n;

                bestT = hitT;
                bestNormal = n;
            }

            if (bestT <= 1.0f)
            {
                t = bestT;
                normal = bestNormal;
                return true;
            }

            return false;
        }

        if (IsCircle)
        {
            Vector2 m = from - Centre;
            float c = Vector2.Dot(m, m) - Radius * Radius;
            if (c <= 0.0f) return false;

            float a = Vector2.Dot(d, d);
            if (a < 1e-12f) return false;

            float b = Vector2.Dot(m, d);
            float disc = b * b - a * c;
            if (disc < 0.0f) return false;

            float hitT = (-b - MathF.Sqrt(disc)) / a;
            if (hitT < 0.0f || hitT > 1.0f) return false;

            Vector2 away = (from + d * hitT) - Centre;
            float len = away.Length();
            normal = len > 1e-6f ? away / len : Vector2.UnitY;
            t = hitT;
            return true;
        }

        // Rectangle slab test
        float tEnter = 0.0f, tExit = 1.0f;
        int hitAxis = -1;
        float hitSign = 0.0f;

        if (MathF.Abs(d.X) < 1e-8f)
        {
            if (from.X < Min.X || from.X > Max.X) return false;
        }
        else
        {
            float invX = 1.0f / d.X;
            float tx0 = (Min.X - from.X) * invX;
            float tx1 = (Max.X - from.X) * invX;
            float signX = -1.0f;
            if (tx0 > tx1)
            {
                (tx0, tx1) = (tx1, tx0);
                signX = 1.0f;
            }

            if (tx0 > tEnter || (hitAxis < 0 && tx0 == 0.0f && tx1 > 0.0f))
            {
                tEnter = tx0;
                hitAxis = 0;
                hitSign = signX;
            }

            tExit = MathF.Min(tExit, tx1);
            if (tEnter > tExit) return false;
        }

        if (MathF.Abs(d.Y) < 1e-8f)
        {
            if (from.Y < Min.Y || from.Y > Max.Y) return false;
        }
        else
        {
            float invY = 1.0f / d.Y;
            float ty0 = (Min.Y - from.Y) * invY;
            float ty1 = (Max.Y - from.Y) * invY;
            float signY = -1.0f;
            if (ty0 > ty1)
            {
                (ty0, ty1) = (ty1, ty0);
                signY = 1.0f;
            }

            if (ty0 > tEnter || (hitAxis < 0 && ty0 == 0.0f && ty1 > 0.0f))
            {
                tEnter = ty0;
                hitAxis = 1;
                hitSign = signY;
            }

            tExit = MathF.Min(tExit, ty1);
            if (tEnter > tExit) return false;
        }

        if (hitAxis < 0) return false;

        t = tEnter;
        normal = hitAxis == 0 ? new Vector2(hitSign, 0.0f) : new Vector2(0.0f, hitSign);
        return true;
    }
}
