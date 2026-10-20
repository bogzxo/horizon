using System.Numerics;

namespace Horizon.UI.Skinning;

/// <summary>
/// Every colour a skin draws with, in one place, which is what a theme changes (see <see cref="UITheme"/>). The
/// components ask the skin, whose colour properties read and write this, so a skin's palette can be swapped or
/// looked at whole. The colours code is shown in are here too (<see cref="Syntax"/>).
/// <para>
/// A palette can fill itself in from a few colours (<see cref="Derive"/>). A theme that says its background, its
/// surface, its text and its accent has the rest worked out, the field and the track out of the background, the
/// panel and the control out of the surface, the border and the hover out of the text, the highlight out of the
/// accent, and only what it writes out itself is taken as written.
/// </para>
/// </summary>
public sealed class UIPalette
{
    /// <summary>The colour of text that stands on its own. Labels and captions.</summary>
    public Vector4 Text { get; set; } = new(0.08f, 0.09f, 0.13f, 1.0f);

    /// <summary>The colour of text drawn on top of a control's art, like the label of a button.</summary>
    public Vector4 ControlText { get; set; } = new(0.08f, 0.09f, 0.13f, 1.0f);

    /// <summary>What a control is filled with when the skin has no art for it.</summary>
    public Vector4 Control { get; set; } = new(0.78f, 0.81f, 0.87f, 1.0f);

    /// <summary>What something to type into is filled with when the skin has no art for it.</summary>
    public Vector4 Field { get; set; } = new(0.0f, 0.0f, 0.0f, 0.35f);

    /// <summary>The thin line around controls that are drawn without art. See-through (which it is unless a skin says otherwise) for none.</summary>
    public Vector4 Border { get; set; }

    /// <summary>What a panel that asks for a background is filled with when the skin has no art for it.</summary>
    public Vector4 Panel { get; set; } = new(0.1f, 0.12f, 0.17f, 0.92f);

    /// <summary>What the empty part of a bar or slider is filled with.</summary>
    public Vector4 Track { get; set; } = new(0.0f, 0.0f, 0.0f, 0.35f);

    /// <summary>Laid over a control while the pointer is on it.</summary>
    public Vector4 Hover { get; set; } = new(1.0f, 1.0f, 1.0f, 0.22f);

    /// <summary>The colour of the filled part of bars and sliders, and of toggles that are on, when the skin has no art for them.</summary>
    public Vector4 Accent { get; set; } = new(0.85f, 0.42f, 0.3f, 1.0f);

    /// <summary>The outline drawn around a control while the pointer is on it.</summary>
    public Vector4 Highlight { get; set; } = new(1.0f, 1.0f, 1.0f, 0.9f);

    /// <summary>What a disabled control is tinted with.</summary>
    public Vector4 DisabledTint { get; set; } = new(1.0f, 1.0f, 1.0f, 0.4f);

    /// <summary>The colour of the text written on bars. It gets a shadow, as it has to read on both the filled and the empty part.</summary>
    public Vector4 BarText { get; set; } = new(1.0f, 1.0f, 1.0f, 1.0f);

    /// <summary>The shadow under <see cref="BarText"/>.</summary>
    public Vector4 TextShadow { get; set; } = new(0.0f, 0.0f, 0.0f, 0.7f);

    /// <summary>The colour of the label on an icon that doesn't say its own.</summary>
    public Vector4 IconLabel { get; set; } = new(1.0f, 1.0f, 1.0f, 1.0f);

    /* The kinds of text. What is said quietly, what can be clicked, and what has gone wrong, right or nearly so */

    /// <summary>Text that is said quietly, a placeholder, a detail after a name, a tab that isn't the one open, the text colour seen through unless the theme says.</summary>
    public Vector4 DimText { get; set; } = new(0.08f, 0.09f, 0.13f, 0.6f);

    /// <summary>A link in a text, the accent unless the theme says.</summary>
    public Vector4 Link { get; set; } = new(0.85f, 0.42f, 0.3f, 1.0f);

    /// <summary>Something that has gone wrong, a problem in the code, a failed check. <c>[color:error]</c> in a text.</summary>
    public Vector4 Error { get; set; } = new(1.0f, 0.42f, 0.38f, 1.0f);

    /// <summary>Something to look at, a warning in the code. <c>[color:warning]</c> in a text.</summary>
    public Vector4 Warning { get; set; } = new(1.0f, 0.78f, 0.3f, 1.0f);

    /// <summary>Something that went right. <c>[color:success]</c> in a text.</summary>
    public Vector4 Success { get; set; } = new(0.36f, 0.85f, 0.45f, 1.0f);

    /// <summary>The colours code is shown in, see <see cref="Components.CodeView"/>.</summary>
    public UISyntaxColors Syntax { get; } = new();

    /// <summary>Whether code is written on a light field, which picks a language's light colours over its dark ones.</summary>
    public bool LightField { get; private set; }

    /// <summary>
    /// The keys a skin or theme file writes a colour under, in the order they are worked out. <c>background</c> and
    /// <c>surface</c> are the two that aren't colours of the palette themselves, they are what the others are
    /// derived from (<see cref="Derive"/>).
    /// </summary>
    public static readonly string[] Keys =
    [
        "background", "surface", "text_color", "accent_color", "control_text_color", "control_color", "field_color", "border_color",
        "panel_color", "track_color", "hover_color", "highlight_color", "disabled_tint", "bar_text_color", "text_shadow_color",
        "icon_label_color", "dim_text_color", "link_color", "error_color", "warning_color", "success_color"
    ];

    /// <summary>Whether a key of a skin file is a colour of the palette (or one the palette is derived from).</summary>
    public static bool IsKey(string key) => Array.IndexOf(Keys, key) >= 0;

    /// <summary>
    /// The colours that are worked out of a key when nobody wrote them (<see cref="Derive"/>). A theme that writes the
    /// key gets these worked out again even where the skin wrote them, a theme's surface is its panel and its controls
    /// whatever the skin's were, see UISkin.Loading.
    /// </summary>
    public static IReadOnlyList<string> DerivedFrom(string key) => key switch
    {
        "background" => ["field_color", "track_color"],
        "surface" => ["panel_color", "control_color"],
        "text_color" => ["control_text_color", "border_color", "hover_color", "icon_label_color", "dim_text_color"],
        "accent_color" => ["highlight_color", "link_color"],
        _ => []
    };

    /// <summary>
    /// Sets a colour by the key a file writes it under. <c>background</c> and <c>surface</c> are no colour of the
    /// palette and are left to <see cref="Derive"/>, this says so by returning false for them.
    /// </summary>
    public bool Set(string key, Vector4 color)
    {
        switch (key)
        {
            case "text_color": Text = color; break;
            case "control_text_color": ControlText = color; break;
            case "control_color": Control = color; break;
            case "field_color": Field = color; break;
            case "border_color": Border = color; break;
            case "panel_color": Panel = color; break;
            case "track_color": Track = color; break;
            case "hover_color": Hover = color; break;
            case "accent_color": Accent = color; break;
            case "highlight_color": Highlight = color; break;
            case "disabled_tint": DisabledTint = color; break;
            case "bar_text_color": BarText = color; break;
            case "text_shadow_color": TextShadow = color; break;
            case "icon_label_color": IconLabel = color; break;
            case "dim_text_color": DimText = color; break;
            case "link_color": Link = color; break;
            case "error_color": Error = color; break;
            case "warning_color": Warning = color; break;
            case "success_color": Success = color; break;
            default: return false;
        }

        return true;
    }

    /// <summary>
    /// Works the colours a file didn't write out of the ones it did. Only the keys in <paramref name="given"/> were
    /// written, everything else is filled in here, so a theme of four colours comes out whole and a theme that wrote
    /// every colour is taken as written.
    /// </summary>
    /// <param name="given">The colours the file wrote, by their keys, <c>background</c> and <c>surface</c> among them if it did.</param>
    /// <param name="syntaxGiven">Whether the file wrote any colour of code. If it didn't, the code is coloured for a dark or a light field, whichever this is.</param>
    public void Derive(IReadOnlyDictionary<string, Vector4> given, bool syntaxGiven = true)
    {
        bool Given(string key) => given.ContainsKey(key);

        if (given.TryGetValue("background", out Vector4 background))
        {
            if (!Given("field_color")) Field = background;
            if (!Given("track_color")) Track = background;
        }

        if (given.TryGetValue("surface", out Vector4 surface))
        {
            if (!Given("panel_color")) Panel = surface;

            // A control stands a step off its panel, towards the text, so a button is seen on the panel it sits on
            if (!Given("control_color")) Control = Towards(surface, Text, 0.14f);
        }

        if (given.TryGetValue("text_color", out Vector4 text))
        {
            if (!Given("control_text_color")) ControlText = text;
            if (!Given("icon_label_color")) IconLabel = text;
            if (!Given("border_color")) Border = text with { W = 0.12f };
            if (!Given("hover_color")) Hover = text with { W = 0.08f };
        }

        // The quiet text is the text seen through, whatever the text is, unless the theme has one of its own
        if (!Given("dim_text_color")) DimText = Text with { W = Text.W * 0.62f };

        if (given.TryGetValue("accent_color", out Vector4 accent))
        {
            if (!Given("highlight_color")) Highlight = accent with { W = 0.9f };
        }

        // The panel as the eye gets it, over black where it is see-through, is what the coloured text has to read on
        Vector4 panel = Vector4.Lerp(new Vector4(0.0f, 0.0f, 0.0f, 1.0f), Panel with { W = 1.0f }, Panel.W);
        bool lightPanel = Luminance(panel) > 0.18f;

        // A link is the accent, pulled down towards black on a light panel, as far as it takes for it to read there
        if (!Given("link_color"))
        {
            Link = Accent;
            for (float share = 0.1f; lightPanel && share <= 0.71f && Contrast(Link, panel) < 3.0f; share += 0.1f)
                Link = Towards(Accent, new Vector4(0.0f, 0.0f, 0.0f, 1.0f), share);
        }

        // What has gone wrong, right or nearly so, in colours that read on this palette, light ones on a dark panel
        // and deep ones on a light one
        if (!Given("error_color")) Error = lightPanel ? new Vector4(0.65f, 0.08f, 0.1f, 1.0f) : new Vector4(1.0f, 0.42f, 0.38f, 1.0f);
        if (!Given("warning_color")) Warning = lightPanel ? new Vector4(0.5f, 0.27f, 0.0f, 1.0f) : new Vector4(1.0f, 0.78f, 0.3f, 1.0f);
        if (!Given("success_color")) Success = lightPanel ? new Vector4(0.0f, 0.4f, 0.12f, 1.0f) : new Vector4(0.36f, 0.85f, 0.45f, 1.0f);

        // Code is written on the field. A skin that never coloured its code gets it coloured for the field it has,
        // the colours for a dark field are the defaults and the ones for a light field are these
        Vector4 field = Vector4.Lerp(panel, Field with { W = 1.0f }, Field.W);
        LightField = Luminance(field) > 0.3f;
        if (!syntaxGiven)
        {
            if (LightField) UISyntaxColors.Light().CopyTo(Syntax);

            // The numbers down the side sit on the panel, not the field, and are the text half seen
            Syntax.LineNumber = Text with { W = Text.W * 0.5f };
        }
    }

    /// <summary>A colour moved part of the way towards another, the alpha of the first kept.</summary>
    public static Vector4 Towards(Vector4 from, Vector4 to, float share) =>
        Vector4.Lerp(from, to, share) with { W = from.W };

    /// <summary>
    /// How light a colour is, 0 black to 1 white, the way the eye weighs it (the relative luminance of the web's
    /// guidelines, the sRGB curve taken out first, so a grey of 0.2 is about 0.03 and not 0.2). What the contrast
    /// between two colours is worked out from.
    /// </summary>
    public static float Luminance(Vector4 color)
    {
        static float Channel(float c) => c <= 0.03928f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        return 0.2126f * Channel(color.X) + 0.7152f * Channel(color.Y) + 0.0722f * Channel(color.Z);
    }

    /// <summary>
    /// The contrast between a colour and what it is on, 1 for the same colour and 21 for black on white, the ratio
    /// of the web's guidelines (4.5 reads easily, 3 does for big or quiet text). The first is laid over the second by
    /// its alpha first.
    /// </summary>
    public static float Contrast(Vector4 text, Vector4 on)
    {
        float lighter = Luminance(Vector4.Lerp(on, text with { W = 1.0f }, text.W)), darker = Luminance(on);
        if (lighter < darker) (lighter, darker) = (darker, lighter);
        return (lighter + 0.05f) / (darker + 0.05f);
    }

    /// <summary>Whether the palette is a dark one, text lighter than its panel.</summary>
    public bool IsDark => Luminance(Text) > Luminance(Panel);

    /// <summary>A copy, for a UI that wants to change a colour without changing the skin's.</summary>
    public UIPalette Clone()
    {
        var copy = new UIPalette
        {
            Text = Text, ControlText = ControlText, Control = Control, Field = Field, Border = Border, Panel = Panel, Track = Track,
            Hover = Hover, Accent = Accent, Highlight = Highlight, DisabledTint = DisabledTint, BarText = BarText, TextShadow = TextShadow,
            IconLabel = IconLabel, DimText = DimText, Link = Link, Error = Error, Warning = Warning, Success = Success
        };
        Syntax.CopyTo(copy.Syntax);
        return copy;
    }
}
