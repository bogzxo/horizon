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
    // Said in full, new() of a record struct is the struct's own empty one and not the primary constructor with its
    // defaults, and frosted glass came out with no blur and no clarity, a solid panel over nothing
    public static UIGlass Frosted => new(Blur: 18.0f, Clarity: 0.35f, Rim: 0.3f, Bend: 6.0f);

    /// <summary>
    /// Clear glass with a bright rim and a strong lens, what Hex's glass sliders start at (its Fluid Glass). Blurred far
    /// enough that text behind it is colour and not words, a blur of 16 had the code under a palette ghosting through it.
    /// Half of what's behind comes through. Nearly three quarters of it had a palette over anything bright washed out to
    /// grey, the glass was all backdrop and its text had nothing to stand on.
    /// </summary>
    public static UIGlass Clear => new(Blur: 24.0f, Clarity: 0.55f, Rim: 0.5f, Bend: 12.0f);

    /// <summary>The glass of popups (dialogs, menus, a palette) out of four sliders, each held to what still looks like glass.</summary>
    public static UIGlass Lens(float blur, float clarity, float rim, float bend) =>
        new(Blur: Math.Clamp(blur, 0.0f, 64.0f), Clarity: Math.Clamp(clarity, 0.0f, 1.0f), Rim: Math.Clamp(rim, 0.0f, 1.0f), Bend: Math.Clamp(bend, 0.0f, 24.0f));

    /// <summary>
    /// The glass of a side panel out of the same sliders, a little more blur and a lot less rim and no bend. There's
    /// nothing on most of a side but a tree, and a lens on that much glass looks like the window's melting.
    /// </summary>
    public static UIGlass Pane(float blur, float clarity, float rim) =>
        new(Blur: Math.Clamp(blur * 1.2f, 0.0f, 64.0f), Clarity: Math.Clamp(clarity + 0.05f, 0.0f, 1.0f), Rim: Math.Clamp(rim * 0.35f, 0.0f, 1.0f), Bend: 0.0f);

    // How far towards white a background on glass that lets nothing through is lifted, frosted glass scatters the light.
    // A tenth was milk, a dark theme's popups came out grey
    private const float MILK = 0.05f;

    /// <summary>
    /// Helper method for a background as it is painted over the glass, the see through part of it taken off its alpha,
    /// and the less there is of that the milkier it is, a little towards white. Clear glass and frosted glass came out the
    /// same colour with only more or less of it, and nobody could tell the two looks apart.
    /// </summary>
    public Vector4 Over(Vector4 background)
    {
        float frost = Math.Clamp(1.0f - Clarity, 0.0f, 1.0f);
        Vector4 milky = Vector4.Lerp(background, Vector4.One, frost * MILK);
        return milky with { W = background.W * frost };
    }
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
