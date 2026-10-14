using System.Numerics;

using Horizon.Rendering;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// A number that changes all the time, and what it is counted in after it, small. Frames a second, a health of
/// 73 out of 100, how many milliseconds something took. What makes it its own thing and not a
/// <see cref="Label"/> is that writing a new number into it makes no garbage. A label's text is a string, and
/// forty numbers written out four times a second is a hundred and sixty strings a second for the collector. This
/// keeps the few characters it was given first and writes into those.
/// <code>
/// let fps = compositor.readout({ parent: bar, size: vec(60, 18), align: "right", unit: "fps" });
/// </code>
/// <code>
/// readout.Set(engine.Fps, "0");                        // a number, formatted the way a format says
/// readout.Clear().Add("tick ").Add(tickNumber);        // or put together a piece at a time
/// </code>
/// Give it a size wide enough for the widest it gets, or whatever is next to it moves about as the number grows
/// and shrinks. The unit sits on the same line as the number however much smaller it is written.
/// </summary>
public class Readout : UIComponent
{
    private const int ROOM = 64;
    private const float UNIT_GAP = 3.0f;

    // How far down its line the middle of a letter is, which is what small text next to big text is moved by
    private const float BASELINE = 0.26f;

    private readonly char[] written = new char[ROOM];
    private int length;

    /// <summary>What it is counted in, written small after the number. Empty for none.</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>The scale of the number, or zero to use the skin's.</summary>
    public float TextScale { get; set; }

    /// <summary>The scale of the unit, or zero to use the skin's (which next to a bigger number is smaller than it).</summary>
    public float UnitScale { get; set; }

    /// <summary>The colour of the number, or null to use the skin's.</summary>
    public Vector4? Color { get; set; }

    /// <summary>The colour of the unit, or null for the colour text is written in, turned down.</summary>
    public Vector4? UnitColor { get; set; }

    /// <summary>Which side of its room the number and its unit are against.</summary>
    public Origin Align { get; set; } = Origin.Left;

    /// <summary>What is written in it, as it was written. Reading this doesn't make a string, see <see cref="Text"/> for one.</summary>
    public ReadOnlySpan<char> Written => written.AsSpan(0, length);

    /// <summary>
    /// What is written in it as a string, for a layout file to say what it starts out as and an editor to show.
    /// Reading it makes a string every time, a game reads <see cref="Written"/>.
    /// </summary>
    public string Text
    {
        get => new(written, 0, length);
        set => Clear().Add(value);
    }

    /// <summary>Wipes it, to be written into anew with <see cref="Add(ReadOnlySpan{char})"/> and its friends.</summary>
    public Readout Clear()
    {
        length = 0;
        return this;
    }

    /// <summary>Writes some words after what is there. What there is no room for is left off.</summary>
    public Readout Add(ReadOnlySpan<char> words)
    {
        int room = Math.Min(words.Length, written.Length - length);
        words[..room].CopyTo(written.AsSpan(length));
        length += room;
        return this;
    }

    /// <summary>Writes a number after what is there, the way a format says ("0.00").</summary>
    public Readout Add(double value, ReadOnlySpan<char> format)
    {
        // Not a number is what a rate is before anything has come round, and nobody wants to read "NaN"
        if (!double.IsFinite(value)) value = 0.0;

        if (value.TryFormat(written.AsSpan(length), out int digits, format))
            length += digits;

        return this;
    }

    /// <summary>Writes a whole number after what is there.</summary>
    public Readout Add(long value)
    {
        if (value.TryFormat(written.AsSpan(length), out int digits))
            length += digits;

        return this;
    }

    /// <summary>Has it say a number and nothing else, the way a format says ("0.0").</summary>
    public void Set(double value, ReadOnlySpan<char> format) => Clear().Add(value, format);

    /// <summary>Has it say a whole number and nothing else.</summary>
    public void Set(long value) => Clear().Add(value);

    /// <summary>Has it say some words and nothing else.</summary>
    public void Write(ReadOnlySpan<char> words) => Clear().Add(words);

    private float ScaleOf(UISkin skin) => TextScale > 0.0f ? TextScale : skin.TextScale;

    private float UnitScaleOf(UISkin skin) => UnitScale > 0.0f ? UnitScale : MathF.Min(skin.TextScale, ScaleOf(skin));

    protected override Vector2 Measure(UISkin skin)
    {
        float scale = ScaleOf(skin);
        Vector2 number = skin.Font.Measure(Written, scale, markup: false);
        if (Unit.Length == 0)
            return new Vector2(number.X, MathF.Max(number.Y, skin.Font.LineHeight * scale));

        Vector2 unit = skin.Font.Measure(Unit, UnitScaleOf(skin), markup: false);
        return new Vector2(number.X + UNIT_GAP + unit.X, MathF.Max(MathF.Max(number.Y, unit.Y), skin.Font.LineHeight * scale));
    }

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;
        float scale = ScaleOf(skin), unitScale = UnitScaleOf(skin);
        Vector4 colour = Color ?? skin.TextColor;

        // A number that grows a digit is a quad more, and what comes after it mustn't be taken for it for a tick
        list.BeginPart(0);

        if (Unit.Length == 0)
        {
            list.Text(Written, Bounds, Align, scale, colour, markup: false);
            list.EndParts();
            return;
        }

        Vector4 unitColour = UnitColor ?? skin.TextColor with { W = skin.TextColor.W * 0.62f };
        float numberWidth = skin.Font.Measure(Written, scale, markup: false).X;
        float unitWidth = skin.Font.Measure(Unit, unitScale, markup: false).X;
        float whole = numberWidth + UNIT_GAP + unitWidth;

        // Where the two of them start, by which side they are against
        float left = Align switch
        {
            Origin.Right or Origin.TopRight or Origin.BottomRight => Bounds.Max.X - whole,
            Origin.Center or Origin.Top or Origin.Bottom => Bounds.Center.X - whole * 0.5f,
            _ => Bounds.Min.X
        };

        // The unit a bit lower, both are put in the middle of the same row and the middle of a small letter is
        // higher up its line than a big one's
        float drop = MathF.Round(skin.Font.LineHeight * (scale - unitScale) * BASELINE);

        list.Text(Written, new UIRect(new Vector2(left, Bounds.Min.Y), new Vector2(left + numberWidth, Bounds.Max.Y)), Origin.Left, scale, colour, markup: false);

        list.BeginPart(1);
        list.Text(
            Unit,
            new UIRect(new Vector2(left + numberWidth + UNIT_GAP, Bounds.Min.Y - drop), new Vector2(left + whole, Bounds.Max.Y - drop)),
            Origin.Left, unitScale, unitColour, markup: false);
        list.EndParts();
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("text", () => Text, value => Text = value);
        Expose("unit", () => Unit, value => Unit = value);
        Expose("text_scale", () => TextScale, value => TextScale = value);
        Expose("unit_scale", () => UnitScale, value => UnitScale = value);
        Expose("color", () => Color ?? Vector4.Zero, value => Color = value);
        Expose("unit_color", () => UnitColor ?? Vector4.Zero, value => UnitColor = value);
        Expose(
            "align",
            () => Scripting.UIScript.FromEnum(Align),
            value => Align = Scripting.UIScript.ToEnum<Origin>(value, "align"));
    }
}
