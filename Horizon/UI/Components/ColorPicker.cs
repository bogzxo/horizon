using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// Picks a colour. A square of every shade of one hue (more colourful to the right, brighter to the top), a
/// strip of the hues next to it and, unless told not to, a strip for how see-through the colour is. Press or
/// drag in any of them. What is picked shows in the corner, over a checkerboard so its alpha can be seen.
/// </summary>
public class ColorPicker : UIComponent
{
    private const float DEFAULT_WIDTH = 240.0f;
    private const float DEFAULT_HEIGHT = 140.0f;
    private const float STRIP_WIDTH = 18.0f;
    private const float GAP = 6.0f;

    // The gradients are drawn as this many flat cells along each side, the draw list only does flat colours.
    private const int SHADE_CELLS = 14;
    private const int STRIP_CELLS = 24;

    private enum Zone
    {
        None,
        Shade,
        Hue,
        Alpha
    }

    private IRuntimeValue? changedHandler;

    // Kept as hue, saturation and value so the hue isn't lost on the way through black, white and grey.
    private float hue, saturation = 0.0f, brightness = 1.0f, alpha = 1.0f;
    private Zone held;

    /// <summary>The colour that is picked, red, green, blue and alpha from 0 to 1.</summary>
    public Vector4 Color
    {
        get => new(ToRgb(hue, saturation, brightness), alpha);
        set
        {
            // The same colour back leaves the hue alone, it may well be one this colour can't tell.
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

    /// <summary>Whether there is a strip for the alpha. Without one it stays whatever it was set to.</summary>
    public bool ShowAlpha { get; set; } = true;

    /// <summary>Called with the new colour whenever the pointer changes it.</summary>
    public Action<Vector4>? OnChanged { get; set; }

    protected override bool HitTestVisible => true;

    protected override Vector2 Measure(UISkin skin) => new(DEFAULT_WIDTH, DEFAULT_HEIGHT);

    /* Where everything is, the square on the left, then the strips, then the swatch in what is left */

    private float StripsWidth => (ShowAlpha ? 2 : 1) * (STRIP_WIDTH + GAP);

    /// <summary>The square of shades.</summary>
    public UIRect ShadeBounds
    {
        get
        {
            float side = MathF.Max(1.0f, MathF.Min(Bounds.Height, Bounds.Width - StripsWidth));
            return new UIRect(new Vector2(Bounds.Min.X, Bounds.Max.Y - side), new Vector2(Bounds.Min.X + side, Bounds.Max.Y));
        }
    }

    /// <summary>The strip of hues, red at the bottom and all the way around to red again at the top.</summary>
    public UIRect HueBounds
    {
        get
        {
            UIRect shade = ShadeBounds;
            return new UIRect(new Vector2(shade.Max.X + GAP, shade.Min.Y), new Vector2(shade.Max.X + GAP + STRIP_WIDTH, shade.Max.Y));
        }
    }

    /// <summary>The strip of the alpha, see-through at the bottom.</summary>
    public UIRect AlphaBounds
    {
        get
        {
            UIRect strip = HueBounds;
            return new UIRect(new Vector2(strip.Max.X + GAP, strip.Min.Y), new Vector2(strip.Max.X + GAP + STRIP_WIDTH, strip.Max.Y));
        }
    }

    private UIRect SwatchBounds
    {
        get
        {
            UIRect last = ShowAlpha ? AlphaBounds : HueBounds;
            return new UIRect(new Vector2(last.Max.X + GAP, last.Min.Y), new Vector2(MathF.Max(last.Max.X + GAP, Bounds.Max.X), last.Max.Y));
        }
    }

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;
        Vector4 tint = EnabledInHierarchy ? Vector4.One : skin.DisabledTint;

        UIRect shade = ShadeBounds;
        Vector2 cell = shade.Size / SHADE_CELLS;

        for (int y = 0; y < SHADE_CELLS; y++)
        {
            for (int x = 0; x < SHADE_CELLS; x++)
            {
                Vector3 colour = ToRgb(hue, x / (SHADE_CELLS - 1.0f), y / (SHADE_CELLS - 1.0f));
                Vector2 min = shade.Min + new Vector2(x, y) * cell;

                list.Rect(new UIRect(min, min + cell), new Vector4(colour, 1.0f) * tint);
            }
        }

        UIRect hues = HueBounds;
        float step = hues.Height / STRIP_CELLS;

        for (int i = 0; i < STRIP_CELLS; i++)
        {
            var min = new Vector2(hues.Min.X, hues.Min.Y + i * step);
            list.Rect(new UIRect(min, min + new Vector2(hues.Width, step)), new Vector4(ToRgb(i / (float)STRIP_CELLS, 1.0f, 1.0f), 1.0f) * tint);
        }

        Vector3 solid = ToRgb(hue, saturation, brightness);

        if (ShowAlpha)
        {
            UIRect alphas = AlphaBounds;
            PaintCheckers(list, alphas, tint);

            for (int i = 0; i < STRIP_CELLS; i++)
            {
                var min = new Vector2(alphas.Min.X, alphas.Min.Y + i * step);
                list.Rect(new UIRect(min, min + new Vector2(alphas.Width, step)), new Vector4(solid, (i + 0.5f) / STRIP_CELLS) * tint);
            }

            PaintMark(list, alphas, alphas.Min.Y + alpha * alphas.Height, skin);
        }

        PaintMark(list, hues, hues.Min.Y + hue * hues.Height, skin);

        // Where in the square the colour is, ringed twice so it shows on dark and on light.
        Vector2 at = shade.Min + new Vector2(saturation, brightness) * shade.Size;
        list.Outline(UIRect.FromCenter(at, new Vector2(10.0f)), 2.0f, new Vector4(0.0f, 0.0f, 0.0f, 1.0f));
        list.Outline(UIRect.FromCenter(at, new Vector2(6.0f)), 1.0f, Vector4.One);

        UIRect swatch = SwatchBounds;
        if (!swatch.IsEmpty)
        {
            PaintCheckers(list, swatch, tint);
            list.Rect(swatch, new Vector4(solid, alpha) * tint);
            list.Outline(swatch, 1.0f, skin.TextColor * tint);
        }
    }

    private static void PaintMark(UIDrawList list, UIRect strip, float y, UISkin skin)
    {
        var mark = new UIRect(new Vector2(strip.Min.X - 2.0f, y - 2.0f), new Vector2(strip.Max.X + 2.0f, y + 2.0f));

        list.Rect(mark, Vector4.One);
        list.Outline(mark, 1.0f, new Vector4(0.0f, 0.0f, 0.0f, 1.0f));
    }

    private static void PaintCheckers(UIDrawList list, UIRect area, Vector4 tint)
    {
        const float SIZE = 6.0f;

        list.Rect(area, new Vector4(0.75f, 0.75f, 0.75f, 1.0f) * tint);

        int row = 0;
        for (float y = area.Min.Y; y < area.Max.Y; y += SIZE, row++)
        {
            for (float x = area.Min.X + (row % 2) * SIZE; x < area.Max.X; x += SIZE * 2.0f)
            {
                var max = new Vector2(MathF.Min(x + SIZE, area.Max.X), MathF.Min(y + SIZE, area.Max.Y));
                list.Rect(new UIRect(new Vector2(x, y), max), new Vector4(0.45f, 0.45f, 0.45f, 1.0f) * tint);
            }
        }
    }

    protected internal override void OnPointerDown(Vector2 point)
    {
        // Whatever was pressed on keeps the pointer until it is let go of, wherever it wanders off to.
        held =
            ShadeBounds.Contains(point) ? Zone.Shade
            : HueBounds.Contains(point) ? Zone.Hue
            : ShowAlpha && AlphaBounds.Contains(point) ? Zone.Alpha
            : Zone.None;

        MoveTo(point);
    }

    protected internal override void OnPointerDrag(Vector2 point) => MoveTo(point);

    protected internal override void OnPointerUp(Vector2 point) => held = Zone.None;

    private void MoveTo(Vector2 point)
    {
        Vector4 before = Color;
        float hueBefore = hue;

        switch (held)
        {
            case Zone.Shade:
                UIRect shade = ShadeBounds;
                saturation = Math.Clamp((point.X - shade.Min.X) / shade.Width, 0.0f, 1.0f);
                brightness = Math.Clamp((point.Y - shade.Min.Y) / shade.Height, 0.0f, 1.0f);
                break;

            case Zone.Hue:
                // Just short of all the way around, which would be red again and read back as the bottom.
                hue = Math.Clamp((point.Y - HueBounds.Min.Y) / HueBounds.Height, 0.0f, 0.999f);
                break;

            case Zone.Alpha:
                alpha = Math.Clamp((point.Y - AlphaBounds.Min.Y) / AlphaBounds.Height, 0.0f, 1.0f);
                break;

            default:
                return;
        }

        if (Color != before || hue != hueBefore)
        {
            OnChanged?.Invoke(Color);
            InvokeScript(changedHandler, new Vector4Value(Color));
        }
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

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("color", () => Color, value => Color = value);
        Expose("show_alpha", () => ShowAlpha, value => ShowAlpha = value);
        Expose("on_changed", () => changedHandler ?? new NullValue(), value => changedHandler = value);
    }
}
