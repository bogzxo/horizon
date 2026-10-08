using System.Numerics;

using Horizon.Rendering;
using Horizon.HIDL.Runtime;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// A switch that is either on or off, with a caption next to it. Pressing anywhere on it flips it.
/// </summary>
public class ToggleButton : UIComponent
{
    private const string ON_REGION = "toggle_on";
    private const string OFF_REGION = "toggle_off";

    private IRuntimeValue? pressedHandler;

    public string Label { get; set; } = string.Empty;

    /// <summary>The scale of the caption, or zero to use the skin's.</summary>
    public float LabelScale { get; set; }

    /// <summary>Whether the toggle is on.</summary>
    public bool State { get; set; }

    /// <summary>Called when the toggle is pressed, after <see cref="State"/> has flipped.</summary>
    public Action? OnPressed { get; set; }

    public ToggleButton()
    { }

    public ToggleButton(string label, bool startingState = false)
    {
        Label = label;
        State = startingState;
    }

    protected override bool HitTestVisible => true;

    protected internal override bool Navigable => true;

    protected internal override void OnActivate() => OnClick();

    protected override Vector2 Measure(UISkin skin)
    {
        Vector2 label = skin.Font.Measure(Label, LabelScale > 0.0f ? LabelScale : skin.TextScale);
        float gap = label.X > 0.0f ? skin.Spacing : 0.0f;

        return new Vector2(skin.ToggleSize + gap + label.X, MathF.Max(skin.ToggleSize, label.Y));
    }

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;

        bool enabled = EnabledInHierarchy;
        Vector4 tint = enabled ? Vector4.One : skin.DisabledTint;

        // The indicator is a square on the left that is as tall as the toggle can spare, the caption gets the rest.
        float side = MathF.Min(Bounds.Height, Bounds.Width);
        UIRect indicator = UIRect.FromCenter(
            new Vector2(Bounds.Min.X + side * 0.5f, Bounds.Center.Y),
            new Vector2(side));

        // The whole row lights up, as pressing anywhere on it works.
        if (enabled && (IsHovered || IsSelected))
            list.Box(Bounds.Shrink(new UIEdges(-6.0f, -4.0f)), skin.HoverColor);

        if (skin.TryGetRegion(State ? ON_REGION : OFF_REGION, out var region))
            list.Region(region, indicator, tint);
        else
            list.Box(indicator, (State ? skin.AccentColor : skin.ControlColor) * tint);

        list.Text(
            Label,
            new UIRect(new Vector2(indicator.Max.X + skin.Spacing, Bounds.Min.Y), Bounds.Max),
            Origin.Left,
            LabelScale > 0.0f ? LabelScale : skin.TextScale,
            skin.TextColor * tint);

        PaintSelection(list);
    }

    protected internal override void OnClick()
    {
        State = !State;

        OnPressed?.Invoke();
        InvokeScript(pressedHandler, new BooleanValue(State));
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("label", () => Label, value => Label = value);
        Expose("lbl_scale", () => LabelScale, value => LabelScale = value);
        Expose("state", () => State, value => State = value);

        // Scale used to be called spr_scale, and the handler could only be given as on_press up front.
        Expose("spr_scale", () => Scale, value => Scale = value);
        Expose("on_pressed", () => pressedHandler ?? new NullValue(), value => pressedHandler = value);
        Expose("on_press", () => pressedHandler ?? new NullValue(), value => pressedHandler = value);
    }
}
