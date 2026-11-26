using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// Picks a colour. A field of every shade of one hue across the top (more colourful to the right, brighter up), and
/// under it a slider of the hues and, unless told not to, one for how see-through the colour is, each with a round
/// handle. Beside the sliders the colour as it is now next to what it was (<see cref="Original"/>), a press on the old
/// half puts it back. Press or drag anywhere in them, the pointer stays with what it was pressed on.
/// </summary>
public class ColorPicker : UIComponent
{
    private const float DEFAULT_WIDTH = 360.0f;
    private const float DEFAULT_HEIGHT = 230.0f;

    // How tall a slider is, the room between the parts, and how wide the comparing swatch is
    private const float SLIDER = 12.0f;
    private const float GAP = 10.0f;
    private const float COMPARE_WIDTH = 52.0f;

    // The draw list only does flat colours, so a gradient is cells this many pixels across, small enough to read as smooth
    private const float CELL = 3.0f;

    // How round the corners of the field, the sliders and the swatch are
    private const float RADIUS = 3.0f;

    // How big the handles are across
    private const float FIELD_HANDLE = 14.0f;
    private const float SLIDER_HANDLE = 16.0f;

    private enum Zone
    {
        None,
        Shade,
        Hue,
        Alpha,
        Original
    }

    private IRuntimeValue? changedHandler;

    // Kept as hue, saturation and value so the hue isn't lost on the way through black, white and grey
    private float hue, saturation = 0.0f, brightness = 1.0f, alpha = 1.0f;
    private Zone held;

    /// <summary>The colour that is picked, red, green, blue and alpha from 0 to 1.</summary>
    public Vector4 Color
    {
        get => new(ToRgb(hue, saturation, brightness), alpha);
        set
        {
            // The same colour back leaves the hue alone, it may well be one this colour can't tell
            if (Vector4.DistanceSquared(value, Color) < 1e-7f)
                return;

            var (h, s, v) = ToHsv(new Vector3(value.X, value.Y, value.Z));

            if (s > 0.0f && v > 0.0f)
                hue = h;

            saturation = s;
            brightness = v;
            alpha = Math.Clamp(value.W, 0.0f, 1.0f);
        }
    }

    /// <summary>
    /// The colour it started from, shown beside the one picked so the two can be compared, and put back when its half is
    /// pressed. Null for a picker that has nothing to compare with, the swatch is then the colour alone.
    /// </summary>
    public Vector4? Original { get; set; }

    /// <summary>Whether there is a slider for the alpha. Without one it stays whatever it was set to.</summary>
    public bool ShowAlpha { get; set; } = true;

    /// <summary>Called with the new colour whenever the pointer changes it.</summary>
    public Action<Vector4>? OnChanged { get; set; }

    protected override bool HitTestVisible => true;

    protected override Vector2 Measure(UISkin skin) => new(DEFAULT_WIDTH, DEFAULT_HEIGHT);

    /* Where everything is. The layout's y goes up, so the field is at the top and the sliders under it */

    // How tall the band of sliders is, one or two of them with a gap between
    private float SlidersHeight => ShowAlpha ? SLIDER * 2.0f + GAP : SLIDER;

    // The band under the field, as tall as the sliders or the swatch, whichever is taller
    private float BandHeight => MathF.Max(SlidersHeight, 28.0f);

    /// <summary>The field of shades, saturation across and brightness up.</summary>
    public UIRect ShadeBounds => new(new Vector2(Bounds.Min.X, Bounds.Min.Y + BandHeight + GAP), Bounds.Max);

    // The band the sliders and the swatch share, under the field
    private UIRect Band => new(Bounds.Min, new Vector2(Bounds.Max.X, Bounds.Min.Y + BandHeight));

    // Where the sliders go, the band less the swatch on its right, the handles' half widths in from each end
    private UIRect Sliders
    {
        get
        {
            UIRect band = Band;
            float inset = SLIDER_HANDLE * 0.5f;
            return new UIRect(new Vector2(band.Min.X + inset, band.Min.Y), new Vector2(band.Max.X - COMPARE_WIDTH - GAP - inset, band.Max.Y));
        }
    }

    /// <summary>The slider of hues, red at the left and all the way round to red again at the right.</summary>
    public UIRect HueBounds
    {
        get
        {
            UIRect sliders = Sliders;
            float top = sliders.Center.Y + SlidersHeight * 0.5f;
            return new UIRect(new Vector2(sliders.Min.X, top - SLIDER), new Vector2(sliders.Max.X, top));
        }
    }

    /// <summary>The slider of the alpha, see-through at the left, under the hues.</summary>
    public UIRect AlphaBounds
    {
        get
        {
            UIRect hues = HueBounds;
            return new UIRect(new Vector2(hues.Min.X, hues.Min.Y - GAP - SLIDER), new Vector2(hues.Max.X, hues.Min.Y - GAP));
        }
    }

    /// <summary>The swatch, the colour picked and, when there's one, the original beside it.</summary>
    public UIRect SwatchBounds
    {
        get
        {
            UIRect band = Band;
            return new UIRect(new Vector2(band.Max.X - COMPARE_WIDTH, band.Min.Y), band.Max);
        }
    }

    // The original's half of the swatch, its right half
    private UIRect OriginalBounds
    {
        get
        {
            UIRect swatch = SwatchBounds;
            return new UIRect(new Vector2(swatch.Center.X, swatch.Min.Y), swatch.Max);
        }
    }

    protected override long PaintVersion
    {
        get
        {
            var hash = new HashCode();
            hash.Add(hue);
            hash.Add(saturation);
            hash.Add(brightness);
            hash.Add(alpha);
            hash.Add(ShowAlpha);
            hash.Add(Original);
            return hash.ToHashCode() & 0x7FFFFFFF;
        }
    }

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;
        Vector4 tint = EnabledInHierarchy ? Vector4.One : skin.DisabledTint;
        Vector3 solid = ToRgb(hue, saturation, brightness);
        Vector4 edge = skin.BorderColor.W > 0.0f ? skin.BorderColor : skin.TextColor * new Vector4(1, 1, 1, 0.2f);

        // The field, its corners rounded by the frame drawn over the cells in the colour of what's behind it
        UIRect shade = ShadeBounds;
        PaintShadeCells(list, shade, hue, tint);
        Rounded(list, shade, edge);

        // The hues, then the alpha over a checkerboard in the colour picked
        UIRect hues = HueBounds;
        PaintHueCells(list, hues, tint);
        Rounded(list, hues, edge);
        SliderHandle(list, hues, hue, new Vector4(ToRgb(hue, 1.0f, 1.0f), 1.0f) * tint);

        if (ShowAlpha)
        {
            UIRect alphas = AlphaBounds;
            Checkers(list, alphas, tint);
            PaintAlphaCells(list, alphas, solid, tint);
            Rounded(list, alphas, edge);
            SliderHandle(list, alphas, alpha, new Vector4(solid, alpha) * tint);
        }

        // Where in the field the colour is, a ring filled with it, dark round the outside so it shows on white
        Vector2 at = shade.Min + new Vector2(saturation, brightness) * shade.Size;
        UIRect ring = UIRect.FromCenter(at, new Vector2(FIELD_HANDLE));
        list.RoundRect(By(ring, 1.0f), FIELD_HANDLE * 0.5f + 1.0f, new Vector4(0, 0, 0, 0.45f));
        list.RoundRect(ring, FIELD_HANDLE * 0.5f, Vector4.One);
        list.RoundRect(By(ring, -2.5f), FIELD_HANDLE * 0.5f - 2.5f, new Vector4(solid, 1.0f) * tint);

        // The colour now, and what it was beside it
        UIRect swatch = SwatchBounds;
        Checkers(list, swatch, tint);
        if (Original is { } was)
        {
            UIRect now = new(swatch.Min, new Vector2(swatch.Center.X, swatch.Max.Y));
            list.Rect(now, new Vector4(solid, alpha) * tint);
            list.Rect(OriginalBounds, was * tint);
        }
        else
        {
            list.Rect(swatch, new Vector4(solid, alpha) * tint);
        }

        Rounded(list, swatch, edge);
    }

    // Helper method to paint the saturation/brightness field as flat cells without delegate or closure allocations
    private static void PaintShadeCells(UIDrawList list, UIRect area, float hue, Vector4 tint)
    {
        int across = Math.Max(1, (int)MathF.Ceiling(area.Width / CELL));
        int up = area.Height <= SLIDER ? 1 : Math.Max(1, (int)MathF.Ceiling(area.Height / CELL));
        Vector2 cell = new(area.Width / across, area.Height / up);
        Vector2 overlap = cell + new Vector2(0.5f);
        float invAcross = across == 1 ? 0.0f : 1.0f / (across - 1.0f);
        float invUp = up == 1 ? 0.0f : 1.0f / (up - 1.0f);

        for (int y = 0; y < up; y++)
        {
            float v = up == 1 ? 0.5f : y * invUp;
            float minY = area.Min.Y + y * cell.Y;
            for (int x = 0; x < across; x++)
            {
                float s = across == 1 ? 0.5f : x * invAcross;
                var min = new Vector2(area.Min.X + x * cell.X, minY);
                list.Rect(new UIRect(min, min + overlap), new Vector4(ToRgb(hue, s, v), 1.0f) * tint);
            }
        }
    }

    // Helper method to paint the horizontal hue strip as flat cells without delegate or closure allocations
    private static void PaintHueCells(UIDrawList list, UIRect area, Vector4 tint)
    {
        int across = Math.Max(1, (int)MathF.Ceiling(area.Width / CELL));
        float cellWidth = area.Width / across;
        float invAcross = across == 1 ? 0.0f : 1.0f / (across - 1.0f);
        float maxY = area.Max.Y + 0.5f;

        for (int x = 0; x < across; x++)
        {
            float h = across == 1 ? 0.5f : x * invAcross;
            float minX = area.Min.X + x * cellWidth;
            list.Rect(new UIRect(new Vector2(minX, area.Min.Y), new Vector2(minX + cellWidth + 0.5f, maxY)), new Vector4(ToRgb(h, 1.0f, 1.0f), 1.0f) * tint);
        }
    }

    // Helper method to paint the horizontal alpha strip as flat cells without delegate or closure allocations
    private static void PaintAlphaCells(UIDrawList list, UIRect area, Vector3 solid, Vector4 tint)
    {
        int across = Math.Max(1, (int)MathF.Ceiling(area.Width / CELL));
        float cellWidth = area.Width / across;
        float invAcross = across == 1 ? 0.0f : 1.0f / (across - 1.0f);
        float maxY = area.Max.Y + 0.5f;

        for (int x = 0; x < across; x++)
        {
            float a = across == 1 ? 0.5f : x * invAcross;
            float minX = area.Min.X + x * cellWidth;
            list.Rect(new UIRect(new Vector2(minX, area.Min.Y), new Vector2(minX + cellWidth + 0.5f, maxY)), new Vector4(solid, a) * tint);
        }
    }

    // A thin rounded frame round an area, so the cells' square corners read as the edge of a part
    private static void Rounded(UIDrawList list, UIRect area, Vector4 edge, float radius = RADIUS) =>
        list.RoundOutline(area, MathF.Min(radius, MathF.Min(area.Width, area.Height) * 0.5f), 1.0f, edge);

    // An area grown by so much each way, or shrunk for less than nothing
    private static UIRect By(UIRect area, float much) => new(area.Min - new Vector2(much), area.Max + new Vector2(much));

    // A slider's handle, a white ring filled with what it picks, at a fraction of the way along
    private static void SliderHandle(UIDrawList list, UIRect slider, float at, Vector4 fill)
    {
        Vector2 middle = new(slider.Min.X + at * slider.Width, slider.Center.Y);
        UIRect handle = UIRect.FromCenter(middle, new Vector2(SLIDER_HANDLE));
        list.RoundRect(By(handle, 1.0f), SLIDER_HANDLE * 0.5f + 1.0f, new Vector4(0, 0, 0, 0.45f));
        list.RoundRect(handle, SLIDER_HANDLE * 0.5f, Vector4.One);
        Checkers(list, By(handle, -3.0f), Vector4.One, 3.0f);
        list.RoundRect(By(handle, -3.0f), SLIDER_HANDLE * 0.5f - 3.0f, fill);
        list.RoundOutline(By(handle, -2.0f), SLIDER_HANDLE * 0.5f - 2.0f, 1.5f, Vector4.One);
    }

    private static void Checkers(UIDrawList list, UIRect area, Vector4 tint, float size = 6.0f)
    {
        list.Rect(area, new Vector4(0.8f, 0.8f, 0.8f, 1.0f) * tint);

        int row = 0;
        for (float y = area.Min.Y; y < area.Max.Y; y += size, row++)
        {
            for (float x = area.Min.X + (row % 2) * size; x < area.Max.X; x += size * 2.0f)
            {
                var max = new Vector2(MathF.Min(x + size, area.Max.X), MathF.Min(y + size, area.Max.Y));
                list.Rect(new UIRect(new Vector2(x, y), max), new Vector4(0.55f, 0.55f, 0.55f, 1.0f) * tint);
            }
        }
    }

    protected internal override bool KeepsTouchDrag(Vector2 point) => true;

    protected internal override void OnPointerDown(Vector2 point)
    {
        // Whatever was pressed on keeps the pointer until it is let go of, wherever it wanders off to. The sliders are
        // a little taller to press than they're drawn, a handle hangs over their edges
        float reach = (SLIDER_HANDLE - SLIDER) * 0.5f;
        held =
            ShadeBounds.Contains(point) ? Zone.Shade
            : By(HueBounds, reach).Contains(point) ? Zone.Hue
            : ShowAlpha && By(AlphaBounds, reach).Contains(point) ? Zone.Alpha
            : Original is not null && OriginalBounds.Contains(point) ? Zone.Original
            : Zone.None;

        MoveTo(point);
    }

    protected internal override void OnPointerDrag(Vector2 point)
    {
        if (held != Zone.Original)
            MoveTo(point);
    }

    protected internal override void OnPointerUp(Vector2 point) => held = Zone.None;

    private void MoveTo(Vector2 point)
    {
        Vector4 before = Color;
        float hueBefore = hue;

        switch (held)
        {
            case Zone.Shade:
                UIRect shade = ShadeBounds;
                saturation = shade.Width > 0.0f ? Math.Clamp((point.X - shade.Min.X) / shade.Width, 0.0f, 1.0f) : 0.0f;
                brightness = shade.Height > 0.0f ? Math.Clamp((point.Y - shade.Min.Y) / shade.Height, 0.0f, 1.0f) : 0.0f;
                break;

            case Zone.Hue:
                // Just short of all the way round, which would be red again and read back as the left end
                UIRect hues = HueBounds;
                hue = hues.Width > 0.0f ? Math.Clamp((point.X - hues.Min.X) / hues.Width, 0.0f, 0.999f) : 0.0f;
                break;

            case Zone.Alpha:
                UIRect alphas = AlphaBounds;
                alpha = alphas.Width > 0.0f ? Math.Clamp((point.X - alphas.Min.X) / alphas.Width, 0.0f, 1.0f) : 0.0f;
                break;

            case Zone.Original when Original is { } was:
                Color = was;
                break;

            default:
                return;
        }

        if (Color != before || hue != hueBefore)
            Changed();
    }

    /// <summary>Tells whoever listens that the colour changed, for a change made from outside (a value typed) as well.</summary>
    public void Changed()
    {
        OnChanged?.Invoke(Color);
        InvokeScript(changedHandler, new Vector4Value(Color));
    }

    /* Colours */

    /// <summary>Turns a hue (0 to 1 around the wheel), a saturation and a value into red, green and blue.</summary>
    public static Vector3 ToRgb(float hue, float saturation, float value)
    {
        float sector = (hue - MathF.Floor(hue)) * 6.0f;
        float chroma = value * saturation;
        float second = chroma * (1.0f - MathF.Abs(sector % 2.0f - 1.0f));
        float floor = value - chroma;

        Vector3 colour = (int)sector switch
        {
            0 => new Vector3(chroma, second, 0.0f),
            1 => new Vector3(second, chroma, 0.0f),
            2 => new Vector3(0.0f, chroma, second),
            3 => new Vector3(0.0f, second, chroma),
            4 => new Vector3(second, 0.0f, chroma),
            _ => new Vector3(chroma, 0.0f, second)
        };

        return colour + new Vector3(floor);
    }

    /// <summary>Turns red, green and blue into a hue (0 to 1 around the wheel), a saturation and a value.</summary>
    public static (float Hue, float Saturation, float Value) ToHsv(Vector3 colour)
    {
        colour = Vector3.Clamp(colour, Vector3.Zero, Vector3.One);

        float max = MathF.Max(colour.X, MathF.Max(colour.Y, colour.Z));
        float min = MathF.Min(colour.X, MathF.Min(colour.Y, colour.Z));
        float chroma = max - min;

        float hue = 0.0f;
        if (chroma > 0.0f)
        {
            if (max == colour.X)
                hue = ((colour.Y - colour.Z) / chroma + 6.0f) % 6.0f;
            else if (max == colour.Y)
                hue = (colour.Z - colour.X) / chroma + 2.0f;
            else
                hue = (colour.X - colour.Y) / chroma + 4.0f;
        }

        return (hue / 6.0f, max > 0.0f ? chroma / max : 0.0f, max);
    }

    private const string HEX_DIGITS = "0123456789ABCDEF";

    /// <summary>Formats a colour as #RRGGBB (or #RRGGBBAA when not opaque) into <paramref name="destination"/> without allocating.</summary>
    public static bool TryFormatHex(Vector4 colour, Span<char> destination, out int written)
    {
        static int Byte(float value) => (int)MathF.Round(Math.Clamp(value, 0.0f, 1.0f) * 255.0f);

        int r = Byte(colour.X);
        int g = Byte(colour.Y);
        int b = Byte(colour.Z);
        int a = Byte(colour.W);
        int needed = a == 255 ? 7 : 9;
        if (destination.Length < needed)
        {
            written = 0;
            return false;
        }

        destination[0] = '#';
        destination[1] = HEX_DIGITS[r >> 4];
        destination[2] = HEX_DIGITS[r & 0xF];
        destination[3] = HEX_DIGITS[g >> 4];
        destination[4] = HEX_DIGITS[g & 0xF];
        destination[5] = HEX_DIGITS[b >> 4];
        destination[6] = HEX_DIGITS[b & 0xF];
        if (needed == 9)
        {
            destination[7] = HEX_DIGITS[a >> 4];
            destination[8] = HEX_DIGITS[a & 0xF];
        }

        written = needed;
        return true;
    }

    /// <summary>A colour as #RRGGBB, with AA on the end when it isn't opaque.</summary>
    public static string ToHex(Vector4 colour)
    {
        Span<char> buffer = stackalloc char[9];
        TryFormatHex(colour, buffer, out int written);
        return new string(buffer[..written]);
    }

    /// <summary>A colour out of #RGB, #RRGGBB or #RRGGBBAA (the # can be left off), null for text that isn't one.</summary>
    public static Vector4? FromHex(string text) => text is null ? null : FromHex(text.AsSpan());

    /// <summary>A colour out of #RGB, #RRGGBB or #RRGGBBAA (the # can be left off), null for text that isn't one. Makes no allocations.</summary>
    public static Vector4? FromHex(ReadOnlySpan<char> text)
    {
        ReadOnlySpan<char> hex = text.Trim();
        while (hex.Length > 0 && hex[0] == '#')
            hex = hex[1..];

        if (hex.Length == 3)
        {
            int r = HexNibble(hex[0]);
            int g = HexNibble(hex[1]);
            int b = HexNibble(hex[2]);
            if ((r | g | b) < 0)
                return null;
            return new Vector4(((r << 4) | r) / 255.0f, ((g << 4) | g) / 255.0f, ((b << 4) | b) / 255.0f, 1.0f);
        }

        if (hex.Length is not (6 or 8))
            return null;

        int rByte = HexByte(hex[0], hex[1]);
        int gByte = HexByte(hex[2], hex[3]);
        int bByte = HexByte(hex[4], hex[5]);
        int aByte = hex.Length == 8 ? HexByte(hex[6], hex[7]) : 255;
        if ((rByte | gByte | bByte | aByte) < 0)
            return null;

        return new Vector4(rByte / 255.0f, gByte / 255.0f, bByte / 255.0f, aByte / 255.0f);
    }

    // Helper method to decode a single ASCII hex digit, or -1 if invalid
    private static int HexNibble(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1
    };

    // Helper method to decode two ASCII hex digits into a byte, or -1 if either is invalid
    private static int HexByte(char hi, char lo)
    {
        int h = HexNibble(hi);
        int l = HexNibble(lo);
        return (h | l) < 0 ? -1 : (h << 4) | l;
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("color", () => Color, value => Color = value);
        Expose("show_alpha", () => ShowAlpha, value => ShowAlpha = value);
        Expose("on_changed", () => changedHandler ?? new NullValue(), value => changedHandler = value);
    }
}
