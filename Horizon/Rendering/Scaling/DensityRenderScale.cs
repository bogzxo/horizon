namespace Horizon.Rendering.Scaling;

/// <summary>
/// How <see cref="RenderScalePolicy.Density"/> picks a scale, out of the pixels the surface really has and how dense
/// the screen says it is (Android's density, pixels to a dp, 2 at xhdpi, the desktop's display scale). Never out of the
/// name of a device, the most pixels its screen could have, or a guess at its GPU.
/// <para>
/// Two things bring the scale down, whichever brings it down further wins. A screen denser than
/// <see cref="TargetDensity"/> gets a scene that dense and no denser, a 3.5 density phone draws at 2 / 3.5 of its pixels,
/// because past about that the eye at a phone's distance doesn't see the rest. A surface with more pixels than
/// <see cref="PixelBudget"/> is drawn at the scale that fits it in the budget, a 2560 by 1600 tablet at density 2 is
/// over a 1080p budget by 2 times, so it draws at 0.71 of each side. A screen that's neither dense nor big is drawn
/// at 1, which is no pass at all.
/// </para>
/// The result is rounded to <see cref="Step"/>, so a few pixels of a resize don't make a new target, kept between
/// <see cref="Minimum"/> and 1, and anything from <see cref="NativeFrom"/> up is 1, not worth a pass. Density is a
/// logical number the system gives, not inches and not a measure of the GPU. It says how big a dp is drawn, which is
/// what UI sizing goes by, and the UI is drawn at the window's pixels unless the frame is drawn whole (see
/// <see cref="RenderComposition"/>), so none of this shrinks it.
/// </summary>
public sealed record DensityRenderScale
{
    /// <summary>The density (pixels a dp) a scene is drawn at most, 2 being xhdpi.</summary>
    public float TargetDensity { get; init; } = 2.0f;

    /// <summary>The most pixels a scene is drawn with, a 1080p frame's unless told otherwise.</summary>
    public double PixelBudget { get; init; } = 1920.0 * 1080.0;

    /// <summary>The smallest scale it picks, whatever the screen.</summary>
    public float Minimum { get; init; } = 0.5f;

    /// <summary>What the scale is rounded to.</summary>
    public float Step { get; init; } = 0.05f;

    /// <summary>A scale from here up is drawn at 1, the few pixels it would save aren't worth the pass.</summary>
    public float NativeFrom { get; init; } = 0.95f;

    /// <summary>The usual, see the class remarks.</summary>
    public static DensityRenderScale Default { get; } = new();

    /// <summary>
    /// The scale for a surface of so many pixels on a screen that dense. Nothing that isn't a number (a density of NaN,
    /// a budget of 0) gets anywhere, it is taken as the usual value, so the answer is always between the minimum and 1.
    /// </summary>
    public float ScaleFor(uint width, uint height, float density)
    {
        if (width == 0 || height == 0)
            return 1.0f;

        float target = float.IsFinite(TargetDensity) && TargetDensity > 0.0f ? TargetDensity : Default.TargetDensity;
        double budget = double.IsFinite(PixelBudget) && PixelBudget > 0.0 ? PixelBudget : Default.PixelBudget;
        float minimum = float.IsFinite(Minimum) ? Math.Clamp(Minimum, RenderScaleSettings.SMALLEST, 1.0f) : Default.Minimum;
        float step = float.IsFinite(Step) && Step > 0.0f ? MathF.Min(Step, 0.5f) : Default.Step;
        float nativeFrom = float.IsFinite(NativeFrom) ? Math.Clamp(NativeFrom, minimum, 1.0f) : Default.NativeFrom;
        if (!float.IsFinite(density) || density <= 0.0f)
            density = 1.0f;

        double scale = 1.0;
        if (density > target)
            scale = target / density;

        double pixels = (double)width * height;
        if (pixels > budget)
            scale = Math.Min(scale, Math.Sqrt(budget / pixels));

        // Rounded down to the step, so the budget is kept, with a hair of slack for what floats make of 0.7 / 0.05
        float stepped = (float)(Math.Floor(scale / step + 1e-4) * step);
        if (stepped >= nativeFrom)
            return 1.0f;

        return Math.Clamp(stepped, minimum, 1.0f);
    }
}
