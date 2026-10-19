using System.Numerics;

using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// A container. On its own it is invisible and just groups its children. Give it a
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

    /// <summary>How round the corners of a panel filled with a <see cref="Color"/> are, in pixels. Square unless it says so.</summary>
    public float Radius { get; set; }

    /// <summary>A thin line round the panel in this colour, the way a card has an edge. Null for none.</summary>
    public Vector4? Outline { get; set; }

    /// <summary>
    /// For a container whose content isn't known until the program runs (a row per save game, a cell per
    /// character). The layout file each of its items is made from, looked for next to the layout this panel
    /// is in. The program fills it with <see cref="UILayout.Populate(Panel, int)"/>.
    /// </summary>
    public string Template { get; set; } = string.Empty;

    /// <summary>
    /// How many items an editor shows in a container that has a <see cref="Template"/>, to stand in for the
    /// ones the program will make. It means nothing to the program itself.
    /// </summary>
    public int PreviewCount { get; set; }

    /// <summary>
    /// How many seconds later each child makes its entrance than the one before it (see
    /// <see cref="UIComponent.Intro"/>), which is what has a list pop up one row after the other.
    /// </summary>
    public float Stagger { get; set; }

    /// <summary>
    /// Whether what is in it is cut off at its edges, painted and pointed at, rather than spilling over whatever is next
    /// to it. For a panel of a fixed size somebody else fills (the view of an extension in Hex).
    /// </summary>
    public bool Clip { get; set; }

    protected override bool ClipsChildren => Clip;

    protected bool HasBackdrop => Background.Length > 0 || Color is not null;

    protected override bool HitTestVisible => HasBackdrop;

    protected override float GlassRadius(UISkin skin) =>
        skin.TryGetRegion(Background, out _) ? skin.CornerRadius : Radius;

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

    protected override void PaintChildren(UIDrawList list)
    {
        if (!Clip)
        {
            base.PaintChildren(list);
            return;
        }

        list.PushClip(Bounds);
        base.PaintChildren(list);
        list.PopClip();
    }

    protected override void Paint(UIDrawList list)
    {
        if (!HasBackdrop)
            return;

        // On glass the background is see through, as much as the glass says
        if (list.Skin.TryGetRegion(Background, out var region))
            list.NineSlice(region, Bounds, Glass is { } frosted ? frosted.Over(Color ?? Vector4.One) : Color ?? Vector4.One);
        else
            list.RoundRect(Bounds, Radius, Glass is { } glass ? glass.Over(Color ?? list.Skin.PanelColor) : Color ?? list.Skin.PanelColor);

        if (Outline is { W: > 0.0f } outline)
            list.RoundOutline(Bounds, Radius, 1.0f, outline);
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("background", () => Background, value => Background = value);
        Expose("template", () => Template, value => Template = value);
        Expose("preview_count", () => PreviewCount, value => PreviewCount = Math.Max(0, (int)value));
        Expose("stagger", () => Stagger, value => Stagger = MathF.Max(0.0f, value));
        Expose("color", () => Color ?? Vector4.Zero, value => Color = value);
        Expose("radius", () => Radius, value => Radius = MathF.Max(0.0f, value));
        Expose("clip", () => Clip, value => Clip = value);
        Expose("outline", () => Outline ?? Vector4.Zero, value => Outline = value.W > 0.0f ? value : null);
    }
}
