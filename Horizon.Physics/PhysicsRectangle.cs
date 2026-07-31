using System.Numerics;

namespace Horizon.Physics;

public struct PhysicsRectangle
{
    public Vector2 Position { get; init; }
    public Vector2 Size { get; init; }

    public readonly float X => Position.X;
    public readonly float Y => Position.Y;
    public readonly float Width => Size.X;
    public readonly float Height => Size.Y;

    public readonly float Left => Position.X;
    public readonly float Right => Position.X + Size.X;
    public readonly float Top => Position.Y;
    public readonly float Bottom => Position.Y + Size.Y;

    public PhysicsRectangle(float x, float y, float w, float h)
    {
        this.Position = new Vector2(x, y);
        this.Size = new Vector2(w, h);
    }

    public PhysicsRectangle(Vector2 position, Vector2 size)
    {
        this.Position = position;
        this.Size = size;
    }
}
