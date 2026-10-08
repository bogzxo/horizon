using Horizon.Rendering;
using System.Numerics;

using Horizon.UI.Components;

namespace Horizon.UI.Drawing;

/// <summary>
/// Draws the box of text that comes up next to the pointer when it rests on a component for a moment, see
/// <see cref="UIComponent.Tooltip"/>. The compositor decides when, this only draws it: a box in the colours of the
/// skin, just below and to the right of the pointer, pushed back onto the screen where it would hang off it.
/// </summary>
internal static class UITooltip
{
    // How wide a tooltip is at most before its text wraps, and the room around the text
    private const float MAX_WIDTH = 320.0f;
    private const float PADDING = 8.0f;

    // How far from the pointer the box sits, so the pointer doesn't cover its first word
    private static readonly Vector2 Offset = new(14.0f, -22.0f);

    /// <summary>
    /// Paints a tooltip into a list that has the transform of the module set, see <see cref="UIModule.Paint"/>.
    /// </summary>
    /// <param name="point">Where the pointer is, in the module's units.</param>
    /// <param name="lines">Scratch for cutting the text into lines, reused from one frame to the next.</param>
    public static void Paint(UIDrawList list, UIModule module, string text, Vector2 point, List<(int Start, int Length)> lines)
    {
        var skin = list.Skin;
        float scale = skin.TextScale * 0.75f;

        skin.Font.Wrap(text, scale, MAX_WIDTH, lines, markup: false);
        Vector2 size = skin.Font.Measure(text, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(lines), scale, markup: false) + new Vector2(PADDING * 2.0f);

        // Its top left corner hangs off the pointer, and the whole box stays inside the frame of the module
        UIRect frame = module.Frame;
        Vector2 corner = point + Offset;
        corner.X = MathF.Min(corner.X, frame.Max.X - size.X);
        corner.Y = MathF.Max(corner.Y, frame.Min.Y + size.Y);
        corner.X = MathF.Max(corner.X, frame.Min.X);
        corner.Y = MathF.Min(corner.Y, frame.Max.Y);

        var box = new UIRect(new Vector2(corner.X, corner.Y - size.Y), new Vector2(corner.X + size.X, corner.Y));

        list.Box(box, skin.PanelColor with { W = 1.0f });
        list.Frame(box, 1.0f, skin.AccentColor);
        list.Text(text, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(lines), box.Shrink(new UIEdges(PADDING)), Origin.TopLeft, scale, skin.TextColor, markup: false);
    }
}
