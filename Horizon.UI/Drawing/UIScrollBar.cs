using System.Numerics;

namespace Horizon.UI.Drawing;

/// <summary>
/// What every scroll bar is drawn by, ScrollPanel's, the code editor's, the terminal's, the lists' and the trees'. No
/// track to speak of and a thumb in the text's colour seen through, brighter and a bit thicker under the pointer and
/// brighter still while it's held, a pill for a skin with round corners. They were five bars of four widths in three
/// shades and the terminal's grew to twelve pixels under the pointer while the editor's stayed at four.
/// </summary>
public static class UIScrollBar
{
    /// <summary>How thick a bar is as it sits, in units, and how much thicker it gets under the pointer.</summary>
    public const float WIDTH = 6.0f, LIT_GROWTH = 1.5f;

    /// <summary>The least a thumb is, so a long text still has something to take hold of.</summary>
    public const float MIN_THUMB = 24.0f;

    /// <summary>How see-through the thumb is as it sits, under the pointer and held, and the track while it's lit.</summary>
    public const float THUMB = 0.2f, THUMB_LIT = 0.32f, THUMB_HELD = 0.5f, TRACK_LIT = 0.05f;

    /// <summary>How thick a bar is drawn for how lit it is, 0 sitting there and 1 under the pointer, never past the most.</summary>
    public static float Width(float rest, float lit, float most = float.MaxValue) =>
        MathF.Min(most, rest + (rest * LIT_GROWTH - rest) * Math.Clamp(lit, 0.0f, 1.0f));

    /// <summary>
    /// Paints a bar against the right edge of its column, the track as tall as the column and the thumb from its top down
    /// by its length. The track is painted see-through rather than left out while it isn't lit, so the quads don't shift as
    /// it lights.
    /// </summary>
    /// <param name="right">The right edge of the bar, it grows to the left.</param>
    /// <param name="width">How thick it's drawn, see <see cref="Width"/>.</param>
    /// <param name="lit">How lit it is, 0 to 1, eased by whoever has it or straight on and off.</param>
    public static void Paint(UIDrawList list, float right, float bottom, float top, float thumbTop, float thumbLength, float width, float lit, bool held, Vector4 ink)
    {
        lit = Math.Clamp(lit, 0.0f, 1.0f);
        float round = list.Skin.CornerRadius > 0.0f ? width * 0.5f : 0.0f;
        float left = right - width;

        list.RoundRect(new UIRect(new Vector2(left, bottom), new Vector2(right, top)), round, ink with { W = ink.W * TRACK_LIT * lit });
        float alpha = held ? THUMB_HELD : THUMB + (THUMB_LIT - THUMB) * lit;
        list.RoundRect(new UIRect(new Vector2(left, thumbTop - thumbLength), new Vector2(right, thumbTop)), round, ink with { W = ink.W * alpha });
    }

    /// <summary>A tick across a bar's track, what was found or what's wrong at a share of the way down, VS Code's overview ruler.</summary>
    public static void Tick(UIDrawList list, float left, float right, float bottom, float top, float share, float height, Vector4 colour)
    {
        float middle = top - (top - bottom) * Math.Clamp(share, 0.0f, 1.0f);
        float half = height * 0.5f;
        middle = Math.Clamp(middle, bottom + half, top - half);
        list.Rect(new UIRect(new Vector2(left, middle - half), new Vector2(right, middle + half)), colour);
    }
}
