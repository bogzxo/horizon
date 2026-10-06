using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.Rendering.UIX.Drawing;
using Horizon.Rendering.UIX.Skinning;

namespace Horizon.Rendering.UIX.Components;

/// <summary>
/// A progress bar the pointer can set. Press anywhere along it, or drag, to move the value there.
/// </summary>
public class Slider : ProgressBar
{
    private const string HANDLE_REGION = "slider_handle";
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

    protected override void Paint(UIDrawList list)
    {
        base.Paint(list);

        UISkin skin = list.Skin;
        bool enabled = EnabledInHierarchy;
        Vector4 tint = enabled ? Vector4.One : skin.DisabledTint;

        UIRect track = GetTrack(skin);

        // A handle the skin has art for is as big as its art, otherwise it is a bar as tall as the slider.
        bool hasHandle = skin.TryGetRegion(HANDLE_REGION, out var region);
        UIRect handle = UIRect.FromCenter(
            new Vector2(track.Min.X + track.Width * Progress, Bounds.Center.Y),
            hasHandle ? region.Size : new Vector2(HANDLE_WIDTH, Bounds.Height));

        if (hasHandle)
            list.Region(region, handle, tint);
        else if (skin.TryGetRegion(HANDLE_FALLBACK_REGION, out region))
            list.NineSlice(region, handle, tint);
        else
            list.Box(handle, skin.ControlTextColor * tint);

        if (enabled && (IsHovered || IsPressed))
            list.Frame(handle, 2.0f, skin.HighlightColor);
    }

    protected internal override void OnPointerDown(Vector2 point) => MoveTo(point);

    protected internal override void OnPointerDrag(Vector2 point) => MoveTo(point);

    private void MoveTo(Vector2 point)
    {
        if (Module?.Compositor.Skin is not { } skin)
            return;

        UIRect track = GetTrack(skin);
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
        Expose("value", () => Value, value => Value = value);
        Expose("on_changed", () => changedHandler ?? new NullValue(), value => changedHandler = value);
    }
}
