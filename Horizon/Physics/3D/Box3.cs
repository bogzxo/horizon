using System.Numerics;

namespace Horizon.Physics.Colliders;

/// <summary>
/// A box lined up with the axes, from its smallest corner to its biggest. What everything in a world with depth bumps
/// into is made of, a block is one, a player is one, a crate is one. Nothing turns, which is the whole trick, two
/// boxes that can't turn are about the cheapest question there is.
/// </summary>
public readonly record struct Box3(Vector3 Min, Vector3 Max)
{
    /// <summary>A box by its middle and how big it is all the way across.</summary>
    public static Box3 FromCenter(Vector3 center, Vector3 size) => new(center - size * 0.5f, center + size * 0.5f);

    /// <summary>A box standing on a point, the way a somebody stands on their feet, <paramref name="size"/> wide, high and deep.</summary>
    public static Box3 FromFeet(Vector3 feet, Vector3 size) => new(
        new Vector3(feet.X - size.X * 0.5f, feet.Y, feet.Z - size.Z * 0.5f),
        new Vector3(feet.X + size.X * 0.5f, feet.Y + size.Y, feet.Z + size.Z * 0.5f));

    /// <summary>The box of one cell of a grid, a block.</summary>
    public static Box3 Cell(int x, int y, int z) => new(new Vector3(x, y, z), new Vector3(x + 1, y + 1, z + 1));

    public Vector3 Size => Max - Min;

    public Vector3 Center => (Min + Max) * 0.5f;

    /// <summary>Whether the two share some room. Touching faces don't count, somebody standing on the floor isn't in it.</summary>
    public bool Intersects(in Box3 other) =>
        Min.X < other.Max.X && Max.X > other.Min.X &&
        Min.Y < other.Max.Y && Max.Y > other.Min.Y &&
        Min.Z < other.Max.Z && Max.Z > other.Min.Z;

    public bool Contains(Vector3 point) =>
        point.X >= Min.X && point.X <= Max.X &&
        point.Y >= Min.Y && point.Y <= Max.Y &&
        point.Z >= Min.Z && point.Z <= Max.Z;

    /// <summary>The same box somewhere else.</summary>
    public Box3 Offset(Vector3 by) => new(Min + by, Max + by);

    /// <summary>Grown by an amount on every side, shrunk by a negative one.</summary>
    public Box3 Inflate(float by) => new(Min - new Vector3(by), Max + new Vector3(by));

    /// <summary>The box the two of them fit in.</summary>
    public Box3 Union(in Box3 other) => new(Vector3.Min(Min, other.Min), Vector3.Max(Max, other.Max));

    /// <summary>The room this box goes through on its way along <paramref name="by"/>, start to finish.</summary>
    public Box3 Swept(Vector3 by) => Union(Offset(by));

    /// <summary>
    /// How far along a ray it is to where it goes into the box, null if it misses. A ray that starts inside is in at 0.
    /// Which face it came in through comes out as the normal of that face.
    /// </summary>
    public float? Raycast(Vector3 origin, Vector3 direction, out Vector3 normal)
    {
        normal = Vector3.Zero;
        float near = float.NegativeInfinity, far = float.PositiveInfinity;
        int axis = -1;
        float sign = 0.0f;

        for (int i = 0; i < 3; i++)
        {
            float o = origin[i], d = direction[i], min = Min[i], max = Max[i];
            if (MathF.Abs(d) < 1e-12f)
            {
                // Going nowhere along this axis, it's either between the faces the whole way or never
                if (o < min || o > max) return null;
                continue;
            }

            float t0 = (min - o) / d, t1 = (max - o) / d;
            float face = -1.0f;
            if (t0 > t1)
            {
                (t0, t1) = (t1, t0);
                face = 1.0f;
            }

            if (t0 > near)
            {
                near = t0;
                axis = i;
                sign = face;
            }

            far = MathF.Min(far, t1);
            if (near > far) return null;
        }

        if (far < 0.0f) return null;
        if (near < 0.0f) return 0.0f;

        if (axis >= 0)
        {
            Vector3 n = Vector3.Zero;
            n[axis] = sign;
            normal = n;
        }

        return near;
    }

    /* How far a box can go along one axis before it runs into this one. The other two axes have to overlap for there
       to be anything in the way at all, and a box that is already in this one is let go, or something that got stuck
       would stay stuck forever */

    public float ClipX(in Box3 moving, float dx)
    {
        if (moving.Max.Y <= Min.Y || moving.Min.Y >= Max.Y || moving.Max.Z <= Min.Z || moving.Min.Z >= Max.Z) return dx;

        if (dx > 0.0f && moving.Max.X <= Min.X) return MathF.Min(dx, Min.X - moving.Max.X);
        if (dx < 0.0f && moving.Min.X >= Max.X) return MathF.Max(dx, Max.X - moving.Min.X);
        return dx;
    }

    public float ClipY(in Box3 moving, float dy)
    {
        if (moving.Max.X <= Min.X || moving.Min.X >= Max.X || moving.Max.Z <= Min.Z || moving.Min.Z >= Max.Z) return dy;

        if (dy > 0.0f && moving.Max.Y <= Min.Y) return MathF.Min(dy, Min.Y - moving.Max.Y);
        if (dy < 0.0f && moving.Min.Y >= Max.Y) return MathF.Max(dy, Max.Y - moving.Min.Y);
        return dy;
    }

    public float ClipZ(in Box3 moving, float dz)
    {
        if (moving.Max.X <= Min.X || moving.Min.X >= Max.X || moving.Max.Y <= Min.Y || moving.Min.Y >= Max.Y) return dz;

        if (dz > 0.0f && moving.Max.Z <= Min.Z) return MathF.Min(dz, Min.Z - moving.Max.Z);
        if (dz < 0.0f && moving.Min.Z >= Max.Z) return MathF.Max(dz, Max.Z - moving.Min.Z);
        return dz;
    }
}
