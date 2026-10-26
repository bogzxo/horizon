using System.Numerics;

namespace Horizon.UI.Drawing;

/// <summary>A run of a row of a minimap drawn as one bar, from a column to the one after its last, in its colour.</summary>
public readonly record struct UIMinimapBar(int From, int To, Vector4 Colour);

/// <summary>
/// What every minimap draws by, the code editor's and the terminal's. A row is a whole number of the screen's pixels tall
/// (two at 100% and 111%, three at 150%) and a character one pixel wide, everything sits on whole pixels, and the slider
/// over what's in sight is the text seen through. A row two units tall slid by a bit of a pixel every tick at 111%, and
/// the whole map flashed as it scrolled.
/// </summary>
public static class UIMinimap
{
    /// <summary>How tall a row is and how wide a character is, in units at 100%, before they're made whole pixels.</summary>
    public const float ROW = 2.0f, CHARACTER = 1.0f;

    /// <summary>How see-through the slider is, as it sits, and under the pointer or held.</summary>
    public const float SLIDER = 0.12f, SLIDER_LIT = 0.2f;

    /// <summary>How many of the screen's pixels a unit of a module is, 1.11 at a UI scale of 111%, 1 without a module.</summary>
    public static float PixelsPerUnit(UIModule? module) =>
        module is not null ? MathF.Max(0.01f, module.ToWorld(Vector2.UnitY).Y - module.ToWorld(Vector2.Zero).Y) : 1.0f;

    /// <summary>A length in units made a whole number of pixels, one at the least.</summary>
    public static float Whole(float units, float pixelsPerUnit) => MathF.Max(1.0f, MathF.Round(units * pixelsPerUnit)) / pixelsPerUnit;

    /// <summary>How tall a row is in units, a whole number of pixels.</summary>
    public static float Row(float pixelsPerUnit) => Whole(ROW, pixelsPerUnit);

    /// <summary>A place moved onto the nearest corner of the screen's pixels.</summary>
    public static Vector2 OnPixel(UIModule? module, Vector2 point) =>
        module is not null ? module.ToLocal(Vector2.Round(module.ToWorld(point))) : Vector2.Round(point);

    /// <summary>A rectangle with its corners on the screen's pixels.</summary>
    public static UIRect OnPixels(UIModule? module, UIRect area) => new(OnPixel(module, area.Min), OnPixel(module, area.Max));

    /// <summary>A height in units as it is on the screen, in pixels up from its bottom.</summary>
    public static float ScreenY(UIModule? module, float y) => module is not null ? module.ToWorld(new Vector2(0.0f, y)).Y : y;

    /// <summary>
    /// How many whole rows a map scrolls for a share of the way down what it shows, so it's at its top with the text at
    /// the top and at its bottom with the text at the bottom, none when all its rows fit.
    /// </summary>
    public static int ScrolledRows(int rows, float row, float height, float share)
    {
        int fit = (int)(height / row);
        return rows <= fit ? 0 : (int)MathF.Round((rows - fit) * Math.Clamp(share, 0.0f, 1.0f));
    }

    /// <summary>A gap of a pixel between rows once they're three or more pixels tall, two pixels with one of them gap is a dotted mess.</summary>
    public static float Gap(float row, float pixelsPerUnit) => MathF.Round(row * pixelsPerUnit) >= 3.0f ? 1.0f / pixelsPerUnit : 0.0f;

    /// <summary>One bar of a row, from a column to another, the columns past the most left out.</summary>
    public static void Bar(UIDrawList list, float left, float character, float rowTop, float row, float gap, int from, int to, int most, Vector4 colour)
    {
        to = Math.Min(to, most);
        if (to > from)
            list.Rect(new UIRect(new Vector2(left + from * character, rowTop - row + gap), new Vector2(left + to * character, rowTop)), colour);
    }

    /// <summary>The bars of a row, the columns past the most left out.</summary>
    public static void Bars(UIDrawList list, ReadOnlySpan<UIMinimapBar> bars, float left, float character, float rowTop, float row, float gap, int most)
    {
        foreach (UIMinimapBar bar in bars)
        {
            if (bar.From >= most)
                break;
            Bar(list, left, character, rowTop, row, gap, bar.From, bar.To, most, bar.Colour);
        }
    }

    /// <summary>
    /// The slider over what's in sight, its top on a pixel and its height a whole number of them so it doesn't swim over
    /// the rows under it, lighter under the pointer and while it's held. Where its top is, in units.
    /// </summary>
    public static float Slider(UIDrawList list, UIModule? module, UIRect map, float top, float height, Vector4 text, bool lit)
    {
        float pixels = PixelsPerUnit(module);
        float sliderTop = OnPixel(module, new Vector2(0.0f, top)).Y;
        var slider = new UIRect(new Vector2(map.Min.X, sliderTop - MathF.Round(height * pixels) / pixels), new Vector2(map.Max.X, sliderTop));
        list.Rect(slider, text with { W = lit ? SLIDER_LIT : SLIDER });
        return sliderTop;
    }
}
