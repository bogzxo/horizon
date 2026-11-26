using System.Numerics;

using Horizon.Physics.Simulation;

namespace Horizon.Physics.Fixtures;

public class RectanglePhysicsFixture(Vector2 position, Vector2 size, string tag="") : IPhysicsFixture
{
    public string Tag { get; init; } = tag;
    public bool IsTouching { get; set; }
    public HashSet<IPhysicsFixture> ActiveContacts { get; } = new(4);
    public Vector3? DebugColour { get; set; }
    public bool OneWay { get; init; }
    public PhysicsFixtureShape Shape { get; init; } = PhysicsFixtureShape.Rectangle;

    private PhysicsRectangle _bounds = new(position, size);
    private PhysicsShape _localShape = new()
    {
        Min = position,
        Max = position + size
    };

    // Can be moved and resized at any time, for a box that follows something (what of a fighter can be hit right now)
    public PhysicsRectangle Bounds
    {
        get => _bounds;
        set
        {
            _bounds = value;
            _localShape = new PhysicsShape
            {
                Min = value.Position,
                Max = value.Position + value.Size
            };
        }
    }

    internal ref readonly PhysicsShape LocalShape => ref _localShape;

    public Vector2 Position => _bounds.Position;
    public Vector2 Size => _bounds.Size;

    public bool TestIntersection(in IPhysicsFixture other, Vector2 positionOffset, Vector2 otherPositionOffset)
    {
        switch (other.Shape)
        {
            case PhysicsFixtureShape.Rectangle:
                {
                    var rect = (RectanglePhysicsFixture)other;

                    // AABB vs AABB
                    return positionOffset.X + this.Bounds.Left < otherPositionOffset.X + rect.Bounds.Right &&
                           positionOffset.X + this.Bounds.Right > otherPositionOffset.X + rect.Bounds.Left &&
                           positionOffset.Y + this.Bounds.Top < otherPositionOffset.Y + rect.Bounds.Bottom &&
                           positionOffset.Y + this.Bounds.Bottom > otherPositionOffset.Y + rect.Bounds.Top;
                }
            case PhysicsFixtureShape.Circle:
                {
                    var circle = (CirclePhysicsFixture)other;
                    // The circle is the other one here, so it is the other body's position that goes with it. These two
                    // were the wrong way round for as long as this has existed, a box asked about a circle measured the
                    // circle from where the box's body stood. Nobody noticed because nobody uses circles, the cowards
                    return CirclePhysicsFixture.IntersectsCircleAndRectangle(circle, this, otherPositionOffset, positionOffset);
                }
            default:
                return false;
        }
    }
}
