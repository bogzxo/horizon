using Horizon.Content;
using Horizon.Rendering;
using System.Numerics;

using Horizon.UI.Components;

namespace Horizon.UI.Drawing;

/// <summary>
/// Draws the box of text that comes up next to the pointer when it rests on a component for a moment, see
/// <see cref="UIComponent.Tooltip"/>. The compositor decides when, this only draws it, a box in the colours of the
/// skin, just below and to the right of the pointer, pushed back onto the screen where it would hang off it.
/// </summary>
internal static class UITooltip
{
    // How wide a tooltip is at most before its text wraps, and the room around the text
    private const float MAX_WIDTH = 320.0f;
    private const float MAX_RICH_WIDTH = 520.0f;
    private const float PADDING = 10.0f;

    // How far from the pointer the box sits, so the pointer doesn't cover its first word
    private static readonly Vector2 Offset = new(14.0f, -22.0f);

    /// <summary>
    /// Paints a tooltip into a list that has the transform of the module set, see <see cref="UIModule.Paint"/>.
    /// </summary>
    /// <param name="point">Where the pointer is, in the module's units.</param>
    /// <param name="lines">Scratch for cutting the text into lines, reused from one frame to the next.</param>
    /// <param name="rich">Where a tooltip that is markdown is laid out, null for one that is plain words.</param>
    /// <param name="openedAt">When it was first painted, for its entrance, less than 0 for not yet.</param>
    public static void Paint(UIDrawList list, UIModule module, string text, Vector2 point, List<(int Start, int Length)> lines, UIRichText? rich, ref float openedAt)
    {
        var skin = list.Skin;

        // The size of what the skin writes, a tooltip in small print was the hardest thing in the UI to read
        float scale = skin.TextScale;
        text = module.Compositor.Resolve(text);

        Vector2 size;
        if (rich is not null)
        {
            rich.Build(text, skin.Font, scale, MAX_RICH_WIDTH, markdown: true);
            size = new Vector2(rich.Width, rich.Height) + new Vector2(PADDING * 2.0f);
        }
        else
        {
            skin.Font.Wrap(text, scale, MAX_WIDTH, lines, markup: false);
            size = skin.Font.Measure(text, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(lines), scale, markup: false) + new Vector2(PADDING * 2.0f);
        }

        // Its top left corner hangs off the pointer, and the whole box stays inside the frame of the module
        UIRect frame = module.Frame;
        Vector2 corner = point + Offset;
        corner.X = MathF.Min(corner.X, frame.Max.X - size.X);
        corner.Y = MathF.Max(corner.Y, frame.Min.Y + size.Y);
        corner.X = MathF.Max(corner.X, frame.Min.X);
        corner.Y = MathF.Min(corner.Y, frame.Max.Y);

        var box = new UIRect(new Vector2(corner.X, corner.Y - size.Y), new Vector2(corner.X + size.X, corner.Y));

        // Faded in out of the corner it hangs from, on what every popup sits on
        bool entering = list.BeginEntrance(ref openedAt, new Vector2(box.Min.X, box.Max.Y), new Vector2(0.0f, 4.0f));
        list.Popup(box, module.Compositor.PopupGlass);

        if (rich is not null)
            rich.Paint(list, box.Shrink(new UIEdges(PADDING)), Origin.TopLeft, skin.TextColor);
        else
            list.Text(text, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(lines), box.Shrink(new UIEdges(PADDING)), Origin.TopLeft, scale, skin.TextColor, markup: false);

        if (entering)
            list.EndEntrance();
    }
}
