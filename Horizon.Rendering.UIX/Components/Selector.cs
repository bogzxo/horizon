using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.Rendering.UIX.Drawing;
using Horizon.Rendering.UIX.Skinning;

namespace Horizon.Rendering.UIX.Components;

/// <summary>
/// One out of a handful of options, shown between two arrows. Clicking its right half moves on to the next
/// option and its left half back to the one before, around and around.
/// For things there are only a few of. An anchor, a direction, a theme.
/// </summary>
public class Selector : UIComponent
{
    private const string REGION = "button_flat";
    private const string HOVER_REGION = "button_flat_hover";
    private const float DEFAULT_WIDTH = 220.0f;

    private IRuntimeValue? changedHandler;
    private string[] options = [];
    private int index;

    /// <summary>What there is to choose from, in the order it is stepped through.</summary>
    public string[] Options
    {
        get => options;
        set
        {
            options = value ?? [];
            index = Math.Clamp(index, 0, Math.Max(0, options.Length - 1));
        }
    }

    /// <summary>Which of the options is chosen.</summary>
    public int Index
    {
        get => index;
        set => index = options.Length == 0 ? 0 : Math.Clamp(value, 0, options.Length - 1);
    }

    /// <summary>
    /// The chosen option itself, empty if there are none. Setting it to something that isn't an option
    /// changes nothing.
    /// </summary>
    public string Value
    {
        get => options.Length > 0 ? options[index] : string.Empty;
        set
        {
            int found = Array.IndexOf(options, value);
            if (found >= 0)
                index = found;
        }
    }

    /// <summary>The scale of the text, or zero to use the skin's.</summary>
    public float TextScale { get; set; }

    /// <summary>Called with the newly chosen option whenever a click changes it.</summary>
    public Action<string>? OnChanged { get; set; }

    public Selector()
    { }

    public Selector(params string[] options)
    {
        Options = options;
    }

    protected override bool HitTestVisible => true;

    protected override Vector2 Measure(UISkin skin)
    {
        float scale = TextScale > 0.0f ? TextScale : skin.TextScale;
        float height = skin.Font.LineHeight * scale + skin.ButtonPadding.Total.Y;

        return new Vector2(DEFAULT_WIDTH, skin.TryGetRegion(REGION, out var region) ? MathF.Max(height, region.Size.Y * 0.6f) : height);
    }

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;

        bool enabled = EnabledInHierarchy;
        Vector4 tint = enabled ? Vector4.One : skin.DisabledTint;
        float scale = TextScale > 0.0f ? TextScale : skin.TextScale;

        if ((enabled && IsHovered && skin.TryGetRegion(HOVER_REGION, out var art)) || skin.TryGetRegion(REGION, out art))
            list.NineSlice(art, Bounds, tint);
        else
        {
            list.Box(Bounds, skin.ControlColor * tint);
            if (enabled && IsHovered)
                list.Box(Bounds, skin.HoverColor);
            if (skin.BorderColor.W > 0.0f)
                list.Frame(Bounds, 1.0f, skin.BorderColor * tint);
        }

        UIRect content = Bounds.Shrink(new UIEdges(10.0f, 0.0f));
        Vector4 arrows = skin.AccentColor * tint;

        list.Text("<", content, Origin.Left, scale, arrows, markup: false);
        list.Text(">", content, Origin.Right, scale, arrows, markup: false);
        list.Text(Value, content, Origin.Center, scale, skin.TextColor * tint, markup: false);
    }

    protected internal override void OnPointerUp(Vector2 point)
    {
        if (options.Length < 2 || !Bounds.Contains(point) || !EnabledInHierarchy)
            return;

        // Left of the middle goes back, right of it goes on.
        int step = point.X < Bounds.Center.X ? -1 : 1;
        index = (index + step + options.Length) % options.Length;

        OnChanged?.Invoke(Value);
        InvokeScript(changedHandler, new StringValue(Value));
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        // Scripts have no lists, so the options are one text with commas in it.
        Expose(
            "options",
            () => string.Join(", ", options),
            value => Options = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        Expose("value", () => Value, value => Value = value);
        Expose("text_scale", () => TextScale, value => TextScale = value);
        Expose("on_changed", () => changedHandler ?? new NullValue(), value => changedHandler = value);
    }
}
