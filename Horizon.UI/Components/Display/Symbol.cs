using System.Numerics;

using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// One of the icons every skin has on its own, a Codicon by name (<see cref="UISymbols"/>), as big as a line of
/// text unless given a size, in the skin's text colour unless given one.
/// <code>
/// let saved = compositor.symbol({ icon: "check", color: vec(0.4, 0.9, 0.5, 1) });
/// </code>
/// </summary>
public class Symbol : UIComponent
{
    /// <summary>Which icon, "save", "git-branch", "error". A dash or an underscore is the same. Empty draws nothing.</summary>
    public string Icon { get; set; } = string.Empty;

    /// <summary>What colour it is drawn in, null for the skin's text colour.</summary>
    public Vector4? Color { get; set; }

    public Symbol()
    { }

    public Symbol(string icon)
    {
        Icon = icon;
    }

    protected override Vector2 Measure(UISkin skin) => new(MathF.Round(skin.Font.LineHeight * skin.TextScale));

    protected override void Paint(UIDrawList list)
    {
        if (Icon.Length == 0)
            return;

        Vector4 color = Color ?? list.Skin.TextColor;
        list.Symbol(Icon, Bounds.Shrink(Padding), EnabledInHierarchy ? color : color * list.Skin.DisabledTint);
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("icon", () => Icon, value => Icon = value);
        Expose("color", () => Color ?? Vector4.Zero, value => Color = value == Vector4.Zero ? null : value);
    }
}
