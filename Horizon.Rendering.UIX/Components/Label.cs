using System.Numerics;

using Horizon.Rendering.UIX.Drawing;
using Horizon.Rendering.UIX.Scripting;
using Horizon.Rendering.UIX.Skinning;

namespace Horizon.Rendering.UIX.Components;

/// <summary>
/// A piece of text. Sizes itself to fit unless told otherwise; lines are split on '\n'.
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

    public Label()
    { }

    public Label(string text)
    {
        Text = text;
    }

    protected override Vector2 Measure(UISkin skin) =>
        skin.Font.Measure(Text, TextScale > 0.0f ? TextScale : skin.TextScale);

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;

        list.Text(
            Text,
            Bounds,
            Align,
            TextScale > 0.0f ? TextScale : skin.TextScale,
            Color ?? skin.TextColor);
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("text", () => Text, value => Text = value);
        Expose("text_scale", () => TextScale, value => TextScale = value);
        Expose("color", () => Color ?? Vector4.Zero, value => Color = value);
        Expose(
            "align",
            () => UIScript.FromEnum(Align),
            value => Align = UIScript.ToEnum<Origin>(value, "align"));
    }
}
