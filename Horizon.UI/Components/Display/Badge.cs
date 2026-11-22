using System.Numerics;

using Horizon.Engine;
using Horizon.Rendering;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>What a <see cref="Badge"/> says by its colour, from the skin's palette.</summary>
public enum UIBadgeTone
{
    /// <summary>The text's own colour, quiet, a count that's only a count.</summary>
    Neutral,

    /// <summary>The accent, something for whoever's looking (unread, new).</summary>
    Accent,

    /// <summary>Blue, something going on.</summary>
    Info,

    /// <summary>The palette's green, done and well.</summary>
    Success,

    /// <summary>The palette's amber, wants a look.</summary>
    Warning,

    /// <summary>The palette's red, failed or wrong.</summary>
    Danger
}

/// <summary>
/// A small pill that says a state or a count beside something, a tint of its tone behind words in the tone, with an
/// icon or a dot in front (the dot can beat, for something going on now). As wide as what it says, as tall as a small
/// line of text. Colour is never all it says: the words are always there.
/// <code>
/// let state = compositor.badge({ text: "Working 24s", tone: "info", dot: true, pulse: true });
/// </code>
/// </summary>
public class Badge : UIComponent
{
    // How far in the words are from the round ends, the gap after the dot or icon, and how much of a line tall it is
    private const float PAD = 7.0f, INNER_GAP = 5.0f, TALL = 1.45f;

    // How strong the tint behind is and the rim round it, and how long a beat of the dot takes
    private const float FILL = 0.16f, RIM = 0.32f, BEAT = 1.2f;

    // The blue of something going on, the same in every look, a theme's selection is its accent in some
    private static readonly Vector4 INFO = new(0.38f, 0.62f, 1.0f, 1.0f);

    /// <summary>What it says, plain text.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>An icon in front of the words, a name of <see cref="UISymbols"/>, empty for none.</summary>
    public string Icon { get; set; } = string.Empty;

    /// <summary>What its colour says.</summary>
    public UIBadgeTone Tone { get; set; }

    /// <summary>A colour of its own over the tone's, null for the tone's (an agent system's own colour).</summary>
    public Vector4? Colour { get; set; }

    /// <summary>A dot in front of the words (after the icon if it has one).</summary>
    public bool Dot { get; set; }

    /// <summary>Whether the dot beats, for something going on now. Still in a UI told not to move.</summary>
    public bool Pulse { get; set; }

    /// <summary>How big its words are, 0 for a little under the skin's.</summary>
    public float TextScale { get; set; }

    // How far into a beat the dot is
    private float beat;

    private float ScaleIn(UISkin skin) => TextScale > 0.0f ? TextScale : skin.TextScale * 0.85f;

    /// <summary>The colour it's drawn in, its own or its tone's in a skin.</summary>
    public Vector4 ColourIn(UISkin skin) => Colour ?? Tone switch
    {
        UIBadgeTone.Accent => skin.Palette.Accent,
        UIBadgeTone.Info => INFO,
        UIBadgeTone.Success => skin.Palette.Success,
        UIBadgeTone.Warning => skin.Palette.Warning,
        UIBadgeTone.Danger => skin.Palette.Error,
        _ => skin.TextColor
    };

    protected override Vector2 Measure(UISkin skin)
    {
        float scale = ScaleIn(skin);
        float line = skin.Font.LineHeight * scale, height = MathF.Round(line * TALL);
        float width = PAD * 2.0f + (Text.Length > 0 ? skin.Font.Measure(Text, scale, markup: false).X : 0.0f);
        if (Icon.Length > 0)
            width += line + (Text.Length > 0 ? INNER_GAP : 0.0f);
        if (Dot)
            width += line * 0.45f + (Text.Length > 0 || Icon.Length > 0 ? INNER_GAP : 0.0f);
        return new Vector2(MathF.Ceiling(MathF.Max(width, height)), height);
    }

    protected override void Update(float dt)
    {
        if (Dot && Pulse && MotionTime(BEAT) > 0.0f)
        {
            beat = (beat + dt / MotionTime(BEAT)) % 1.0f;
            GameEngine.Instance?.WindowManager?.RequestFrames(0.1);
        }
        else
            beat = 0.0f;

        base.Update(dt);
    }

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;
        Vector4 colour = ColourIn(skin);
        if (!EnabledInHierarchy)
            colour *= skin.DisabledTint;
        float scale = ScaleIn(skin), line = skin.Font.LineHeight * scale;
        UIRect bounds = Bounds;
        float round = bounds.Height * 0.5f;

        list.RoundRect(bounds, round, colour with { W = colour.W * FILL });
        list.RoundOutline(bounds, round, 1.0f, colour with { W = colour.W * RIM });

        float x = bounds.Min.X + PAD, middle = bounds.Center.Y;
        if (Icon.Length > 0)
        {
            list.Symbol(Icon, new UIRect(new Vector2(x, middle - line * 0.5f), new Vector2(x + line, middle + line * 0.5f)), colour);
            x += line + INNER_GAP;
        }

        if (Dot)
        {
            // A beat is the dot swelling a little and its glow going out from it, then still till the next
            float r = line * 0.225f;
            var at = new Vector2(x + r, middle);
            if (Pulse)
            {
                float glow = r * (1.0f + beat * 1.6f);
                list.RoundRect(new UIRect(at - new Vector2(glow), at + new Vector2(glow)), glow, colour with { W = colour.W * 0.45f * (1.0f - beat) });
            }

            list.RoundRect(new UIRect(at - new Vector2(r), at + new Vector2(r)), r, colour);
            x += r * 2.0f + INNER_GAP;
        }

        if (Text.Length > 0)
            list.Text(Text, new UIRect(new Vector2(x, bounds.Min.Y), new Vector2(bounds.Max.X - PAD + 1.0f, bounds.Max.Y)), Origin.Left, scale, colour, markup: false);
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("text", () => Text, value => Text = value);
        Expose("icon", () => Icon, value => Icon = value);
        Expose("tone", () => Tone.ToString().ToLowerInvariant(), value => Tone = Enum.TryParse(value, true, out UIBadgeTone tone) ? tone : UIBadgeTone.Neutral);
        Expose("dot", () => Dot, value => Dot = value);
        Expose("pulse", () => Pulse, value => Pulse = value);
        Expose("text_scale", () => TextScale, value => TextScale = value);
    }
}
