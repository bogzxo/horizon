using System.Numerics;

using Horizon.Rendering.UIX.Drawing;
using Horizon.Rendering.UIX.Skinning;

namespace Horizon.Rendering.UIX.Components;

/// <summary>
/// A bar that fills up from the left, with how full it is written on top as a percentage.
/// Unless given a size it is as big as its frame art.
/// </summary>
public class ProgressBar : UIComponent
{
    private const string FRAME_REGION = "progress_frame";
    private const string FILL_REGION = "progress_fill";

    private static readonly Vector2 FallbackSize = new(256.0f, 32.0f);

    private float progress = 1.0f;

    /// <summary>How full the bar is, from 0 to 1.</summary>
    public float Progress
    {
        get => progress;
        set => progress = Math.Clamp(value, 0.0f, 1.0f);
    }

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

        // The fill is always drawn at the size of a full bar and cut off where the progress ends, so
        // its pattern stays put instead of being squashed as the bar empties.
        list.PushClip(new UIRect(track.Min, new Vector2(track.Min.X + track.Width * progress, track.Max.Y)));
        if (skin.TryGetRegion(FILL_REGION, out var fill))
            list.NineSlice(fill, track, tint);
        else
            list.Rect(track, skin.AccentColor * tint);
        list.PopClip();

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
        Expose("show_text", () => ShowText, value => ShowText = value);
        Expose("text_scale", () => TextScale, value => TextScale = value);
    }
}
