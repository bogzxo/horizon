using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// A progress bar the pointer can set. Press anywhere along it, or drag, to move the value there.
/// </summary>
public class Slider : ProgressBar
{
    /// <summary>The name of the skin region the handle is drawn with, the bar under it goes by <see cref="ProgressBar.Style"/>.</summary>
    public string HandleStyle { get; set; } = "slider_handle";

    private const string HANDLE_FALLBACK_REGION = "button";
    private const float HANDLE_WIDTH = 18.0f;

    private IRuntimeValue? changedHandler;

    /// <summary>The value at the left end of the slider.</summary>
    public float Min { get; set; }

    /// <summary>The value at the right end of the slider.</summary>
    public float Max { get; set; } = 1.0f;

    /// <summary>What the value is a whole multiple of (counted from <see cref="Min"/>), zero for anything in between.</summary>
    public float Step { get; set; }

    /// <summary>Where the slider is set, from <see cref="Min"/> to <see cref="Max"/> (0 to 1 unless told otherwise).</summary>
    public float Value
    {
        get => Snap(Min + Progress * (Max - Min));
        set => Progress = Max != Min ? (Snap(value) - Min) / (Max - Min) : 0.0f;
    }

    // The nearest value that is a whole number of steps from the start, which is also what keeps a value that
    // went through the bar (a fraction of its length) from coming back a hair off.
    private float Snap(float value) =>
        Step > 0.0f ? Min + MathF.Round((value - Min) / Step, MidpointRounding.AwayFromZero) * Step : value;

    /// <summary>Called with the new value whenever the pointer changes it.</summary>
    public Action<float>? OnChanged { get; set; }

    public Slider()
    {
        ShowText = false;
    }

    protected override bool HitTestVisible => true;

    protected internal override bool Navigable => true;

    protected internal override bool OnAdjust(int step)
    {
        if (!EnabledInHierarchy || Max == Min)
            return false;

        // A step of its own if it has them, otherwise a twentieth of the way
        float previous = Value;
        float by = Step > 0.0f ? Step : (Max - Min) / 20.0f;
        Value = Math.Clamp(previous + Math.Sign(step) * by, MathF.Min(Min, Max), MathF.Max(Min, Max));

        if (Value != previous)
        {
            OnChanged?.Invoke(Value);
            InvokeScript(changedHandler, new NumberValue(Value));
        }

        return true;
    }

    // A slider of a skin with no art, a thin groove and a round knob, how big each is
    private const float GROOVE = 4.0f, KNOB = 18.0f;

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;
        if (!HasArt(skin) && !skin.TryGetRegion(HandleStyle, out _))
        {
            PaintFlat(list);
            return;
        }

        base.Paint(list);

        bool enabled = EnabledInHierarchy;
        Vector4 tint = enabled ? Vector4.One : skin.DisabledTint;

        UIRect track = GetTrack(skin);

        // A handle the skin has art for is as big as its art, otherwise it is a bar as tall as the slider.
        bool hasHandle = skin.TryGetRegion(HandleStyle, out var region);
        UIRect handle = UIRect.FromCenter(
            new Vector2(track.Min.X + track.Width * Progress, Bounds.Center.Y),
            hasHandle ? region.Size : new Vector2(HANDLE_WIDTH, Bounds.Height));

        if (hasHandle)
            list.Region(region, handle, tint);
        else if (skin.TryGetRegion(HANDLE_FALLBACK_REGION, out region))
            list.NineSlice(region, handle, tint);
        else
            list.Box(handle, skin.ControlTextColor * tint);

        if (enabled && (IsHovered || IsPressed || IsSelected))
            list.Frame(handle, 2.0f, skin.HighlightColor);

        PaintSelection(list);
    }

    /// <summary>
    /// A slider with no art, the way a Mac or a phone has one. A thin rounded groove, the accent along it as far as the
    /// value, and a round knob in the light with a soft shadow under it that gets a ring of the accent under the pointer.
    /// It was a box as tall as the row filled with gold and a slab for a handle.
    /// </summary>
    private void PaintFlat(UIDrawList list)
    {
        UISkin skin = list.Skin;
        bool enabled = EnabledInHierarchy;
        Vector4 tint = enabled ? Vector4.One : skin.DisabledTint;

        float knob = MathF.Min(KNOB, Bounds.Height);
        float left = Bounds.Min.X + knob * 0.5f, right = Bounds.Max.X - knob * 0.5f;
        float y = Bounds.Center.Y, x = left + (right - left) * Progress;
        var groove = new UIRect(new Vector2(left, y - GROOVE * 0.5f), new Vector2(right, y + GROOVE * 0.5f));

        list.RoundRect(groove, GROOVE * 0.5f, skin.TextColor with { W = 0.16f } * tint);
        list.RoundRect(new UIRect(groove.Min, new Vector2(MathF.Max(groove.Min.X + GROOVE, x), groove.Max.Y)), GROOVE * 0.5f, skin.AccentColor * tint);

        UIRect handle = UIRect.FromCenter(new Vector2(x, y), new Vector2(knob));
        bool lit = enabled && (IsHovered || IsPressed || IsSelected);
        list.Shadow(handle, knob * 0.5f, 5.0f, new Vector4(0.0f, 0.0f, 0.0f, 0.35f), new Vector2(0.0f, -1.0f));
        list.RoundOutline(UIRect.FromCenter(handle.Center, new Vector2(knob + 8.0f)), knob * 0.5f + 4.0f, 4.0f, skin.HighlightColor with { W = lit ? 0.3f : 0.0f });
        list.RoundRect(handle, knob * 0.5f, new Vector4(0.96f, 0.97f, 0.98f, 1.0f) * tint);

        PaintSelection(list);
    }

    // Where along it the value goes from its least to its most, the middle of the knob's travel for a slider with no art
    private UIRect Travel(UISkin skin)
    {
        if (HasArt(skin) || skin.TryGetRegion(HandleStyle, out _))
            return GetTrack(skin);

        float knob = MathF.Min(KNOB, Bounds.Height);
        return new UIRect(new Vector2(Bounds.Min.X + knob * 0.5f, Bounds.Min.Y), new Vector2(Bounds.Max.X - knob * 0.5f, Bounds.Max.Y));
    }

    protected internal override void OnPointerDown(Vector2 point) => MoveTo(point);

    protected internal override void OnPointerDrag(Vector2 point) => MoveTo(point);

    private void MoveTo(Vector2 point)
    {
        if (Module?.Compositor.Skin is not { } skin)
            return;

        UIRect track = Travel(skin);
        if (track.Width <= 0.0f)
            return;

        float previous = Value;
        Value = Min + Math.Clamp((point.X - track.Min.X) / track.Width, 0.0f, 1.0f) * (Max - Min);

        if (Value != previous)
        {
            OnChanged?.Invoke(Value);
            InvokeScript(changedHandler, new NumberValue(Value));
        }
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        // The ends come before the value in a script, which is measured against them.
        Expose("min", () => Min, value => { float kept = Value; Min = value; Value = kept; });
        Expose("max", () => Max, value => { float kept = Value; Max = value; Value = kept; });
        Expose("step", () => Step, value => Step = MathF.Max(0.0f, value));
        Expose("handle", () => HandleStyle, value => HandleStyle = value);
        Expose("value", () => Value, value => Value = value);
        Expose("on_changed", () => changedHandler ?? new NullValue(), value => changedHandler = value);
    }
}
