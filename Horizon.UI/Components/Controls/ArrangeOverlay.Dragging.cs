using System.Numerics;

using Horizon.Engine;

namespace Horizon.UI.Components;

// The pointer on the overlay. A press inside the rectangle takes hold of all of it, one on a handle of that corner or
// edge, the wheel scales it round the pointer. Up is up, the top edge is the one with the bigger y as everywhere in the UI.
public sealed partial class ArrangeOverlay
{
    /// <summary>How far from a handle the pointer still has it, in the layout's units.</summary>
    public const float REACH = 10.0f;

    /// <summary>How much one notch of the wheel scales it by.</summary>
    public const float WHEEL_STEP = 1.1f;

    /// <summary>What the pointer has hold of right now, a handle's edges, all of them for the whole thing, none for nothing.</summary>
    public UIResize Held { get; private set; }

    /// <summary>Whether the rectangle is being dragged about whole right now.</summary>
    public bool Moving { get; private set; }

    /// <summary>The handle under the pointer while nothing is held, for painting it lit, and for tests.</summary>
    public UIResize Hovered { get; private set; }

    // The rectangle and the pointer as the press found them, a drag is from there
    private UIRect grabbed;
    private Vector2 grabbedAt;

    /// <summary>
    /// Which handle of the rectangle a point is on, a corner both of its edges. None in the middle or outside, a handle
    /// reaches a little past the edge so a small one can still be got hold of.
    /// </summary>
    public UIResize HandleAt(Vector2 point)
    {
        UIRect rect = Rect;
        if (rect.IsEmpty)
            return UIResize.None;

        // Never more than a third of the way in, or something small would be all handle and no middle to move it by
        float across = MathF.Min(REACH, rect.Width / 3.0f), down = MathF.Min(REACH, rect.Height / 3.0f);
        bool alongside = point.Y >= rect.Min.Y - REACH && point.Y <= rect.Max.Y + REACH;
        bool above = point.X >= rect.Min.X - REACH && point.X <= rect.Max.X + REACH;
        if (!alongside || !above)
            return UIResize.None;

        UIResize on = UIResize.None;
        if (point.X >= rect.Min.X - REACH && point.X <= rect.Min.X + across) on |= UIResize.Left;
        else if (point.X <= rect.Max.X + REACH && point.X >= rect.Max.X - across) on |= UIResize.Right;
        if (point.Y <= rect.Max.Y + REACH && point.Y >= rect.Max.Y - down) on |= UIResize.Top;
        else if (point.Y >= rect.Min.Y - REACH && point.Y <= rect.Min.Y + down) on |= UIResize.Bottom;

        // An edge is only a handle near its middle, the rest of it moves the whole thing like the inside does
        if (on is UIResize.Left or UIResize.Right && MathF.Abs(point.Y - rect.Center.Y) > REACH * 1.5f)
            return UIResize.None;
        if (on is UIResize.Top or UIResize.Bottom && MathF.Abs(point.X - rect.Center.X) > REACH * 1.5f)
            return UIResize.None;

        return on;
    }

    protected internal override void OnPointerDown(Vector2 point)
    {
        Focus();
        grabbed = Rect;
        grabbedAt = point;
        Held = HandleAt(point);
        Moving = Held == UIResize.None && Rect.Contains(point);
    }

    protected internal override void OnPointerDrag(Vector2 point)
    {
        Vector2 by = point - grabbedAt;
        if (Moving)
            Change(new UIRect(grabbed.Min + by, grabbed.Max + by));
        else if (Held != UIResize.None)
            Change(Resized(grabbed, Held, by, keepShape: !FreeShape));
    }

    protected internal override void OnPointerUp(Vector2 point)
    {
        Held = UIResize.None;
        Moving = false;
    }

    // The wheel over it scales it round the pointer, the point under the pointer staying under it
    protected internal override bool OnScroll(float delta)
    {
        if (Rect.IsEmpty || Module is not { } module)
            return true;

        float by = MathF.Pow(WHEEL_STEP, delta);
        if (MathF.Min(Rect.Width, Rect.Height) * by < LEAST)
            return true;

        Vector2 at = module.ToLocal(module.Compositor.Pointer.Position);
        Change(new UIRect(at + (Rect.Min - at) * by, at + (Rect.Max - at) * by));
        return true;
    }

    /// <summary>Whether a handle lets go of the shape the rectangle had, Alt held down.</summary>
    private static bool FreeShape => GameEngine.Instance?.Input.Keyboard.Alt == true;

    /// <summary>
    /// A rectangle with some of its edges moved by so much. Keeping its shape, a corner goes by whichever way it was
    /// pulled further, an edge brings the other way along round its middle, and the edges not pulled stay where they are.
    /// </summary>
    public static UIRect Resized(UIRect from, UIResize edges, Vector2 by, bool keepShape)
    {
        float left = from.Min.X, right = from.Max.X, bottom = from.Min.Y, top = from.Max.Y;
        if ((edges & UIResize.Left) != 0) left = MathF.Min(left + by.X, right - LEAST);
        if ((edges & UIResize.Right) != 0) right = MathF.Max(right + by.X, left + LEAST);
        if ((edges & UIResize.Bottom) != 0) bottom = MathF.Min(bottom + by.Y, top - LEAST);
        if ((edges & UIResize.Top) != 0) top = MathF.Max(top + by.Y, bottom + LEAST);

        if (!keepShape || from.Width <= 0.0f || from.Height <= 0.0f)
            return new UIRect(new Vector2(left, bottom), new Vector2(right, top));

        float shape = from.Width / from.Height, width = right - left, height = top - bottom;
        bool across = (edges & UIResize.Horizontal) != 0, upDown = (edges & UIResize.Vertical) != 0;
        if (across && upDown)
        {
            if (width / from.Width >= height / from.Height)
                height = width / shape;
            else
                width = height * shape;
        }
        else if (across)
        {
            height = width / shape;
        }
        else
        {
            width = height * shape;
        }

        // Kept from going under the least either way, the shape with it
        if (width < LEAST || height < LEAST)
        {
            float up = MathF.Max(LEAST / width, LEAST / height);
            width *= up;
            height *= up;
        }

        (left, right) = Spread(left, right, from.Min.X, from.Max.X, width, (edges & UIResize.Left) != 0, (edges & UIResize.Right) != 0);
        (bottom, top) = Spread(bottom, top, from.Min.Y, from.Max.Y, height, (edges & UIResize.Bottom) != 0, (edges & UIResize.Top) != 0);
        return new UIRect(new Vector2(left, bottom), new Vector2(right, top));
    }

    // One axis given its length, from the edge that stayed put, or round its middle when neither edge was pulled
    private static (float Low, float High) Spread(float low, float high, float wasLow, float wasHigh, float length, bool pulledLow, bool pulledHigh)
    {
        if (pulledLow)
            return (wasHigh - length, wasHigh);
        if (pulledHigh)
            return (wasLow, wasLow + length);

        float middle = (wasLow + wasHigh) * 0.5f;
        return (middle - length * 0.5f, middle + length * 0.5f);
    }

    // What's under the pointer, lit while nothing is held
    private void UpdateHover()
    {
        if (Module is not { } module)
            return;

        Hovered = Held != UIResize.None || Moving ? Held : HandleAt(module.ToLocal(module.Compositor.Pointer.Position));
    }
}
