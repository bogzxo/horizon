using System.Numerics;

namespace Horizon.Physics.Fixtures;

public class RectanglePhysicsFixture : IPhysicsFixture
{
    public PhysicsFixtureShape Shape { get; init; } = PhysicsFixtureShape.Rectangle;

    public PhysicsRectangle Bounds { get; init; }

    public Vector2 Position => Bounds.Position;
    public Vector2 Size => Bounds.Size;

    public PhysicsBodyComponent2D Parent { get; init; }

    public RectanglePhysicsFixture(in PhysicsBodyComponent2D parent, Vector2 position, Vector2 size)
    {
        this.Parent = parent;
        this.Bounds = new PhysicsRectangle(position, size);
    }

    public bool TestIntersection(in IPhysicsFixture other)
    {
        switch (other.Shape)
        {
            case PhysicsFixtureShape.Rectangle:
                {
                    var rect = (RectanglePhysicsFixture)other;

                    // AABB vs AABB
                    return this.Parent.Position.X + this.Bounds.Left < other.Parent.Position.X + rect.Bounds.Right &&
                           this.Parent.Position.X + this.Bounds.Right > other.Parent.Position.X + rect.Bounds.Left &&
                           this.Parent.Position.Y + this.Bounds.Top < other.Parent.Position.Y + rect.Bounds.Bottom &&
                           this.Parent.Position.Y + this.Bounds.Bottom > other.Parent.Position.Y + rect.Bounds.Top;
                }
            case PhysicsFixtureShape.Circle:
                {
                    var circle = (CirclePhysicsFixture)other;
                    return CirclePhysicsFixture.IntersectsCircleAndRectangle(circle, this);
                }
            default:
                return false;
        }
    }
}
