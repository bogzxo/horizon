using System.Numerics;

using Horizon.Graphics;

namespace Horizon.UI.Components;

// Where the picture behind the code goes and back again. Its place is kept as shares of the window, so it keeps its size
// against the window as that's resized, which is how a program saves it, and a rectangle somebody dragged it to by hand
// (an ArrangeOverlay) is turned back into those shares here.
public partial class CodeEditor
{
    /// <summary>
    /// How wide the picture is drawn against its own shape, 1 for the shape it has, 2 for twice as wide as that. Somebody
    /// sizing it by hand with its shape let go of.
    /// </summary>
    public float BackdropStretch { get; set; } = 1.0f;

    /// <summary>
    /// The room the picture is placed in as it was last drawn, the text area, where its corner goes from. Null before the
    /// editor's been drawn at all.
    /// </summary>
    public UIRect? BackdropRoom => paintedSkin is { } skin ? TextArea(skin, paintedScale) : null;

    /// <summary>Where the picture is drawn now, by its place as it is and the room as it was last drawn. Null for no picture or no room yet.</summary>
    public UIRect? BackdropPlaced => Backdrop is { IsValid: true } picture && BackdropRoom is { } room
        ? PlaceBackdrop(room, Module?.Frame ?? room, picture, BackdropScale, BackdropOffset, BackdropStretch)
        : null;

    /// <summary>The same as the plain one, the picture's width stretched by so much against its own shape.</summary>
    public static UIRect PlaceBackdrop(UIRect area, UIRect window, Texture picture, float scale, Vector2 offset, float stretch)
    {
        UIRect placed = PlaceBackdrop(area, window, picture, scale, offset);
        if (stretch == 1.0f)
            return placed;

        // It stays against the same corner, the bottom right, and grows to the left
        return new UIRect(new Vector2(placed.Max.X - placed.Width * stretch, placed.Min.Y), placed.Max);
    }

    /// <summary>
    /// The size, the offset and the stretch that put a picture at a rectangle, the other way round from
    /// <see cref="PlaceBackdrop(UIRect, UIRect, Texture, float, Vector2, float)"/>. For a picture somebody placed by hand,
    /// to keep as shares of the window.
    /// </summary>
    public static (float Scale, Vector2 Offset, float Stretch) BackdropFrom(UIRect placed, UIRect area, UIRect window, Texture picture)
    {
        float scale = window.Height > 0.0f ? placed.Height / window.Height : 0.0f;
        Vector2 offset = new(
            window.Width > 0.0f ? (area.Max.X - placed.Max.X) / window.Width : 0.0f,
            window.Height > 0.0f ? (placed.Min.Y - area.Min.Y) / window.Height : 0.0f);
        float natural = picture.Height > 0 && placed.Height > 0.0f ? placed.Height * picture.Width / picture.Height : 0.0f;
        float stretch = natural > 0.0f ? placed.Width / natural : 1.0f;
        return (scale, offset, stretch);
    }
}
