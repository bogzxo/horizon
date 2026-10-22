using System.Numerics;

using Horizon.Content;
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
    private string style = "toggle";
    private string onStyle = "toggle_on";
    private string offStyle = "toggle_off";

    /// <summary>
    /// What the skin regions of the indicator are called, less the "_on" and "_off" they end in. Another pair
    /// of the skin's (a radio button, a switch) is another style.
    /// </summary>
    public string Style
    {
        get => style;
        set
        {
            style = StyleStem(value, "_on", "_off");
            onStyle = style + "_on";
            offStyle = style + "_off";
        }
    }

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
        Vector2 label = skin.Font.Measure(Resolve(Label), LabelScale > 0.0f ? LabelScale : skin.TextScale);
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

        // The whole row lights up, as pressing anywhere on it works. Always painted, see-through when it isn't lit, so
        // the toggle has the same quads tick after tick. Coming and going it shifted the switch's quads along by one
        // and the frames between two ticks blended the track with the knob, a glitch every time the pointer came by
        bool lit = enabled && (IsHovered || IsSelected);
        list.Box(Bounds.Shrink(new UIEdges(-6.0f, -4.0f)), lit ? skin.HoverColor : skin.HoverColor with { W = 0.0f });

        if (skin.TryGetRegion(State ? onStyle : offStyle, out var region))
            list.Region(region, indicator, tint);
        else
            indicator = PaintSwitch(list, skin, side, tint);

        list.Text(
            Resolve(Label),
            new UIRect(new Vector2(indicator.Max.X + skin.Spacing, Bounds.Min.Y), Bounds.Max),
            Origin.Left,
            LabelScale > 0.0f ? LabelScale : skin.TextScale,
            skin.TextColor * tint);

        PaintSelection(list);
    }

    // Where the knob of the switch is, 0 off and 1 on, eased to the state as it's painted
    private float knob = -1.0f;

    // How quickly the knob gets across, as the rate of an exponential
    private const float KNOB_RATE = 22.0f;

    /// <summary>
    /// Helper method to paint the toggle as a switch for a skin without art for one, a pill with a knob that slides to
    /// the side it's on, the pill in the accent when it's on. A square that went gold was all there was.
    /// </summary>
    /// <returns>Where it was painted, what the label goes after.</returns>
    private UIRect PaintSwitch(UIDrawList list, UISkin skin, float side, Vector4 tint)
    {
        float wanted = State ? 1.0f : 0.0f;
        knob = knob < 0.0f || !UIDrawList.Motion ? wanted : knob + (wanted - knob) * (1.0f - MathF.Exp(-KNOB_RATE * list.Delta));
        if (MathF.Abs(knob - wanted) < 0.01f)
            knob = wanted;

        float height = MathF.Round(side * 0.7f), width = MathF.Round(height * 1.8f);
        var track = new UIRect(new Vector2(Bounds.Min.X, Bounds.Center.Y - height * 0.5f), new Vector2(Bounds.Min.X + width, Bounds.Center.Y + height * 0.5f));
        Vector4 off = skin.ControlColor with { W = 1.0f }, on = skin.AccentColor with { W = 1.0f };
        list.RoundRect(track, height * 0.5f, Vector4.Lerp(off, on, knob) * tint);
        list.RoundOutline(track, height * 0.5f, 1.0f, skin.BorderColor * tint);

        float inset = MathF.Max(2.0f, height * 0.14f), round = height - inset * 2.0f;
        float x = track.Min.X + inset + (width - inset * 2.0f - round) * knob;
        var dot = new UIRect(new Vector2(x, track.Min.Y + inset), new Vector2(x + round, track.Max.Y - inset));
        Vector4 knobColour = Vector4.Lerp(skin.TextColor, UIPalette.Contrast(skin.BarTextColor, on) >= UIPalette.Contrast(skin.TextColor, on) ? skin.BarTextColor : skin.TextColor, knob);
        list.RoundRect(dot, round * 0.5f, knobColour with { W = 1.0f } * tint);
        return track;
    }

    protected internal override void OnClick()
    {
        State = !State;

        OnPressed?.Invoke();
        InvokeScript(pressedHandler, new BooleanValue(State));
    }

    // Tab gets to it, and Enter or space flips it the way a click does
    protected internal override bool TabStop => true;

    protected internal override void OnTextInput(char character)
    {
        if (character is UIKeyboard.ENTER or ' ' && EnabledInHierarchy)
            OnClick();
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("label", () => Label, value => Label = value);
        Expose("lbl_scale", () => LabelScale, value => LabelScale = value);
        Expose("state", () => State, value => State = value);
        Expose("style", () => Style, value => Style = value);

        // Scale used to be called spr_scale, and the handler could only be given as on_press up front.
        Expose("spr_scale", () => Scale, value => Scale = value);
        Expose("on_pressed", () => pressedHandler ?? new NullValue(), value => pressedHandler = value);
        Expose("on_press", () => pressedHandler ?? new NullValue(), value => pressedHandler = value);
    }
}
