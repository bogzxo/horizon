using System.Numerics;

using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// How full something is, as a thin line with round ends and as much of it filled in. What a
/// <see cref="ProgressBar"/> is when it has no frame, no art and nothing written on it, for next to a number
/// (how busy a thread is, how much of a download there is) rather than in place of one.
/// <code>
/// let load = compositor.meter({ parent: row, size: vec(50, 4), value: 0.4 });
/// </code>
/// </summary>
public class Meter : UIComponent
{
    private static readonly Vector2 DefaultSize = new(50.0f, 4.0f);

    /// <summary>How full it is, from 0 for empty to 1 for full. Anything past either end is that end.</summary>
    public float Value { get; set; }

    /// <summary>The colour of what is filled in, or null for the skin's accent.</summary>
    public Vector4? Color { get; set; }

    /// <summary>The colour of the line it is filled in on, or null for the skin's text colour, nearly see-through.</summary>
    public Vector4? TrackColor { get; set; }

    protected override Vector2 Measure(UISkin skin) => DefaultSize;

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;
        UIRect track = Bounds;
        float round = track.Height * 0.5f;

        // Two parts whether there is anything in it or not
        list.BeginPart(0);
        list.RoundRect(track, round, TrackColor ?? skin.TextColor with { W = 0.09f });

        list.BeginPart(1);
        float filled = MathF.Round(track.Width * Math.Clamp(Value, 0.0f, 1.0f));
        if (filled >= track.Height)
            list.RoundRect(new UIRect(track.Min, new Vector2(track.Min.X + filled, track.Max.Y)), round, Color ?? skin.AccentColor);

        list.EndParts();
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("value", () => Value, value => Value = value);
        Expose("color", () => Color ?? Vector4.Zero, value => Color = value);
        Expose("track_color", () => TrackColor ?? Vector4.Zero, value => TrackColor = value);
    }
}
