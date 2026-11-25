using System.Numerics;
using System.Runtime.InteropServices;

namespace Horizon.UI.Drawing;

/// <summary>
/// Frosted and liquid glass, what a component sits on when it shows a blur and bevel refraction of whatever is
/// behind it rather than its own flat background. See <see cref="Components.UIComponent.Glass"/> and
/// <see cref="UICompositor.PopupGlass"/>.
/// </summary>
/// <param name="Blur">How far what is behind it is smeared, in the UI's pixels.</param>
/// <param name="Clarity">
/// How much of the blur shows through the background the component paints over it, 0 for none (solid), 1 for all of it.
/// </param>
/// <param name="Bend">
/// How far what is behind is bent at the edge of the pane, in the UI's pixels, the way a thick piece of glass pulls
/// what is under it in round its bevel. 0 for flat frosted glass.
/// </param>
public readonly record struct UIGlass(float Blur = 18.0f, float Clarity = 0.35f, float Bend = 4.0f)
{
    /// <summary>Maximum bevel bend strength, in UI pixels.</summary>
    public const float MAX_BEND = 24.0f;

    /// <summary>What a component sits on unless it says otherwise, a fair smear and most of its colour kept.</summary>
    public static UIGlass Frosted => new(Blur: 18.0f, Clarity: 0.35f, Bend: 4.0f);

    /// <summary>Clear liquid glass with a strong bevel lens, what Hex's Fluid Glass starts at.</summary>
    public static UIGlass Clear => new(Blur: 3.0f, Clarity: 0.82f, Bend: 16.0f);

    /// <summary>The glass of popups (dialogs, menus, a palette) out of three sliders, held to valid bounds.</summary>
    public static UIGlass Lens(float blur, float clarity, float bend) =>
        new(Blur: Math.Clamp(blur, 0.0f, 64.0f), Clarity: Math.Clamp(clarity, 0.0f, 1.0f), Bend: Math.Clamp(bend, 0.0f, MAX_BEND));

    /// <summary>
    /// Helper method for a background as it is painted over the glass, the see-through part of it taken off its alpha
    /// with its colour left untouched.
    /// </summary>
    public Vector4 Over(Vector4 background) =>
        background with { W = background.W * Math.Clamp(1.0f - Clarity, 0.0f, 1.0f) };

    /// <summary>Strength of the optical dye tint applied inside the glass shader, derived from <see cref="Clarity"/>.</summary>
    public float Dye => Math.Clamp(1.07f - 0.6f * Clarity, 0.25f, 0.8f);

    /// <summary>Returns <paramref name="background"/> with its alpha scaled by <see cref="Dye"/> for shader tinting.</summary>
    public Vector4 DyeColor(Vector4 background) => background with { W = background.W * Dye };
}

/// <summary>
/// Derived liquid-glass optical parameters for one pane in screen pixels, shared between capture-region padding in
/// <see cref="UICompositor"/> and uniform upload in <see cref="UIGlassPass"/>.
/// </summary>
internal readonly record struct UIGlassOptics(float BevelWidth, float Refraction, float BevelDepth, float Magnify, float Aberration)
{
    /// <summary>Extra pixels around the visible pane required in the captured backdrop so refracted and dispersed samples stay in bounds.</summary>
    public float PaddingPixels => BevelWidth <= 0.0f ? 2.0f : MathF.Ceiling((Refraction + BevelDepth) * (1.0f + Aberration)) + 2.0f + RIM_SPREAD;

    /// <summary>How far to either side of where the rim reads the shader's extra taps go, the strip a pixel of the rim takes in (glass.slang).</summary>
    public const float RIM_SPREAD = 8.0f;

    public static UIGlassOptics Compute(float bendPx, float lens, float minBox)
    {
        if (lens <= 0.0f || bendPx <= 0.0f || minBox < 8.0f)
            return new UIGlassOptics(0.0f, 0.0f, 0.0f, 1.0f, 0.0f);

        float bevelWidth = Math.Clamp(3.0f * bendPx, 4.0f, 0.32f * minBox);
        float refraction = MathF.Min(2.2f * bendPx, 0.11f * minBox);
        // The lip folds in what is up to a bevel and a half outside the pane. Further (it was three bevels) squeezed a
        // dozen lines of code into every pixel of the rim, which over text is a shimmer of stripes and not glass
        float bevelDepth = MathF.Min(4.5f * bendPx, 0.24f * minBox);
        float magnify = 1.0f + 0.12f * lens;

        // The colours part by a quarter of the bend at the most. At three quarters every letter near the rim was three
        // letters, one red, one green and one blue, a hand's width apart
        float aberration = 0.25f * lens;
        return new UIGlassOptics(bevelWidth, refraction, bevelDepth, magnify, aberration);
    }
}

/// <summary>
/// One pane of glass in a list, where it is, up to which quad what is behind it is captured (<see cref="Under"/>),
/// and from which quad on everything is painted over it (<see cref="Start"/>).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct UIGlassPane(
    int Start,
    Vector2 Min,
    Vector2 Max,
    Vector2 ClipMin,
    Vector2 ClipMax,
    float Radius,
    float Blur,
    float Opacity,
    float Bend = 0.0f,
    int Under = -1,
    float Lens = -1.0f,
    uint Tint = 0u)
{
    public int Under { get; init; } = Under < 0 ? Start : Under;
    public float Lens { get; init; } = Lens < 0.0f ? Math.Clamp(Bend / UIGlass.MAX_BEND, 0.0f, 1.0f) : Lens;

    public static UIGlassPane Blend(in UIGlassPane a, in UIGlassPane b, float t) => b with
    {
        Min = Vector2.Lerp(a.Min, b.Min, t),
        Max = Vector2.Lerp(a.Max, b.Max, t),
        Opacity = a.Opacity + (b.Opacity - a.Opacity) * t,
        Bend = a.Bend + (b.Bend - a.Bend) * t,
        Lens = a.Lens + (b.Lens - a.Lens) * t
    };
}
