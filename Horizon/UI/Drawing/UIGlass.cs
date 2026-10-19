using System.Numerics;

namespace Horizon.UI.Drawing;

/// <summary>
/// Frosted glass, what a component sits on when it is to show a blur of whatever is behind it rather than its own
/// flat background, the way Windows 11 has its menus and its side panels. See <see cref="Components.UIComponent.Glass"/>
/// and <see cref="UICompositor.PopupGlass"/>.
/// <para>
/// A compositor that has any of it draws in goes, everything painted before the component, then a blurred copy of
/// what is on screen by then laid inside the component's bounds, then the component and whatever is over it. So the
/// glass shows the scene and the rest of the UI alike, a dialog over the code blurs the code. It costs a copy of the
/// frame and a blur for every pane that has something new under it, and only on frames the UI is drawn anew, a UI
/// that stands still lays its picture over as it always did.
/// </para>
/// </summary>
/// <param name="Blur">How far what is behind it is smeared, in the UI's pixels.</param>
/// <param name="Clarity">
/// How much of the blur shows through the background the component paints over it, 0 for none (it is as solid as it
/// ever was), 1 for nothing but the blur.
/// </param>
/// <param name="Rim">
/// How bright the silver lining is, the lit edge Apple's Liquid Glass has, brightest along the top and the left where
/// the light is taken to come from and faint along the others. 0 for none. A handful of quads painted over the
/// component's background, it costs nothing to speak of.
/// </param>
/// <param name="Bend">
/// How far what is behind is bent at the edge of the pane, in the UI's pixels, the way a thick piece of glass pulls
/// what is under it in at its rim. 0 for flat glass. It's worked out in the pass that lays the pane down anyway, a
/// few sums a pixel and no reads more.
/// </param>
public readonly record struct UIGlass(float Blur = 18.0f, float Clarity = 0.35f, float Rim = 0.3f, float Bend = 6.0f)
{
    /// <summary>What a component sits on unless it says otherwise, a fair smear and most of its colour kept.</summary>
    public static UIGlass Frosted => new();

    /// <summary>Helper method for a background as it is painted over the glass, the see through part of it taken off its alpha.</summary>
    public Vector4 Over(Vector4 background) => background with { W = background.W * Math.Clamp(1.0f - Clarity, 0.0f, 1.0f) };
}

/// <summary>
/// One pane of glass in a list, where it is and from which quad on everything is painted over it. Everything is in
/// the camera's world, the way the quads are once the module and the tweens have placed them.
/// </summary>
/// <param name="Start">The first quad painted over the glass, everything before it is under it.</param>
/// <param name="Min">The bottom left corner of the pane.</param>
/// <param name="Max">Its top right one.</param>
/// <param name="ClipMin">The bottom left corner of what it's cut down to.</param>
/// <param name="ClipMax">And the top right of that.</param>
/// <param name="Radius">How round its corners are.</param>
/// <param name="Blur">How far it smears.</param>
/// <param name="Opacity">How much of it is there, a dialog fading in fades its glass in with it.</param>
/// <param name="Bend">How far it bends what is behind at its edge, see <see cref="UIGlass.Bend"/>.</param>
internal readonly record struct UIGlassPane(int Start, Vector2 Min, Vector2 Max, Vector2 ClipMin, Vector2 ClipMax, float Radius, float Blur, float Opacity, float Bend = 0.0f)
{
    /// <summary>
    /// Helper method for a pane between two, for a frame between two ticks. The clip is the newer one's, an unclipped
    /// pane's is infinite and infinity blended with itself is not a number.
    /// </summary>
    public static UIGlassPane Blend(in UIGlassPane a, in UIGlassPane b, float t) => b with
    {
        Min = Vector2.Lerp(a.Min, b.Min, t),
        Max = Vector2.Lerp(a.Max, b.Max, t),
        Opacity = a.Opacity + (b.Opacity - a.Opacity) * t
    };
}
