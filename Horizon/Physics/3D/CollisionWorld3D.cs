using System.Numerics;

namespace Horizon.Physics.Colliders;

/// <summary>Where a ray ran into something, what it was and which face it came in through.</summary>
/// <param name="Point">Where on the face it landed.</param>
/// <param name="Normal">The face's way out, one of the six axis directions.</param>
/// <param name="Distance">How far along the ray that was.</param>
/// <param name="Collider">The box collider it hit, null when it was a field (a voxel world).</param>
/// <param name="Cell">The cell of the field it hit, a block. Nothing much for a box collider.</param>
public readonly record struct RaycastHit3D(Vector3 Point, Vector3 Normal, float Distance, BoxCollider3D? Collider, (int X, int Y, int Z) Cell);

/// <summary>
/// Something with solid bits in it that is too big (or too many) to be a list of boxes, which in practice is a world
/// of blocks. It hands over the boxes in a region when it is asked, and nobody has to know there are a few billion of
/// them. <see cref="Rendering.Voxels.VoxelTree"/> is one.
/// </summary>
public interface ICollisionField
{
    /// <summary>Adds the boxes of whatever is solid in a region to the list.</summary>
    void CollectSolids(in Box3 region, List<Box3> into);

    /// <summary>The first solid thing along a ray within a distance, if there is one.</summary>
    bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RaycastHit3D hit);

    /// <summary>Whether a point is under water (or whatever else is wet), for somebody to swim in. Nothing is, unless the field says.</summary>
    bool IsLiquid(Vector3 point) => false;
}

/// <summary>
/// A box that things bump into, a crate, a door, a platform. Move it by setting <see cref="Bounds"/>, a character
/// stood on it is not carried along (yet), it's pushed out of the way at best.
/// </summary>
public sealed class BoxCollider3D(Box3 bounds)
{
    public Box3 Bounds { get; set; } = bounds;

    /// <summary>Whether it's there to be bumped into at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whatever the game wants to hang on it, so a ray that hits it can tell what it hit.</summary>
    public object? Tag { get; set; }
}

/// <summary>
/// Everything solid in a world with depth, the boxes (<see cref="BoxCollider3D"/>) and the fields (a world of
/// blocks, see <see cref="ICollisionField"/>), and the questions a game asks of them. What is in the way of a box
/// going somewhere (<see cref="CollectSolids"/>, which <see cref="CharacterController3D"/> moves by), what a ray
/// hits (<see cref="Raycast"/>, for what the player is looking at), and whether a box fits somewhere. Static, no
/// bodies, no forces, nothing falls over by itself. A character controller is all a game like this wants, and the
/// boxes are cheap enough that one more of everything never matters.
/// <code>
/// var collision = new CollisionWorld3D();
/// collision.Add(voxelTree);
/// collision.Add(new BoxCollider3D(Box3.FromFeet(new Vector3(10, 64, 10), new Vector3(2))));
/// player = AddComponent(new CharacterController3D(collision, spawn));
/// </code>
/// Not made for threads, whoever moves things (the simulation thread) is the only one asking.
/// </summary>
public sealed class CollisionWorld3D
{
    private readonly List<BoxCollider3D> boxes = [];
    private readonly List<ICollisionField> fields = [];

    public IReadOnlyList<BoxCollider3D> Boxes => boxes;

    public IReadOnlyList<ICollisionField> Fields => fields;

    public BoxCollider3D Add(BoxCollider3D box)
    {
        boxes.Add(box);
        return box;
    }

    public void Add(ICollisionField field) => fields.Add(field);

    public bool Remove(BoxCollider3D box) => boxes.Remove(box);

    public bool Remove(ICollisionField field) => fields.Remove(field);

    /// <summary>Adds the box of everything solid that is in a region (or partly in it) to the list.</summary>
    public void CollectSolids(in Box3 region, List<Box3> into)
    {
        foreach (BoxCollider3D box in boxes)
        {
            if (box.Enabled && box.Bounds.Intersects(region)) into.Add(box.Bounds);
        }

        foreach (ICollisionField field in fields)
            field.CollectSolids(region, into);
    }

    // Kept between questions so asking doesn't make garbage
    private readonly List<Box3> scratch = [];

    /// <summary>Whether a box would be in anything solid where it is, for whether something fits (a block placed where somebody stands).</summary>
    public bool Overlaps(in Box3 box)
    {
        scratch.Clear();
        CollectSolids(box, scratch);
        foreach (Box3 solid in scratch)
        {
            if (solid.Intersects(box)) return true;
        }

        return false;
    }

    /// <summary>Whether a point is in a liquid, for whatever swims.</summary>
    public bool IsLiquid(Vector3 point)
    {
        foreach (ICollisionField field in fields)
        {
            if (field.IsLiquid(point)) return true;
        }

        return false;
    }

    /// <summary>The nearest solid thing along a ray, within a distance.</summary>
    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RaycastHit3D hit)
    {
        hit = default;
        if (direction.LengthSquared() < 1e-12f) return false;
        direction = Vector3.Normalize(direction);

        bool found = false;
        float nearest = maxDistance;

        foreach (BoxCollider3D box in boxes)
        {
            if (!box.Enabled) continue;
            if (box.Bounds.Raycast(origin, direction, out Vector3 normal) is not { } t || t > nearest) continue;

            nearest = t;
            Vector3 point = origin + direction * t;
            hit = new RaycastHit3D(point, normal, t, box, ((int)MathF.Floor(point.X), (int)MathF.Floor(point.Y), (int)MathF.Floor(point.Z)));
            found = true;
        }

        foreach (ICollisionField field in fields)
        {
            if (!field.Raycast(origin, direction, nearest, out RaycastHit3D fieldHit) || fieldHit.Distance > nearest) continue;

            nearest = fieldHit.Distance;
            hit = fieldHit;
            found = true;
        }

        return found;
    }
}
