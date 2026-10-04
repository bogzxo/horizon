using System.Numerics;

using Horizon.Rendering.UIX.Drawing;
using Horizon.Rendering.UIX.Skinning;

namespace Horizon.Rendering.UIX.Components;

/// <summary>
/// A container. On its own it is invisible and just groups its children; give it a
/// <see cref="Background"/> or a <see cref="Color"/> and it becomes a backdrop that also stops the
/// pointer from reaching what is behind it.
/// Unless given a size it wraps its largest child plus its padding.
/// </summary>
public class Panel : UIComponent
{
    /// <summary>The name of the skin region drawn behind the children, as a nine-slice.</summary>
    public string Background { get; set; } = string.Empty;

    /// <summary>
    /// A flat colour to fill the panel with, or the tint of <see cref="Background"/> when the skin has that region.
    /// </summary>
    public Vector4? Color { get; set; }

    private bool HasBackdrop => Background.Length > 0 || Color is not null;

    protected override bool HitTestVisible => HasBackdrop;

    protected override Vector2 Measure(UISkin skin)
    {
        Vector2 content = Vector2.Zero;
        foreach (var child in ChildSpan)
        {
            if (child.Visible)
                content = Vector2.Max(content, child.DesiredSize);
        }

        return content + Padding.Total;
    }

    protected override void Paint(UIDrawList list)
    {
        if (!HasBackdrop)
            return;

        if (list.Skin.TryGetRegion(Background, out var region))
            list.NineSlice(region, Bounds, Color ?? Vector4.One);
        else
            list.Rect(Bounds, Color ?? list.Skin.PanelColor);
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("background", () => Background, value => Background = value);
        Expose("color", () => Color ?? Vector4.Zero, value => Color = value);
    }
}
