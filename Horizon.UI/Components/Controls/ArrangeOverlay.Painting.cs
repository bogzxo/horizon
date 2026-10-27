using System.Numerics;

using Horizon.UI.Drawing;

namespace Horizon.UI.Components;

// How the overlay looks. Everything round the rectangle dimmed, so it's plain which mode this is and what's being moved,
// the picture in it, a frame in the accent and a handle on every corner and edge, the one under the pointer lit.
public sealed partial class ArrangeOverlay
{
    /// <summary>How dark everything round the rectangle goes.</summary>
    public Vector4 Veil { get; set; } = new(0.0f, 0.0f, 0.0f, 0.5f);

    // Its parts, so the quads of one aren't blended with another's between two ticks
    private const int VEIL_PART = 1, PICTURE_PART = 2, FRAME_PART = 3, HANDLES_PART = 4;

    // How big a handle is drawn, and how thick the frame
    private const float HANDLE = 10.0f, FRAME = 2.0f;

    // Where the eight handles are, as shares of the rectangle from its bottom left, with the edges each one pulls
    private static readonly (float X, float Y, UIResize Edges)[] Handles =
    [
        (0.0f, 0.0f, UIResize.Left | UIResize.Bottom), (0.5f, 0.0f, UIResize.Bottom), (1.0f, 0.0f, UIResize.Right | UIResize.Bottom),
        (0.0f, 0.5f, UIResize.Left), (1.0f, 0.5f, UIResize.Right),
        (0.0f, 1.0f, UIResize.Left | UIResize.Top), (0.5f, 1.0f, UIResize.Top), (1.0f, 1.0f, UIResize.Right | UIResize.Top)
    ];

    protected override void Paint(UIDrawList list)
    {
        UIRect rect = Rect, all = Bounds;

        list.BeginPart(VEIL_PART);
        PaintVeil(list, all, rect.IsEmpty ? default : rect.Intersect(all));

        if (rect.IsEmpty)
        {
            list.EndParts();
            return;
        }

        if (Picture is { IsValid: true } picture && PictureOpacity > 0.0f)
        {
            list.BeginPart(PICTURE_PART);
            list.PushClip(Room ?? all);
            list.Image(picture, rect, new Vector4(1.0f, 1.0f, 1.0f, PictureOpacity));
            list.PopClip();
        }

        // A dark line outside the frame so it shows on a light picture as well as a dark one
        Vector4 accent = list.Skin.AccentColor with { W = 1.0f };
        list.BeginPart(FRAME_PART);
        list.RoundOutline(new UIRect(rect.Min - Vector2.One, rect.Max + Vector2.One), 0.0f, FRAME + 2.0f, new Vector4(0.0f, 0.0f, 0.0f, 0.5f));
        list.RoundOutline(rect, 0.0f, FRAME, accent);

        list.BeginPart(HANDLES_PART);
        foreach (var (x, y, edges) in Handles)
        {
            Vector2 at = new(rect.Min.X + rect.Width * x, rect.Min.Y + rect.Height * y);
            UIRect handle = UIRect.FromCenter(at, new Vector2(HANDLE));
            bool lit = Hovered == edges;
            list.RoundRect(new UIRect(handle.Min - Vector2.One, handle.Max + Vector2.One), 3.0f, new Vector4(0.0f, 0.0f, 0.0f, 0.55f));
            list.RoundRect(handle, 2.0f, lit ? accent : new Vector4(1.0f, 1.0f, 1.0f, 1.0f));
            list.RoundOutline(handle, 2.0f, 1.5f, accent);
        }

        list.EndParts();
    }

    // The veil in four strips round a hole, above and below it the whole way across and the two sides between
    private void PaintVeil(UIDrawList list, UIRect all, UIRect hole)
    {
        if (hole.IsEmpty)
        {
            list.Rect(all, Veil);
            return;
        }

        list.Rect(new UIRect(new Vector2(all.Min.X, hole.Max.Y), all.Max), Veil);
        list.Rect(new UIRect(all.Min, new Vector2(all.Max.X, hole.Min.Y)), Veil);
        list.Rect(new UIRect(new Vector2(all.Min.X, hole.Min.Y), new Vector2(hole.Min.X, hole.Max.Y)), Veil);
        list.Rect(new UIRect(new Vector2(hole.Max.X, hole.Min.Y), new Vector2(all.Max.X, hole.Max.Y)), Veil);
    }
}
