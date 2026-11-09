namespace Horizon.Rendering.Scaling;

/// <summary>
/// What a frame does about its size, worked out from the settings, the window and the screen by <see cref="For"/>.
/// Nothing but numbers, the same answer for the same question every time, so it's what the tests check and what the
/// log and the overlays say.
/// </summary>
/// <param name="OutputWidth">How wide the window is, in pixels, what the frame ends up as.</param>
/// <param name="OutputHeight">How high the window is.</param>
/// <param name="SceneWidth">How wide the scene is drawn, the same as the window when nothing is scaled.</param>
/// <param name="SceneHeight">How high the scene is drawn.</param>
/// <param name="Density">How dense the screen said it is (pixels a dp), 1 where it didn't say.</param>
/// <param name="Policy">How the scale was picked.</param>
/// <param name="RequestedScale">The scale that was asked for or worked out by the policy, after it was put in range.</param>
/// <param name="Requested">The upscaler that was asked for.</param>
/// <param name="Effective">The upscaler that runs, which is another one when the one asked for can't do these sizes.</param>
/// <param name="Composition">What's drawn small and what isn't.</param>
/// <param name="Sharpness">CAS's sharpness, 0 to 1.</param>
/// <param name="Fallback">Why <paramref name="Effective"/> isn't <paramref name="Requested"/>, null when it is.</param>
/// <param name="PrepassWidth">
/// How wide the scene is stretched bilinearly before CAS takes it the rest of the way, 0 when CAS reaches in one go
/// (see <see cref="UpscaleLimit.Prepass"/>).
/// </param>
/// <param name="PrepassHeight">How high that stretch is.</param>
public sealed record RenderScalePlan(
    uint OutputWidth, uint OutputHeight, uint SceneWidth, uint SceneHeight, float Density,
    RenderScalePolicy Policy, float RequestedScale, Upscaler Requested, Upscaler Effective,
    RenderComposition Composition, float Sharpness, string? Fallback, uint PrepassWidth = 0, uint PrepassHeight = 0)
{
    /// <summary>Whether the scene is drawn at another size than the window, which is when there's a target and a pass.</summary>
    public bool Active => SceneWidth != OutputWidth || SceneHeight != OutputHeight;

    /// <summary>Whether CAS runs after a bilinear stretch to half the window, because the scene is further away than it reaches.</summary>
    public bool HasPrepass => PrepassWidth > 0 && PrepassHeight > 0;

    /// <summary>The scale the scene really comes out at across, after it was rounded to whole pixels.</summary>
    public float EffectiveScale => OutputWidth == 0 ? 1.0f : SceneWidth / (float)OutputWidth;

    /// <summary>How big the window is in dp, its pixels over the density.</summary>
    public (float Width, float Height) LogicalSize => (OutputWidth / Density, OutputHeight / Density);

    /// <summary>Nothing scaled, for a window of a size.</summary>
    public static RenderScalePlan NativeFor(uint width, uint height, float density = 1.0f) =>
        new(width, height, width, height, density, RenderScalePolicy.Native, 1.0f, Upscaler.Cas, Upscaler.Cas, RenderComposition.NativeOverlay, 0.0f, null);

    /// <summary>
    /// Works out what a frame does. A window of nothing (no surface) has a plan of nothing, which isn't active. The
    /// scene is at least a pixel each way and no bigger than the window or <paramref name="maxTextureSize"/>. A scale
    /// rounds each side to the nearest pixel, halves away from zero. The automatic policy makes the short edge exactly
    /// its pixels and rounds the long one to keep the window's shape.
    /// </summary>
    public static RenderScalePlan For(in RenderScaleSettings requested, uint outputWidth, uint outputHeight, float density, uint maxTextureSize)
    {
        RenderScaleSettings settings = requested.Sanitized();
        if (!float.IsFinite(density) || density <= 0.0f)
            density = 1.0f;

        uint most = Math.Max(1, maxTextureSize);
        uint sceneWidth, sceneHeight;
        float scale;

        if (settings.Policy == RenderScalePolicy.Automatic)
        {
            (sceneWidth, sceneHeight) = ShortEdge(outputWidth, outputHeight, settings.ShortEdge);
            uint shortSide = Math.Min(outputWidth, outputHeight);
            scale = shortSide == 0 ? 1.0f : Math.Min(sceneWidth, sceneHeight) / (float)shortSide;
            sceneWidth = Math.Min(sceneWidth, most);
            sceneHeight = Math.Min(sceneHeight, most);
        }
        else
        {
            scale = settings.Policy switch
            {
                RenderScalePolicy.Manual => settings.Scale,
                RenderScalePolicy.Density => (settings.Density ?? DensityRenderScale.Default).ScaleFor(outputWidth, outputHeight, density),
                _ => 1.0f
            };

            sceneWidth = Shrink(outputWidth, scale, most);
            sceneHeight = Shrink(outputHeight, scale, most);
        }

        Upscaler effective = settings.Upscaler;
        string? fallback = null;
        bool active = sceneWidth != outputWidth || sceneHeight != outputHeight;

        if (effective == Upscaler.Custom)
        {
            if (settings.Custom is not { } custom)
            {
                effective = Upscaler.Cas;
                fallback = "no custom upscaler was given, CAS does it";
            }
            else if (active && !custom.Supports(sceneWidth, sceneHeight, outputWidth, outputHeight))
            {
                effective = Upscaler.Cas;
                fallback = $"{custom.Name} can't take {sceneWidth}x{sceneHeight} to {outputWidth}x{outputHeight}, CAS does it";
            }
        }

        uint prepassWidth = 0, prepassHeight = 0;
        if (HasReach(effective) && sceneWidth > 0 && sceneHeight > 0 && !CasConstants.SupportsScaling(outputWidth, outputHeight, sceneWidth, sceneHeight))
        {
            if (settings.BeyondLimit == UpscaleLimit.Prepass)
            {
                (prepassWidth, prepassHeight) = CasReach(outputWidth, outputHeight, sceneWidth, sceneHeight);
            }
            else
            {
                fallback = (fallback is null ? "" : fallback + ", but ") + $"{NameOf(effective)} goes to 4 times the area at most in one go and {sceneWidth}x{sceneHeight} to {outputWidth}x{outputHeight} is more, bilinear does it as asked";
                effective = Upscaler.Bilinear;
            }
        }

        return new RenderScalePlan(outputWidth, outputHeight, sceneWidth, sceneHeight, density, settings.Policy, scale,
            settings.Upscaler, effective, settings.Composition, settings.Sharpness, fallback, prepassWidth, prepassHeight);
    }

    /// <summary>Whether an upscaler takes at most 4 times the area in one go, and gets the stretch ahead of it past that, see <see cref="UpscaleLimit"/>.</summary>
    // AMD documents the 4x area limit for CAS and FSR1 EASU. Qualcomm's GSR1 sample includes
    // 1240x576 -> 2400x1600 (~5.38x area), so CAS's limit must not be imposed on GSR1.
    public static bool HasReach(Upscaler upscaler) => upscaler is Upscaler.Cas or Upscaler.Fsr1;

    /// <summary>What an upscaler is called where people read it.</summary>
    public static string NameOf(Upscaler upscaler) => upscaler switch
    {
        Upscaler.Cas => "CAS",
        Upscaler.Gsr1 => "GSR 1",
        Upscaler.Fsr1 => "FSR 1",
        _ => upscaler.ToString()
    };

    /// <summary>
    /// The scene's size for a short edge of so many pixels, the long edge in the window's proportion rounded to the
    /// nearest pixel, halves away from zero. A window with a short edge of that or less is its own size.
    /// </summary>
    public static (uint Width, uint Height) ShortEdge(uint width, uint height, uint pixels)
    {
        uint shortSide = Math.Min(width, height);
        if (shortSide == 0 || shortSide <= pixels)
            return (width, height);

        uint longSide = Math.Max(width, height);
        uint scaledLong = (uint)Math.Max(1.0, Math.Round(longSide * (double)pixels / shortSide, MidpointRounding.AwayFromZero));
        return width <= height ? (pixels, scaledLong) : (scaledLong, pixels);
    }

    /// <summary>
    /// The smallest stretch CAS can take to the window in one go, half of it each way rounded up, which is never more
    /// than 4 times the area away from it, and never smaller than the scene itself.
    /// </summary>
    public static (uint Width, uint Height) CasReach(uint outputWidth, uint outputHeight, uint sceneWidth, uint sceneHeight)
    {
        uint width = Math.Max(sceneWidth, (outputWidth + 1) / 2);
        uint height = Math.Max(sceneHeight, (outputHeight + 1) / 2);
        return (Math.Min(width, outputWidth), Math.Min(height, outputHeight));
    }

    private static uint Shrink(uint size, float scale, uint most)
    {
        if (size == 0) return 0;
        if (scale >= 1.0f) return Math.Min(size, most);

        double scaled = Math.Round(size * (double)scale, MidpointRounding.AwayFromZero);
        return (uint)Math.Clamp(scaled, 1.0, Math.Min(size, most));
    }
}
