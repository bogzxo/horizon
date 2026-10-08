using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Horizon.Core.Threading;
using Horizon.Rendering.Spriting;

namespace Horizon.Rendering.Primitives;

/// <summary>
/// The shapes a <see cref="PrimitiveRenderer"/> draws. Every one is a signed distance field in the shader, so the edges
/// are smooth at any size and an outline is as thin as it is asked to be.
/// </summary>
public enum ShapeKind : uint
{
    /// <summary>A box around its middle, turned by its rotation, with corners rounded by <see cref="ShapeInstance.Rounding"/>.</summary>
    Rectangle = 0,

    /// <summary>A disc around its middle. Its radius is <see cref="ShapeInstance.Size"/>.X.</summary>
    Circle = 1,

    /// <summary>A line from <see cref="ShapeInstance.Center"/> to <see cref="ShapeInstance.Size"/>, as wide as its thickness, with round ends.</summary>
    Segment = 2,

    /// <summary>An isosceles triangle standing on its base with its tip up (at a rotation of 0), as wide and high as its size.</summary>
    Triangle = 3
}

/// <summary>
/// One shape the way the shader is handed it: 64 bytes, laid out exactly like the std430 <c>Shape</c> struct in
/// shaders/primitives/shapes.vert. Made with the helpers (<see cref="Box"/>, <see cref="Disc"/>, <see cref="Line"/>...)
/// or by a <see cref="ShapeList"/>, which has the lot.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 64)]
public struct ShapeInstance
{
    /// <summary>The middle of a rectangle, circle or triangle; one end of a segment.</summary>
    public Vector2 Center;

    /// <summary>Half the width and height of a rectangle or triangle; the radius of a circle (in X); the other end of a segment.</summary>
    public Vector2 Size;

    /// <summary>In radians, anticlockwise around the middle. A segment has none.</summary>
    public float Rotation;

    /// <summary>0 for a filled shape, the width of the line for an outline or a segment.</summary>
    public float Thickness;

    /// <summary>How far the corners of a rectangle are rounded.</summary>
    public float Rounding;

    /// <summary>Where the shape sits along Z, for when the depth test is on.</summary>
    public float Depth;

    /// <summary>RGBA, 8 bits each with red in the low byte, see <see cref="SpriteItem.PackColor"/>.</summary>
    public uint Color;

    /// <summary>A <see cref="ShapeKind"/>.</summary>
    public uint Kind;

    private Vector2 padding0, padding1, padding2;

    public static readonly uint SizeInBytes = (uint)Unsafe.SizeOf<ShapeInstance>();

    /// <summary>A box around a point, as wide and high as a size, turned by an angle. Filled, or an outline of a thickness.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ShapeInstance Box(Vector2 center, Vector2 size, float rotation, uint color, float thickness = 0.0f, float rounding = 0.0f) => new()
    {
        Center = center,
        Size = size * 0.5f,
        Rotation = rotation,
        Thickness = thickness,
        Rounding = rounding,
        Color = color,
        Kind = (uint)ShapeKind.Rectangle
    };

    /// <summary>A disc around a point. Filled, or a ring of a thickness.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ShapeInstance Disc(Vector2 center, float radius, uint color, float thickness = 0.0f) => new()
    {
        Center = center,
        Size = new Vector2(radius),
        Thickness = thickness,
        Color = color,
        Kind = (uint)ShapeKind.Circle
    };

    /// <summary>A line from one point to another, of a width, with round ends.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ShapeInstance Line(Vector2 from, Vector2 to, float width, uint color) => new()
    {
        Center = from,
        Size = to,
        Thickness = width,
        Color = color,
        Kind = (uint)ShapeKind.Segment
    };

    /// <summary>A triangle standing on its base around a point, as wide and high as a size, its tip up at a rotation of 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ShapeInstance Triangle(Vector2 center, Vector2 size, float rotation, uint color, float thickness = 0.0f) => new()
    {
        Center = center,
        Size = size * 0.5f,
        Rotation = rotation,
        Thickness = thickness,
        Color = color,
        Kind = (uint)ShapeKind.Triangle
    };

    // Further than this (in units of the world) between two ticks is being put somewhere else, not moving there
    private const float TELEPORT = 256.0f;

    /// <summary>
    /// What a shape looks like partway (0 to 1) from one snapshot of it to the next: where it is, how big and how
    /// turned, its colour and how fast it goes are mixed. See <see cref="CanBlend"/> for when two aren't to be.
    /// </summary>
    public static ShapeInstance Blend(in ShapeInstance from, in ShapeInstance to, float amount)
    {
        if (amount >= 1.0f)
            return to;

        ShapeInstance shape = to;
        shape.Center = Interpolate.Linear(from.Center, to.Center, amount);
        shape.Size = Interpolate.Linear(from.Size, to.Size, amount);
        shape.Rotation = Interpolate.Angle(from.Rotation, to.Rotation, amount);
        shape.Thickness = Interpolate.Linear(from.Thickness, to.Thickness, amount);
        shape.Rounding = Interpolate.Linear(from.Rounding, to.Rounding, amount);
        shape.Color = Interpolate.PackedColor(from.Color, to.Color, amount);
        return shape;
    }

    /// <summary>
    /// Whether a shape went from one snapshot to the next in a way that can be shown on its way: it is still the same
    /// kind of shape, and it wasn't put somewhere else entirely.
    /// </summary>
    public static bool CanBlend(in ShapeInstance from, in ShapeInstance to)
    {
        if (from.Kind != to.Kind)
            return false;

        Vector2 moved = to.Center - from.Center;
        return moved.LengthSquared() <= TELEPORT * TELEPORT;
    }
}

/// <summary>
/// Shapes written down to be drawn, see <see cref="PrimitiveRenderer.Shapes"/>: the list the simulation fills, with
/// a helper for every kind of shape. Colours are RGBA from 0 to 1, rotations in radians, everything else in units of
/// the world. A <see cref="Vector3"/> colour is opaque.
/// </summary>
public sealed class ShapeList
{
    private ShapeInstance[] shapes = new ShapeInstance[64];
    private int count;

    public int Count => count;

    /// <summary>What has been written down so far.</summary>
    public ReadOnlySpan<ShapeInstance> Span => shapes.AsSpan(0, count);

    public ref ShapeInstance this[int index] => ref shapes[index];

    public void Clear() => count = 0;

    public void Add(in ShapeInstance shape)
    {
        if (count == shapes.Length)
            Array.Resize(ref shapes, shapes.Length * 2);

        shapes[count++] = shape;
    }

    /// <summary>Makes room for so many more shapes, for whoever knows how many are coming.</summary>
    public void Reserve(int more)
    {
        if (count + more > shapes.Length)
            Array.Resize(ref shapes, Math.Max(shapes.Length * 2, count + more));
    }

    /// <summary>Copies everything into another list, which is left with just that.</summary>
    public void CopyTo(ShapeList other)
    {
        other.count = 0;
        other.Reserve(count);
        shapes.AsSpan(0, count).CopyTo(other.shapes);
        other.count = count;
    }

    /* The shapes */

    /// <summary>A filled box around a point, as wide and high as a size, turned by an angle.</summary>
    public void Box(Vector2 center, Vector2 size, float rotation, Vector4 color, float rounding = 0.0f) =>
        Add(ShapeInstance.Box(center, size, rotation, SpriteItem.PackColor(color), 0.0f, rounding));

    public void Box(Vector2 center, Vector2 size, float rotation, Vector3 color, float rounding = 0.0f) =>
        Box(center, size, rotation, new Vector4(color, 1.0f), rounding);

    /// <summary>The outline of a box around a point, as wide and high as a size, turned by an angle.</summary>
    public void BoxOutline(Vector2 center, Vector2 size, float rotation, float thickness, Vector4 color, float rounding = 0.0f) =>
        Add(ShapeInstance.Box(center, size, rotation, SpriteItem.PackColor(color), MathF.Max(thickness, 0.0001f), rounding));

    /// <summary>A filled rectangle that isn't turned, from its smallest corner to its largest.</summary>
    public void FillRectangle(Vector2 min, Vector2 max, Vector4 color, float rounding = 0.0f) =>
        Box((min + max) * 0.5f, max - min, 0.0f, color, rounding);

    public void FillRectangle(Vector2 min, Vector2 max, Vector3 color, float rounding = 0.0f) =>
        FillRectangle(min, max, new Vector4(color, 1.0f), rounding);

    /// <summary>The outline of a rectangle that isn't turned, from its smallest corner to its largest.</summary>
    public void Rectangle(Vector2 min, Vector2 max, float thickness, Vector4 color, float rounding = 0.0f) =>
        BoxOutline((min + max) * 0.5f, max - min, 0.0f, thickness, color, rounding);

    public void Rectangle(Vector2 min, Vector2 max, float thickness, Vector3 color, float rounding = 0.0f) =>
        Rectangle(min, max, thickness, new Vector4(color, 1.0f), rounding);

    /// <summary>A filled disc.</summary>
    public void FillCircle(Vector2 center, float radius, Vector4 color) =>
        Add(ShapeInstance.Disc(center, radius, SpriteItem.PackColor(color)));

    public void FillCircle(Vector2 center, float radius, Vector3 color) => FillCircle(center, radius, new Vector4(color, 1.0f));

    /// <summary>A ring: the outline of a disc.</summary>
    public void Circle(Vector2 center, float radius, float thickness, Vector4 color) =>
        Add(ShapeInstance.Disc(center, radius, SpriteItem.PackColor(color), MathF.Max(thickness, 0.0001f)));

    public void Circle(Vector2 center, float radius, float thickness, Vector3 color) => Circle(center, radius, thickness, new Vector4(color, 1.0f));

    /// <summary>A line from one point to another, of a width, with round ends.</summary>
    public void Line(Vector2 from, Vector2 to, float width, Vector4 color) =>
        Add(ShapeInstance.Line(from, to, width, SpriteItem.PackColor(color)));

    public void Line(Vector2 from, Vector2 to, float width, Vector3 color) => Line(from, to, width, new Vector4(color, 1.0f));

    /// <summary>Lines from each point to the next, and back to the first if closed.</summary>
    public void Polyline(ReadOnlySpan<Vector2> points, float width, Vector4 color, bool closed = false)
    {
        uint packed = SpriteItem.PackColor(color);
        for (int i = 0; i + 1 < points.Length; i++)
            Add(ShapeInstance.Line(points[i], points[i + 1], width, packed));

        if (closed && points.Length > 2)
            Add(ShapeInstance.Line(points[^1], points[0], width, packed));
    }

    public void Polyline(ReadOnlySpan<Vector2> points, float width, Vector3 color, bool closed = false) =>
        Polyline(points, width, new Vector4(color, 1.0f), closed);

    /// <summary>A filled triangle standing on its base around a point, as wide and high as a size, its tip up at a rotation of 0.</summary>
    public void Triangle(Vector2 center, Vector2 size, float rotation, Vector4 color) =>
        Add(ShapeInstance.Triangle(center, size, rotation, SpriteItem.PackColor(color)));

    public void Triangle(Vector2 center, Vector2 size, float rotation, Vector3 color) => Triangle(center, size, rotation, new Vector4(color, 1.0f));

    /// <summary>The outline of a triangle.</summary>
    public void TriangleOutline(Vector2 center, Vector2 size, float rotation, float thickness, Vector4 color) =>
        Add(ShapeInstance.Triangle(center, size, rotation, SpriteItem.PackColor(color), MathF.Max(thickness, 0.0001f)));

    /// <summary>A line with a head at its end, pointing the way it goes. The head shrinks on an arrow too short for it.</summary>
    public void Arrow(Vector2 from, Vector2 to, float width, Vector4 color, float head = 14.0f)
    {
        Vector2 along = to - from;
        float length = along.Length();
        if (length <= 0.0f) return;

        float angle = MathF.Atan2(along.Y, along.X);
        head = MathF.Min(head, length * 0.4f);

        uint packed = SpriteItem.PackColor(color);
        Add(ShapeInstance.Line(from, to - along / length * head, width, packed));

        // The triangle points up at a rotation of 0 and the line's angle counts from pointing right, hence the quarter turn
        Add(ShapeInstance.Triangle(to - along / length * (head * 0.5f), new Vector2(head), angle - MathF.PI * 0.5f, packed));
    }

    public void Arrow(Vector2 from, Vector2 to, float width, Vector3 color, float head = 14.0f) =>
        Arrow(from, to, width, new Vector4(color, 1.0f), head);
}
