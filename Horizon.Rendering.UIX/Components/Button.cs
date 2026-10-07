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
    // What the name of a style ends in when it has no body of its own, and how much of the skin's colour the chosen one of those is filled with
    private const string FLAT_SUFFIX = "_flat";
    private const float CHOSEN_FILL = 0.28f;

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
    /// A skin with a "selection" region has that drawn around the button as well, so the chosen one
    /// can be told from one the pointer just happens to be over.
    /// </summary>
    public bool Selected { get; set; }

    /// <summary>
    /// Whether the button gives a little when something happens to it. A bump when it lights up
    /// (hovered or <see cref="Selected"/>) and a squeeze when it is pressed.
    /// </summary>
    public bool Animated { get; set; } = true;

    // Whether the button was lit up on the last update, and whether there has been one yet.
    private bool wasLit, updated;

    /// <summary>Called when the button is pressed.</summary>
    public Action? OnPressed { get; set; }

    public Button()
    { }

    public Button(string label)
    {
        Label = label;
    }

    protected override bool HitTestVisible => true;

    protected internal override bool Navigable => true;

    protected internal override void OnActivate() => OnClick();

    // Lit as the chosen one, by whoever set it or by a navigator standing on it
    private bool Chosen => Selected || IsSelected;

    protected override Vector2 Measure(UISkin skin)
    {
        Vector2 label = skin.Font.Measure(Label, LabelScale > 0.0f ? LabelScale : skin.TextScale);
        bool hasArt = skin.TryGetRegion(style, out var region);

        // Art that says where its label goes is given that much room around it, otherwise the skin decides.
        UIEdges padding = hasArt ? region.ContentOr(skin.ButtonPadding) : skin.ButtonPadding;
        return Vector2.Max(hasArt ? region.Size : Vector2.Zero, label + padding.Total);
    }

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;

        bool enabled = EnabledInHierarchy;
        bool down = enabled && IsPressed && IsHovered;
        bool hovered = enabled && (IsHovered || Chosen) && !down;
        Vector4 tint = enabled ? Vector4.One : skin.DisabledTint;

        // A state the skin has no art of its own for is drawn with the plain button's.
        bool stateArt = skin.TryGetRegion(down ? pressedStyle : hovered ? hoverStyle : style, out var region);
        if (stateArt || skin.TryGetRegion(style, out region))
            list.NineSlice(region, Bounds, tint);
        else
            PaintFlat(list, skin, down, hovered, tint);

        // Art that has no look of its own for being lit gets a rim put around it
        bool flat = !stateArt && !skin.TryGetRegion(style, out _);
        if (hovered && !stateArt && !flat)
        {
            list.Rect(Bounds.Shrink(new UIEdges(2.0f)), skin.HoverColor);
            list.Outline(Bounds, 2.0f, skin.HighlightColor);
        }

        // The marker of the chosen one sits one of its own pixels outside of the button.
        if (enabled && Chosen && skin.TryGetRegion(SELECTION_REGION, out var marker))
            list.NineSlice(marker, Bounds.Shrink(new UIEdges(-marker.Scale)), Vector4.One);

        // The label is centred on what the padding leaves over, which is the face of the button when
        // the art has a lip or a shadow along one side. It sinks with the button.
        bool hasArt = stateArt || skin.TryGetRegion(style, out region);
        UIRect area = Bounds.Shrink(hasArt ? region.ContentOr(skin.ButtonPadding) : skin.ButtonPadding);
        if (down)
            area = new UIRect(area.Min - Vector2.UnitY * 2.0f, area.Max - Vector2.UnitY * 2.0f);
        list.Text(
            Label,
            area,
            Origin.Center,
            LabelScale > 0.0f ? LabelScale : skin.TextScale,
            skin.ControlTextColor * tint);
    }

    /// <summary>
    /// Helper method to draw the button for a skin that has no art for it. A box that darkens when it is held down,
    /// lights up under the pointer and gets a rim in the colour of the skin when it is the chosen one.
    /// </summary>
    private void PaintFlat(UIDrawList list, UISkin skin, bool down, bool hovered, Vector4 tint)
    {
        bool chosen = Chosen && EnabledInHierarchy;

        // A style that calls itself flat has no body. It is a label that lights up, and stays lit while it is the chosen one
        if (style.EndsWith(FLAT_SUFFIX))
        {
            if (chosen)
                list.Box(Bounds, skin.AccentColor with { W = CHOSEN_FILL });
            if (hovered || down)
                list.Box(Bounds, skin.HoverColor);
            return;
        }

        list.Box(Bounds, skin.ControlColor * (down ? new Vector4(0.8f, 0.8f, 0.8f, 1.0f) : Vector4.One) * tint);

        if (hovered)
            list.Box(Bounds, skin.HoverColor);

        if (chosen)
            list.Frame(Bounds, 2.0f, skin.HighlightColor);
        else if (skin.BorderColor.W > 0.0f)
            list.Frame(Bounds, 1.0f, skin.BorderColor * tint);
    }

    protected override void Update(float dt)
    {
        bool lit = EnabledInHierarchy && (IsHovered || Chosen);

        // A button that is lit from the start hasn't changed, there is nothing to bump for.
        if (lit && !wasLit && updated && Animated)
            this.Punch(0.05f, 0.2f);

        wasLit = lit;
        updated = true;
    }

    protected internal override void OnClick()
    {
        if (Animated)
            this.Punch(-0.08f, 0.2f);

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
        Expose("animated", () => Animated, value => Animated = value);

        // Scale used to be called spr_scale, and the handler could only be given as on_press up front.
        Expose("spr_scale", () => Scale, value => Scale = value);
        Expose("on_pressed", () => pressedHandler ?? new NullValue(), value => pressedHandler = value);
        Expose("on_press", () => pressedHandler ?? new NullValue(), value => pressedHandler = value);
    }
}
