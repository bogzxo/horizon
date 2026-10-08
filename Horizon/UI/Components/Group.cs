using Horizon.Rendering;
using System.Numerics;

using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// A bunch of components that go together: moved, shown, hidden and animated as one. It draws nothing itself and is
/// as big as what's in it, measured around its middle, so a group is just "these things, here". Hex makes one out of
/// whatever is selected (ctrl+G) and takes it apart again (ctrl+shift+G), keeping everything where it was on screen.
/// <code>
/// let hud_left = compositor.group({ anchor: "top_left", pos: vec(40, -40) });
/// let health = compositor.progress_bar({ parent: hud_left, pos: vec(0, 20), size: vec(300, 24) });
/// let name = compositor.label({ parent: hud_left, pos: vec(0, -14), text: "Player 1" });
/// </code>
/// It's a <see cref="Panel"/> underneath, so it can still have a background or a template if you really want one.
/// </summary>
public class Group : Panel
{
    protected override Vector2 Measure(UISkin skin)
    {
        // Big enough for what's in it wherever it sits. A child placed from the middle reaches as far out as its
        // place plus half its size; one anchored to an edge just needs its own size, the group can't know better
        Vector2 half = Vector2.Zero;
        foreach (var child in ChildSpan)
        {
            if (!child.Visible)
                continue;

            Vector2 reach = child.Anchor == Origin.Center && child.Pivot is null or Origin.Center
                ? Vector2.Abs(child.Position) + child.DesiredSize * 0.5f
                : child.DesiredSize * 0.5f;

            half = Vector2.Max(half, reach);
        }

        return half * 2.0f + Padding.Total;
    }
}
