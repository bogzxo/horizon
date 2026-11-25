using System.Numerics;

namespace ThreeBody.Rendering;

/// <summary>A ring buffer of the last positions of one body, oldest overwritten first.</summary>
public sealed class Trail
{
    private readonly Vector2[] points;
    private int head;   // next write slot
    private int count;

    public Trail(int capacity) => points = new Vector2[capacity];

    public int Count => count;

    public void Clear()
    {
        head = 0;
        count = 0;
    }

    public void Add(Vector2 p)
    {
        points[head] = p;
        head = (head + 1) % points.Length;
        if (count < points.Length) count++;
    }

    /// <summary>Point by age, 0 is the oldest.</summary>
    public Vector2 this[int index] => points[(head - count + index + points.Length * 2) % points.Length];
}
