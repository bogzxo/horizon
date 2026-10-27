using Horizon.Content;
using Horizon.Rendering;
using System.Numerics;

using Horizon.UI.Components;
using Horizon.UI.Skinning;

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

    // How far from the pointer the box sits, so the pointer doesn't cover its first word, how far over the pointer its
    // bottom is when it goes over it, and how far in from the edges of the window it stays
    private static readonly Vector2 Offset = new(14.0f, -22.0f);
    private const float ABOVE = 24.0f, MARGIN = 8.0f;

    // How wide the widest word of a text is
    private static float LongestWord(UISkin skin, string text, float scale)
    {
        float widest = 0.0f;
        foreach (string word in text.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries))
            widest = MathF.Max(widest, skin.Font.Measure(word, scale, markup: false).X);
        return widest;
    }

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
            // As wide as its longest word when that's wider than a tooltip usually is, so a word is never broken in two (a
            // path came out as "RE/ADME.md"), never wider than the frame leaves
            float most = MathF.Max(MAX_WIDTH, MathF.Min(LongestWord(skin, text, scale), module.Frame.Width - MARGIN * 2.0f - PADDING * 2.0f));
            skin.Font.Wrap(text, scale, most, lines, markup: false);
            size = skin.Font.Measure(text, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(lines), scale, markup: false) + new Vector2(PADDING * 2.0f);
        }

        // Its top left corner hangs off the pointer, under it, or over it when there's no room under it (a status bar's
        // tooltip was pushed up over the item it was about). The whole box stays inside the frame of the module with a
        // margin, one flush with the window's edges lost its round corners there
        UIRect frame = module.Frame;
        Vector2 corner = point + Offset;
        if (corner.Y - size.Y < frame.Min.Y + MARGIN)
            corner.Y = point.Y + ABOVE + size.Y;
        corner.X = MathF.Min(corner.X, frame.Max.X - MARGIN - size.X);
        corner.Y = MathF.Max(corner.Y, frame.Min.Y + MARGIN + size.Y);
        corner.X = MathF.Max(corner.X, frame.Min.X + MARGIN);
        corner.Y = MathF.Min(corner.Y, frame.Max.Y - MARGIN);

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
