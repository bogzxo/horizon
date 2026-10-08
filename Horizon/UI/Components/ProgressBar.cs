using Horizon.Rendering;
using System.Numerics;

using Horizon.UI.Drawing;
using Horizon.UI.Scripting;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// A bar that fills up from the left (or the right, see <see cref="Reverse"/>), with how full it is written on top as
/// a percentage unless told otherwise. Unless given a size it is as big as its frame art.
/// For a health bar there is a <see cref="Ghost"/>, a second fill behind the first that whoever owns the bar lets
/// drain a moment later, which is what shows how much a combo took off. For a meter there are <see cref="Segments"/>,
/// which cut the fill into so many pieces, and a <see cref="FillColor"/> of its own.
/// <code>
/// let meter = compositor.progress_bar({ size: vec(360, 18), show_text: false, segments: 4, fill_color: vec(1, 0.8, 0.2, 1) });
/// </code>
/// </summary>
public class ProgressBar : UIComponent
{
    private const string FRAME_REGION = "progress_frame";
    private const string FILL_REGION = "progress_fill";

    private static readonly Vector2 FallbackSize = new(256.0f, 32.0f);
    private static readonly Vector4 DefaultGhostColor = new(1.0f, 0.3f, 0.25f, 0.85f);

    private float progress = 1.0f;
    private float ghost;

    /// <summary>How full the bar is, from 0 to 1.</summary>
    public float Progress
    {
        get => progress;
        set => progress = Math.Clamp(value, 0.0f, 1.0f);
    }

    /// <summary>
    /// How full the fill behind the fill is, from 0 to 1. Drawn in <see cref="GhostColor"/> where it sticks out past
    /// the <see cref="Progress"/>, nothing at all while it is at or under it. The bar does nothing with it by itself,
    /// whoever owns the bar tweens it down after the progress.
    /// </summary>
    public float Ghost
    {
        get => ghost;
        set => ghost = Math.Clamp(value, 0.0f, 1.0f);
    }

    /// <summary>The colour of the ghost, a tired red unless told otherwise.</summary>
    public Vector4 GhostColor { get; set; } = DefaultGhostColor;

    /// <summary>The colour the fill is tinted with, or null for the skin's fill as it is.</summary>
    public Vector4? FillColor { get; set; }

    /// <summary>Whether the bar fills from the right instead of the left, for the bar of whoever is on the right of the screen.</summary>
    public bool Reverse { get; set; }

    /// <summary>How many pieces the fill is cut into, with a gap between them. One, for a bar that isn't.</summary>
    public int Segments { get; set; } = 1;

    /// <summary>How wide the gap between two segments is.</summary>
    public float SegmentGap { get; set; } = 3.0f;

    /// <summary>Whether the percentage is written on the bar.</summary>
    public bool ShowText { get; set; } = true;

    /// <summary>The scale of the percentage, or zero to use the skin's.</summary>
    public float TextScale { get; set; }

    public ProgressBar()
    { }

    public ProgressBar(in Vector2 position)
    {
        Position = position;
    }

    protected override Vector2 Measure(UISkin skin) =>
        skin.TryGetRegion(FRAME_REGION, out var frame) ? frame.Size : FallbackSize;

    /// <summary>The part of the bar the fill can occupy.</summary>
    protected UIRect GetTrack(UISkin skin) => Bounds.Shrink(skin.ProgressInset);

    /// <summary>
    /// Helper method to work out how much of the track so much of the bar covers, from whichever end it fills.
    /// </summary>
    private UIRect Covered(UIRect track, float amount)
    {
        float width = track.Width * amount;
        return Reverse
            ? new UIRect(new Vector2(track.Max.X - width, track.Min.Y), track.Max)
            : new UIRect(track.Min, new Vector2(track.Min.X + width, track.Max.Y));
    }

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;
        Vector4 tint = EnabledInHierarchy ? Vector4.One : skin.DisabledTint;

        UIRect track = GetTrack(skin);
        bool hasFrame = skin.TryGetRegion(FRAME_REGION, out var frame);

        if (!hasFrame)
            list.Box(Bounds, skin.ControlColor * tint);

        // Art that is a whole empty bar rather than just the rim of one goes underneath the fill.
        bool frameFirst = hasFrame && skin.ProgressFillOverFrame;
        if (frameFirst)
            list.NineSlice(frame, Bounds, tint);
        else
            list.Rect(track, skin.TrackColor * tint);

        // What was there a moment ago, behind the fill, for the eye to see what a hit took off
        if (ghost > progress)
            list.Rect(Covered(track, ghost), GhostColor * tint);

        // The fill is always drawn at the size of a full bar and cut off where the progress ends, so
        // its pattern stays put instead of being squashed as the bar empties.
        list.PushClip(Covered(track, progress));
        if (skin.TryGetRegion(FILL_REGION, out var fill))
            list.NineSlice(fill, track, (FillColor ?? Vector4.One) * tint);
        else
            list.Rect(track, (FillColor ?? skin.AccentColor) * tint);
        list.PopClip();

        // The gaps between the segments, cut through the fill and the ghost both
        if (Segments > 1 && SegmentGap > 0.0f)
        {
            Vector4 gap = frameFirst || !hasFrame ? skin.TrackColor * tint : skin.PanelColor * tint;
            if (gap.W <= 0.0f)
                gap = new Vector4(0.0f, 0.0f, 0.0f, 0.6f);

            for (int i = 1; i < Segments; i++)
            {
                float x = track.Min.X + track.Width * i / Segments;
                list.Rect(new UIRect(new Vector2(x - SegmentGap * 0.5f, track.Min.Y), new Vector2(x + SegmentGap * 0.5f, track.Max.Y)), gap);
            }
        }

        // The frame goes on top so its corners cover the square ends of the fill.
        if (hasFrame && !frameFirst)
            list.NineSlice(frame, Bounds, tint);

        if (ShowText)
        {
            // Formatted on the stack: this runs every update and shouldn't leave a string behind each time.
            Span<char> text = stackalloc char[8];
            ((int)MathF.Round(progress * 100.0f)).TryFormat(text, out int length);
            text[length++] = '%';

            float scale = TextScale > 0.0f ? TextScale : skin.TextScale;
            UIRect shadow = new(Bounds.Min + new Vector2(2.0f, -2.0f), Bounds.Max + new Vector2(2.0f, -2.0f));

            list.Text(text[..length], shadow, Origin.Center, scale, skin.TextShadowColor * tint);
            list.Text(text[..length], Bounds, Origin.Center, scale, skin.BarTextColor * tint);
        }
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("progress", () => Progress, value => Progress = value);
        Expose("ghost", () => Ghost, value => Ghost = value);
        Expose("ghost_color", () => GhostColor, value => GhostColor = value);
        Expose("fill_color", () => FillColor ?? Vector4.Zero, value => FillColor = value.W > 0.0f ? value : null);
        Expose("reverse", () => Reverse, value => Reverse = value);
        Expose("segments", () => Segments, value => Segments = Math.Max(1, (int)value));
        Expose("show_text", () => ShowText, value => ShowText = value);
        Expose("text_scale", () => TextScale, value => TextScale = value);
    }
}
