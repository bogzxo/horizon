using System.Numerics;

using Horizon.Content;
using Horizon.Rendering;
using Horizon.HIDL.Runtime;
using Horizon.UI.Drawing;
using Horizon.UI.Scripting;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// A button with a text label. It is pressed by releasing the pointer on it after pressing it there.
/// Unless given a size it is as big as its art, or bigger if the label needs the room.
/// </summary>
public class Button : UIComponent
{
    // What the name of a style ends in when it has no body of its own, and how much of the skin's colour the chosen one of those is filled with
    private const string FLAT_SUFFIX = "_flat";
    private const float CHOSEN_FILL = 0.13f;

    private string style = "button";
    private string pressedStyle = "button_pressed";
    private string hoverStyle = "button_hover";

    private IRuntimeValue? pressedHandler;

    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// One of the icons every skin has (<see cref="UISymbols"/>, a Codicon by name, "save", "git-branch") in front of the
    /// label, or the whole of the button with no label. Empty for none.
    /// </summary>
    public string Icon { get; set; } = string.Empty;

    /// <summary>
    /// Where on the button its icon and label sit, in the middle unless told. A grid of them (a toolbox) reads better
    /// from the left, their icons in a column, than each one centred on its own.
    /// </summary>
    public Origin Align { get; set; } = Origin.Center;

    // The icon and the label written as one text, made again only when either changes
    private string shownIcon = string.Empty, shownLabel = string.Empty, shown = string.Empty;

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
    /// Whether it's the one to press, the default of a dialog, filled with the accent where the skin has no art for a
    /// button and labelled in whichever of the skin's text colours reads on that. Art skins draw it as they draw any.
    /// </summary>
    public bool Primary { get; set; }

    /// <summary>
    /// Whether what it does can't be taken back (Reset all, Don't save, Delete), filled with the skin's error red where
    /// the skin has no art for a button. A dialog never has the keyboard start on one.
    /// </summary>
    public bool Danger { get; set; }

    /// <summary>
    /// An edge of the button that gets a line in the accent colour while it is selected, the bar down the left of the
    /// view that is open in an activity bar, VS Code's. None for none, which is what a button comes as. In a layout
    /// <c>indicator: "left"</c>.
    /// </summary>
    public UIResize Indicator { get; set; }

    // How thick that line is
    private const float INDICATOR = 2.0f;

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

    protected override bool ShowsAsChosen => Chosen;

    // The body, the marker of the chosen one and the label are counted as parts of their own, see Paint
    private const int BODY_PART = 0, MARKER_PART = 1, LABEL_PART = 2;

    /// <summary>What is written on the button, its icon as $(name) in front of its label.</summary>
    private string Written()
    {
        string label = Resolve(Label);
        if (Icon.Length == 0)
            return label;

        if (!ReferenceEquals(Icon, shownIcon) || !ReferenceEquals(label, shownLabel))
        {
            shownIcon = Icon;
            shownLabel = label;
            shown = label.Length > 0 ? $"$({Icon}) {label}" : $"$({Icon})";
        }

        return shown;
    }

    protected override Vector2 Measure(UISkin skin)
    {
        Vector2 label = skin.Font.Measure(Written(), LabelScale > 0.0f ? LabelScale : skin.TextScale);
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

        // The hover box and the marker are quads that come and go, and counted along with the rest they pushed the
        // label a quad further on, so the frame between two ticks blended a letter with the hover box and the label
        // flashed white. In parts the count only shifts inside the part it happened in
        list.BeginPart(BODY_PART);

        // A state the skin has no art of its own for is drawn with the plain button's.
        bool stateArt = skin.TryGetRegion(down ? pressedStyle : hovered ? hoverStyle : style, out var region);
        bool primary = false;
        if (stateArt || skin.TryGetRegion(style, out region))
            list.NineSlice(region, Bounds, tint);
        else if (Primary || Danger)
        {
            Vector4 accent = FillOf(skin);
            list.Box(Bounds, (down ? accent * 0.85f : hovered ? Vector4.Lerp(accent, Vector4.One, 0.12f) : accent) with { W = tint.W });
            primary = true;
        }
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
        list.BeginPart(MARKER_PART);
        if (enabled && Chosen && skin.TryGetRegion(SELECTION_REGION, out var marker))
            list.NineSlice(marker, Bounds.Shrink(new UIEdges(-marker.Scale)), Vector4.One);

        // The edge of the chosen one marked in the text's colour, the pill under it says it's chosen and the accent is for
        // what's on
        if (enabled && Chosen && Indicator != UIResize.None)
        {
            UIRect at = Bounds;
            Vector4 accent = skin.TextColor with { W = 0.85f };
            if ((Indicator & UIResize.Left) != 0) list.Rect(new UIRect(at.Min, new Vector2(at.Min.X + INDICATOR, at.Max.Y)), accent);
            if ((Indicator & UIResize.Right) != 0) list.Rect(new UIRect(new Vector2(at.Max.X - INDICATOR, at.Min.Y), at.Max), accent);
            if ((Indicator & UIResize.Top) != 0) list.Rect(new UIRect(new Vector2(at.Min.X, at.Max.Y - INDICATOR), at.Max), accent);
            if ((Indicator & UIResize.Bottom) != 0) list.Rect(new UIRect(at.Min, new Vector2(at.Max.X, at.Min.Y + INDICATOR)), accent);
        }

        list.BeginPart(LABEL_PART);

        // The label is centred on what the padding leaves over, which is the face of the button when
        // the art has a lip or a shadow along one side. It sinks with the button.
        bool hasArt = stateArt || skin.TryGetRegion(style, out region);
        UIRect area = Bounds.Shrink(hasArt ? region.ContentOr(skin.ButtonPadding) : skin.ButtonPadding);
        if (down)
            area = new UIRect(area.Min - Vector2.UnitY * 2.0f, area.Max - Vector2.UnitY * 2.0f);
        list.Text(
            Written(),
            area,
            Align,
            LabelScale > 0.0f ? LabelScale : skin.TextScale,
            (primary ? OnFill(skin, FillOf(skin)) : hasArt ? region.TextOr(skin.ControlTextColor) : skin.ControlTextColor) * tint);
        list.EndParts();
    }

    // The fill of a primary button, the accent, or the skin's error red for a dangerous one
    private Vector4 FillOf(UISkin skin) => (Danger ? skin.ErrorColor : skin.AccentColor) with { W = 1.0f };

    // Whichever of the skin's text colours reads on that fill
    private static Vector4 OnFill(UISkin skin, Vector4 fill) =>
        Horizon.UI.Skinning.UIPalette.Contrast(skin.BarTextColor, fill) >= Horizon.UI.Skinning.UIPalette.Contrast(skin.TextColor, fill)
            ? skin.BarTextColor : skin.TextColor;

    /// <summary>
    /// Helper method to draw the button for a skin that has no art for it. A box that darkens when it is held down,
    /// lights up under the pointer and gets a rim in the colour of the skin when it is the chosen one. Its edge is half
    /// the skin's hairline, a button is told from its panel by its fill the way a Mac's are, and a rim round every one
    /// of a grid of them made a cage of it.
    /// </summary>
    private void PaintFlat(UIDrawList list, UISkin skin, bool down, bool hovered, Vector4 tint)
    {
        bool chosen = Chosen && EnabledInHierarchy;

        // A style that calls itself flat has no body. It is a label that lights up, and stays lit while it is the chosen one
        if (style.EndsWith(FLAT_SUFFIX))
        {
            // The chosen one a lighter pill of the text's colour, a segment of a segmented control. It was the accent seen
            // through, gold over Horizon's blue, which came out olive, and the accent is for the indicator when it has one
            if (chosen)
                list.Box(Bounds, skin.TextColor with { W = CHOSEN_FILL });
            if (hovered || down)
                list.Box(Bounds, skin.HoverColor);
            return;
        }

        list.Box(Bounds, skin.ControlColor * (down ? new Vector4(0.8f, 0.8f, 0.8f, 1.0f) : Vector4.One) * tint);

        if (hovered)
            list.Box(Bounds, skin.HoverColor);

        if (chosen)
            list.Frame(Bounds, 1.5f, skin.HighlightColor with { W = 0.8f });
        else if (skin.BorderColor.W > 0.0f)
            list.Frame(Bounds, 1.0f, skin.BorderColor * new Vector4(1.0f, 1.0f, 1.0f, 0.5f) * tint);
    }

    protected override void Update(float dt)
    {
        bool lit = EnabledInHierarchy && (IsHovered || Chosen);

        // A button that is lit from the start hasn't changed, there is nothing to bump for.
        if (lit && !wasLit && updated && Animated && MotionTime(0.2f) > 0.0f)
            this.Punch(0.05f, MotionTime(0.2f));

        wasLit = lit;
        updated = true;
    }

    protected internal override void OnClick()
    {
        if (Animated && MotionTime(0.2f) > 0.0f)
            this.Punch(-0.08f, MotionTime(0.2f));

        OnPressed?.Invoke();
        InvokeScript(pressedHandler);
    }

    // Tab gets to it, and Enter or space presses it the way a click does
    protected internal override bool TabStop => OnPressed is not null || pressedHandler is not null;

    protected internal override void OnTextInput(char character)
    {
        if (character is UIKeyboard.ENTER or ' ' && EnabledInHierarchy)
            OnClick();
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("label", () => Label, value => Label = value);
        Expose("icon", () => Icon, value => Icon = value);
        Expose("align", () => UIScript.FromEnum(Align), value => Align = UIScript.ToEnum<Origin>(value, "align"));
        Expose("lbl_scale", () => LabelScale, value => LabelScale = value);
        Expose("style", () => Style, value => Style = value);
        Expose("selected", () => Selected, value => Selected = value);
        Expose("primary", () => Primary, value => Primary = value);
        Expose("danger", () => Danger, value => Danger = value);
        Expose("indicator", () => UIScript.FromEnum(Indicator), value => Indicator = UIScript.ToEnum<UIResize>(value, "indicator"));
        Expose("animated", () => Animated, value => Animated = value);

        // Scale used to be called spr_scale, and the handler could only be given as on_press up front.
        Expose("spr_scale", () => Scale, value => Scale = value);
        Expose("on_pressed", () => pressedHandler ?? new NullValue(), value => pressedHandler = value);
        Expose("on_press", () => pressedHandler ?? new NullValue(), value => pressedHandler = value);
    }
}
