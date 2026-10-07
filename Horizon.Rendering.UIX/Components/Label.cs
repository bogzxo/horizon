using System.Numerics;

using Horizon.Rendering.UIX.Drawing;
using Horizon.Rendering.UIX.Scripting;
using Horizon.Rendering.UIX.Skinning;

namespace Horizon.Rendering.UIX.Components;

/// <summary>
/// A piece of text. Sizes itself to fit unless told otherwise; lines are split on '\n'.
/// Told to <see cref="Wrap"/>, text that is wider than the label is broken between words into as many lines as
/// it takes, and the label is as tall as those. That wants a width to wrap to: a <see cref="UIComponent.Size"/>
/// with a width, or a <see cref="UIComponent.Fill"/> across. A label with neither is one line, wrap or no wrap.
/// <code>
/// let about = compositor.label({ size: vec(400, 0), wrap: true, align: "top_left", text: "..." });
/// </code>
/// </summary>
public class Label : UIComponent
{
    public string Text { get; set; } = string.Empty;

    /// <summary>The scale of the text, or zero to use the skin's.</summary>
    public float TextScale { get; set; }

    /// <summary>The colour of the text, or null to use the skin's.</summary>
    public Vector4? Color { get; set; }

    /// <summary>Where the text goes when the label is bigger than it.</summary>
    public Origin Align { get; set; } = Origin.Center;

    /// <summary>
    /// Whether text that is wider than the label is broken into more lines rather than running off the edge.
    /// The label grows downwards for them unless it has a height of its own. See the class for what it wraps to.
    /// </summary>
    public bool Wrap { get; set; }

    // The lines the text was last cut into, and what it was cut for, so it is only cut again when any of that changes
    private readonly List<(int Start, int Length)> lines = [];
    private string? wrappedText;
    private float wrappedScale, wrappedWidth;

    // Whether the lines are what the text is drawn as right now
    private bool wrapped;

    public Label()
    { }

    public Label(string text)
    {
        Text = text;
    }

    /// <summary>How many lines the text is drawn as, once it has been laid out.</summary>
    public int LineCount => wrapped ? lines.Count : 1 + Text.AsSpan().Count('\n');

    private float ScaleOf(UISkin skin) => TextScale > 0.0f ? TextScale : skin.TextScale;

    /// <summary>
    /// Helper method to work out how wide there is to wrap to. The label's own width if it has one, or the width it was
    /// given last time if it fills across (which is a step behind, and catches up on the next layout). Nothing otherwise.
    /// </summary>
    private float WrapWidth()
    {
        if (Size.X > 0.0f)
            return Size.X;

        return (Fill & UIFill.Horizontal) != 0 ? Bounds.Width : 0.0f;
    }

    protected override Vector2 Measure(UISkin skin)
    {
        float scale = ScaleOf(skin);
        float width = Wrap ? WrapWidth() : 0.0f;

        wrapped = width > 0.0f;
        if (!wrapped)
            return skin.Font.Measure(Text, scale);

        // Only cut again when the text, its size or the room has changed. The text is a string, the same one is the same text
        if (!ReferenceEquals(wrappedText, Text) || wrappedScale != scale || wrappedWidth != width)
        {
            skin.Font.Wrap(Text, scale, width, lines);
            (wrappedText, wrappedScale, wrappedWidth) = (Text, scale, width);
        }

        // As wide as it was told to be, which is what it was wrapped to
        return new Vector2(width, skin.Font.Measure(Text, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(lines), scale).Y);
    }

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;
        Vector4 color = Color ?? skin.TextColor;

        if (wrapped)
            list.Text(Text, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(lines), Bounds, Align, ScaleOf(skin), color);
        else
            list.Text(Text, Bounds, Align, ScaleOf(skin), color);
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("text", () => Text, value => Text = value);
        Expose("text_scale", () => TextScale, value => TextScale = value);
        Expose("color", () => Color ?? Vector4.Zero, value => Color = value);
        Expose("wrap", () => Wrap, value => Wrap = value);
        Expose(
            "align",
            () => UIScript.FromEnum(Align),
            value => Align = UIScript.ToEnum<Origin>(value, "align"));
    }
}
