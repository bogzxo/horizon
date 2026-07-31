using System.Numerics;

namespace Horizon.Physics.Fixtures;

public class CirclePhysicsFixture(in PhysicsBodyComponent2D parent, float radius, Vector2 position) : IPhysicsFixture
{
    public float Radius { get; init; } = radius;
    public Vector2 Position { get; init; } = position;
    public PhysicsFixtureShape Shape { get; init; } = PhysicsFixtureShape.Circle;
    public PhysicsBodyComponent2D Parent { get; init; } = parent;

    public bool TestIntersection(in IPhysicsFixture other)
    {
        switch (other.Shape)
        {
            case PhysicsFixtureShape.Circle:
                {
                    var circle = (CirclePhysicsFixture)other;

                    float radiusSum = this.Radius + circle.Radius;
                    float distanceSquared = Vector2.DistanceSquared(this.Position + this.Parent.Position, circle.Parent.Position + circle.Position);

                    return distanceSquared <= (radiusSum * radiusSum);
                }
            case PhysicsFixtureShape.Rectangle:
                {
                    var rect = (RectanglePhysicsFixture)other;
                    return IntersectsCircleAndRectangle(this, rect);
                }
            default:
                return false;
        }
    }
    // Stack allocation free test
    public static bool IntersectsCircleAndRectangle(CirclePhysicsFixture circle, RectanglePhysicsFixture rect)
    {
        // Clamp the circle center to the bounds of the rectangle to find the closest point
        float closestX = Math.Clamp(circle.Parent.Position.X + circle.Position.X,
            rect.Parent.Position.X + rect.Bounds.Left, rect.Parent.Position.X + rect.Bounds.Right);
        float closestY = Math.Clamp(circle.Parent.Position.Y + circle.Position.Y,
            rect.Parent.Position.Y + rect.Bounds.Top, rect.Parent.Position.Y + rect.Bounds.Bottom);

        // distanec from circle center to closest point on rectangle
        float deltaX = circle.Position.X - closestX;
        float deltaY = circle.Position.Y - closestY;

        float distanceSquared = (deltaX * deltaX) + (deltaY * deltaY);
        return distanceSquared <= (circle.Radius * circle.Radius);
    }
}
