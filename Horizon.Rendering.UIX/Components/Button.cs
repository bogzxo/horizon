using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.Rendering.UIX.Drawing;
using Horizon.Rendering.UIX.Skinning;

namespace Horizon.Rendering.UIX.Components;

/// <summary>
/// A button with a text label. It is pressed by releasing the pointer on it after pressing it there.
/// Unless given a size it is as big as its art, or bigger if the label needs the room.
/// </summary>
public class Button : UIComponent
{
    private string style = "button";
    private string pressedStyle = "button_pressed";
    private string hoverStyle = "button_hover";

    private IRuntimeValue? pressedHandler;

    public string Label { get; set; } = string.Empty;

    /// <summary>The scale of the label, or zero to use the skin's.</summary>
    public float LabelScale { get; set; }

    /// <summary>
    /// The name of the skin region the button is drawn with. The regions named after it with
    /// "_pressed" and "_hover" on the end are used for those states, if the skin has them.
    /// </summary>
    public string Style
    {
        get => style;
        set
        {
            style = value;
            pressedStyle = value + "_pressed";
            hoverStyle = value + "_hover";
        }
    }

    /// <summary>
    /// Shows the button as the chosen one of a group, the way hovering does. For menus that are
    /// driven by a gamepad or the keyboard, where there is no pointer to hover with.
    /// </summary>
    public bool Selected { get; set; }

    /// <summary>Called when the button is pressed.</summary>
    public Action? OnPressed { get; set; }

    public Button()
    { }

    public Button(string label)
    {
        Label = label;
    }

    protected override bool HitTestVisible => true;

    protected override Vector2 Measure(UISkin skin)
    {
        Vector2 label = skin.Font.Measure(Label, LabelScale > 0.0f ? LabelScale : skin.TextScale);
        Vector2 art = skin.TryGetRegion(style, out var region) ? region.Size : Vector2.Zero;

        return Vector2.Max(art, label + skin.ButtonPadding.Total);
    }

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;

        bool enabled = EnabledInHierarchy;
        bool down = enabled && IsPressed && IsHovered;
        bool hovered = enabled && (IsHovered || Selected) && !down;
        Vector4 tint = enabled ? Vector4.One : skin.DisabledTint;

        // A state the skin has no art of its own for is drawn with the plain button's.
        bool stateArt = skin.TryGetRegion(down ? pressedStyle : hovered ? hoverStyle : style, out var region);
        if (stateArt || skin.TryGetRegion(style, out region))
            list.NineSlice(region, Bounds, tint);
        else
            list.Rect(Bounds, skin.ControlColor * (down ? new Vector4(0.8f, 0.8f, 0.8f, 1.0f) : Vector4.One) * tint);

        if (hovered && !stateArt)
        {
            list.Rect(Bounds.Shrink(new UIEdges(2.0f)), skin.HoverColor);
            list.Outline(Bounds, 2.0f, skin.HighlightColor);
        }

        // The label sinks with the button.
        UIRect area = down ? new UIRect(Bounds.Min - Vector2.UnitY * 2.0f, Bounds.Max - Vector2.UnitY * 2.0f) : Bounds;
        list.Text(
            Label,
            area,
            Origin.Center,
            LabelScale > 0.0f ? LabelScale : skin.TextScale,
            skin.ControlTextColor * tint);
    }

    protected internal override void OnClick()
    {
        OnPressed?.Invoke();
        InvokeScript(pressedHandler);
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("label", () => Label, value => Label = value);
        Expose("lbl_scale", () => LabelScale, value => LabelScale = value);
        Expose("style", () => Style, value => Style = value);
        Expose("selected", () => Selected, value => Selected = value);

        // Scale used to be called spr_scale, and the handler could only be given as on_press up front.
        Expose("spr_scale", () => Scale, value => Scale = value);
        Expose("on_pressed", () => pressedHandler ?? new NullValue(), value => pressedHandler = value);
        Expose("on_press", () => pressedHandler ?? new NullValue(), value => pressedHandler = value);
    }
}
