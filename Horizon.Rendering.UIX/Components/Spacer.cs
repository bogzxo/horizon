using System.Numerics;

using Horizon.Rendering.UIX.Drawing;
using Horizon.Rendering.UIX.Skinning;

namespace Horizon.Rendering.UIX.Components;

/// <summary>
/// Nothing, of a size. For a gap in a stack bigger than its spacing, without padding the things on either side of it.
/// <see cref="Space"/> is how big it is both ways, a <see cref="UIComponent.Size"/> of its own wins over that.
/// <code>
/// compositor.spacer({ parent: menu, space: 24 });
/// </code>
/// </summary>
public class Spacer : UIComponent
{
    /// <summary>How much room it takes, both ways. The skin's spacing unless it says.</summary>
    public float Space { get; set; }

    protected override Vector2 Measure(UISkin skin) => new(Space > 0.0f ? Space : skin.Spacing);

    protected override void Paint(UIDrawList list)
    { }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("space", () => Space, value => Space = MathF.Max(0.0f, value));
    }
}
