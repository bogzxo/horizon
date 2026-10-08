using System.Numerics;

namespace Horizon.Physics.Fixtures;

public class CirclePhysicsFixture(float radius, Vector2 position, string tag="") : IPhysicsFixture
{
    public string Tag { get; init; } = tag;
    public bool IsTouching { get; set; }
    public HashSet<IPhysicsFixture> ActiveContacts { get; } = new();

    public float Radius { get; init; } = radius;
    public Vector2 Position { get; init; } = position;
    public PhysicsFixtureShape Shape { get; init; } = PhysicsFixtureShape.Circle;

    public bool TestIntersection(in IPhysicsFixture other, Vector2 positionOffset, Vector2 otherPositionOffset)
    {
        switch (other.Shape)
        {
            case PhysicsFixtureShape.Circle:
                {
                    var circle = (CirclePhysicsFixture)other;

                    float radiusSum = this.Radius + circle.Radius;
                    float distanceSquared = Vector2.DistanceSquared(this.Position + positionOffset, circle.Position + otherPositionOffset);

                    return distanceSquared <= (radiusSum * radiusSum);
                }
            case PhysicsFixtureShape.Rectangle:
                {
                    var rect = (RectanglePhysicsFixture)other;
                    return IntersectsCircleAndRectangle(this, rect, positionOffset, otherPositionOffset);
                }
            default:
                return false;
        }
    }
    // Stack allocation free test
    public static bool IntersectsCircleAndRectangle(CirclePhysicsFixture circle, RectanglePhysicsFixture rect, Vector2 positionOffset, Vector2 otherPositionOffset)
    {
        // Clamp the circle center to the bounds of the rectangle to find the closest point
        float closestX = Math.Clamp(positionOffset.X + circle.Position.X,
            otherPositionOffset.X + rect.Bounds.Left, otherPositionOffset.X + rect.Bounds.Right);
        float closestY = Math.Clamp(positionOffset.Y + circle.Position.Y,
            otherPositionOffset.Y + rect.Bounds.Top, otherPositionOffset.Y + rect.Bounds.Bottom);

        // distance from circle center to closest point on rectangle
        float deltaX = positionOffset.X + circle.Position.X - closestX;
        float deltaY = positionOffset.Y + circle.Position.Y - closestY;

        float distanceSquared = (deltaX * deltaX) + (deltaY * deltaY);
        return distanceSquared <= (circle.Radius * circle.Radius);
    }
}
