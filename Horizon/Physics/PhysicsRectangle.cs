using System.Numerics;

namespace Horizon.Physics;

public readonly struct PhysicsRectangle
{
    public Vector2 Position { get; init; }
    public Vector2 Size { get; init; }

    public float X => Position.X;
    public float Y => Position.Y;
    public float Width => Size.X;
    public float Height => Size.Y;

    public float Left => Position.X;
    public float Right => Position.X + Size.X;
    public float Top => Position.Y;
    public float Bottom => Position.Y + Size.Y;

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
