using System.Numerics;

using Horizon.Rendering.UIX.Drawing;
using Horizon.Rendering.UIX.Scripting;
using Horizon.Rendering.UIX.Skinning;

namespace Horizon.Rendering.UIX.Components;

/// <summary>
/// A line between things. Across by default, for between the sections of a menu, or down for between columns.
/// Give it a size (or have it fill) for how long it is, otherwise it is a modest line of its own.
/// <code>
/// compositor.divider({ parent: menu, fill: "horizontal" });
/// </code>
/// </summary>
public class Divider : UIComponent
{
    private const float DEFAULT_LENGTH = 160.0f;

    /// <summary>Which way the line runs.</summary>
    public UIDirection Direction { get; set; } = UIDirection.Horizontal;

    /// <summary>How thick the line is, in pixels of the layout.</summary>
    public float Thickness { get; set; } = 1.0f;

    /// <summary>The colour of the line, or null for the skin's border colour (its text colour, faded, if the skin has no border).</summary>
    public Vector4? Color { get; set; }

    protected override Vector2 Measure(UISkin skin) =>
        Direction == UIDirection.Horizontal ? new Vector2(DEFAULT_LENGTH, Thickness) : new Vector2(Thickness, DEFAULT_LENGTH);

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;
        Vector4 color = Color ?? (skin.BorderColor.W > 0.0f ? skin.BorderColor : skin.TextColor * new Vector4(1.0f, 1.0f, 1.0f, 0.3f));

        // The line sits in the middle of whatever room the divider was given
        Vector2 center = Bounds.Center;
        UIRect line = Direction == UIDirection.Horizontal
            ? new UIRect(new Vector2(Bounds.Min.X, center.Y - Thickness * 0.5f), new Vector2(Bounds.Max.X, center.Y + Thickness * 0.5f))
            : new UIRect(new Vector2(center.X - Thickness * 0.5f, Bounds.Min.Y), new Vector2(center.X + Thickness * 0.5f, Bounds.Max.Y));

        list.Rect(line, color);
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("direction", () => UIScript.FromEnum(Direction), value => Direction = UIScript.ToEnum<UIDirection>(value, "direction"));
        Expose("thickness", () => Thickness, value => Thickness = MathF.Max(0.0f, value));
        Expose("color", () => Color ?? Vector4.Zero, value => Color = value);
    }
}
