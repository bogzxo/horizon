using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.Rendering.UIX.Drawing;
using Horizon.Rendering.UIX.Skinning;

namespace Horizon.Rendering.UIX.Components;

/// <summary>
/// A progress bar the pointer can set: press anywhere along it, or drag, to move the value there.
/// </summary>
public class Slider : ProgressBar
{
    private const string HANDLE_REGION = "slider_handle";
    private const string HANDLE_FALLBACK_REGION = "button";
    private const float HANDLE_WIDTH = 18.0f;

    private IRuntimeValue? changedHandler;

    /// <summary>Where the slider is set, from 0 to 1.</summary>
    public float Value
    {
        get => Progress;
        set => Progress = value;
    }

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
        UIRect handle = UIRect.FromCenter(
            new Vector2(track.Min.X + track.Width * Value, Bounds.Center.Y),
            new Vector2(HANDLE_WIDTH, Bounds.Height));

        if (skin.TryGetRegion(HANDLE_REGION, out var region) || skin.TryGetRegion(HANDLE_FALLBACK_REGION, out region))
            list.NineSlice(region, handle, tint);
        else
            list.Rect(handle, skin.ControlColor * tint);

        if (enabled && (IsHovered || IsPressed))
            list.Outline(handle, 2.0f, skin.HighlightColor);
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
        Value = (point.X - track.Min.X) / track.Width;

        if (Value != previous)
        {
            OnChanged?.Invoke(Value);
            InvokeScript(changedHandler, new NumberValue(Value));
        }
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("value", () => Value, value => Value = value);
        Expose("on_changed", () => changedHandler ?? new NullValue(), value => changedHandler = value);
    }
}
