namespace Horizon.Rendering.Scaling;

/// <summary>
/// How big the scene is drawn for the size of the window, and how it's blown back up to it. The window, the camera,
/// the UI's layout, its display scale and where the pointer and the fingers are all stay the window's own, only the
/// pixels the scene is drawn with change.
/// <code>
/// new WindowManagerConfiguration { RenderScale = RenderScaleSettings.At(0.75f) }
/// engine.WindowManager.Scaling.Settings = RenderScaleSettings.At(0.5f) with { Upscaler = Upscaler.Nearest };
/// </code>
/// The default is <see cref="RenderScalePolicy.Native"/>, nothing changes and nothing extra is made or drawn. A phone
/// gets <see cref="Auto"/> unless the game says otherwise, the scene drawn at 720p (its short edge 720 pixels), see
/// <see cref="RenderScalePolicy.Automatic"/>. Whatever is
/// set is checked over by <see cref="Sanitized"/> before it's used, so no setting can make a target of nothing or of
/// more than the GPU allows.
/// </summary>
public readonly record struct RenderScaleSettings
{
    /// <summary>The smallest scale there is, a sixteenth of the pixels. Anything below is taken as this.</summary>
    public const float SMALLEST = 0.25f;

    /// <summary>What the automatic policy draws the short edge at unless told, 720p.</summary>
    public const uint DEFAULT_SHORT_EDGE = 720;

    /// <summary>The fewest pixels a short edge is asked for, anything fewer is taken as this.</summary>
    public const uint FEWEST_SHORT_EDGE = 16;

    /// <summary>How the scale is picked, native unless told.</summary>
    public RenderScalePolicy Policy { get; init; } = RenderScalePolicy.Native;

    /// <summary>
    /// The share of the window's width and height the scene is drawn at, for <see cref="RenderScalePolicy.Manual"/>.
    /// From <see cref="SMALLEST"/> to 1. Nothing is drawn bigger than the window, there is no supersampling here.
    /// </summary>
    public float Scale { get; init; } = 1.0f;

    /// <summary>What blows the scene up, CAS unless told.</summary>
    public Upscaler Upscaler { get; init; } = Upscaler.Cas;

    /// <summary>
    /// How hard CAS sharpens, 0 to 1, CAS's own knob. 0 is AMD's default, the least ringing, and still sharpens. A custom
    /// upscaler is handed it too.
    /// </summary>
    public float Sharpness { get; init; } = 0.0f;

    /// <summary>Whether the UI is drawn at the window's pixels over the blown up scene (the default) or is part of it.</summary>
    public RenderComposition Composition { get; init; } = RenderComposition.NativeOverlay;

    /// <summary>The upscaler for <see cref="Upscaler.Custom"/>, a platform's or a vendor's own. Null falls back to CAS.</summary>
    public ISpatialUpscaler? Custom { get; init; }

    /// <summary>
    /// How many pixels the short edge of the scene is for <see cref="RenderScalePolicy.Automatic"/>, 720 unless told
    /// (720p). Between <see cref="FEWEST_SHORT_EDGE"/> and 16384, 0 is 720.
    /// </summary>
    public uint ShortEdge { get; init; } = DEFAULT_SHORT_EDGE;

    /// <summary>What CAS does with a scene further from the window than it reaches in one go, a pass ahead of it unless told.</summary>
    public UpscaleLimit BeyondLimit { get; init; } = UpscaleLimit.Prepass;

    /// <summary>How <see cref="RenderScalePolicy.Density"/> decides, null for <see cref="DensityRenderScale.Default"/>.</summary>
    public DensityRenderScale? Density { get; init; }

    public RenderScaleSettings() { }

    /// <summary>Native, what there was before any of this.</summary>
    public static RenderScaleSettings Native { get; } = new();

    /// <summary>720p, the scene's short edge 720 pixels and its shape the window's, what phones and tablets start with.</summary>
    public static RenderScaleSettings Auto { get; } = new() { Policy = RenderScalePolicy.Automatic };

    /// <summary>The scene's short edge so many pixels, 1080 for 1080p.</summary>
    public static RenderScaleSettings ShortEdgeOf(uint pixels) => new() { Policy = RenderScalePolicy.Automatic, ShortEdge = pixels };

    /// <summary>A scale of the game's own, upscaled with CAS.</summary>
    public static RenderScaleSettings At(float scale) => new() { Policy = RenderScalePolicy.Manual, Scale = scale };

    /// <summary>
    /// The same settings with everything in range, which is what is used. A scale that isn't a number, is 0 or less, or
    /// is infinite is 1. One under <see cref="SMALLEST"/> is that, one over 1 is 1. Sharpness goes between 0 and 1 (0 when
    /// it isn't a number), and a policy, an upscaler or a composition nobody defined is the default one.
    /// </summary>
    public RenderScaleSettings Sanitized()
    {
        float scale = float.IsFinite(Scale) && Scale > 0.0f ? Math.Clamp(Scale, SMALLEST, 1.0f) : 1.0f;
        float sharpness = float.IsFinite(Sharpness) ? Math.Clamp(Sharpness, 0.0f, 1.0f) : 0.0f;

        return this with
        {
            Policy = Enum.IsDefined(Policy) ? Policy : RenderScalePolicy.Native,
            Scale = scale,
            Sharpness = sharpness,
            Upscaler = Enum.IsDefined(Upscaler) ? Upscaler : Upscaler.Cas,
            ShortEdge = ShortEdge == 0 ? DEFAULT_SHORT_EDGE : Math.Clamp(ShortEdge, FEWEST_SHORT_EDGE, 16384u),
            BeyondLimit = Enum.IsDefined(BeyondLimit) ? BeyondLimit : UpscaleLimit.Prepass,
            Composition = Enum.IsDefined(Composition) ? Composition : RenderComposition.NativeOverlay
        };
    }
}
