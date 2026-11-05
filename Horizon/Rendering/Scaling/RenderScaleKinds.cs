namespace Horizon.Rendering.Scaling;

/// <summary>How the size the scene is drawn at is picked, see <see cref="RenderScaleSettings"/>.</summary>
public enum RenderScalePolicy
{
    /// <summary>A pixel drawn for every pixel of the window, which is how it always was. No extra target, no extra pass.</summary>
    Native,

    /// <summary>The scene is drawn at <see cref="RenderScaleSettings.Scale"/> of the window's pixels, whatever the screen is like.</summary>
    Manual,

    /// <summary>
    /// The scene is drawn with its short edge <see cref="RenderScaleSettings.ShortEdge"/> pixels (720 unless told, 720p)
    /// and its long edge whatever keeps the window's own shape, so a 2560 by 1600 tablet is drawn at 1152 by 720, a 1080
    /// by 2400 phone at 720 by 1600 and a 1920 by 1080 window at 1280 by 720. A window whose short edge is that or less is
    /// drawn as it is, nothing is drawn bigger than the window. What every phone and tablet gets unless it's told otherwise.
    /// </summary>
    Automatic,

    /// <summary>
    /// The scale is worked out from the surface's pixels and how dense the screen is, see <see cref="DensityRenderScale"/>.
    /// The heuristic there was before the 720p default, kept for whoever asks for it.
    /// </summary>
    Density
}

/// <summary>
/// What CAS and FSR 1 do with a scene beyond their documented four-times-area range
/// (see <see cref="CasConstants.AREA_LIMIT"/>). This limit does not apply to GSR 1.
/// </summary>
public enum UpscaleLimit
{
    /// <summary>
    /// The scene is first stretched bilinearly to half of the window each way, which is as far as they reach, and the
    /// upscaler takes that to the window. The plan says so and the stretch is timed with the upscale. It keeps the
    /// upscaler inside what it's documented for, it doesn't make the picture any better than bilinear would.
    /// </summary>
    Prepass,

    /// <summary>Bilinear does the whole of it and the upscaler that was asked for doesn't run, said in the plan's fallback.</summary>
    Bilinear
}

/// <summary>
/// What blows the smaller scene up to the size of the window. Only what actually works is in here, a vendor's own
/// upscaler goes through <see cref="Custom"/> and an <see cref="ISpatialUpscaler"/>.
/// </summary>
public enum Upscaler
{
    /// <summary>
    /// AMD FidelityFX CAS, contrast adaptive sharpening with its upsampling path, ported from AMD's own header.
    /// Up to 4 times the area (half the size each way) in one go, further with a stretch first, see <see cref="UpscaleLimit"/>.
    /// </summary>
    Cas,

    /// <summary>Plain bilinear filtering. Soft, cheap, and fine for anything that isn't pixel art.</summary>
    Bilinear,

    /// <summary>Every pixel of the window takes the one scene pixel it lands in, for pixel art that wants its edges hard.</summary>
    Nearest,

    /// <summary>Whatever <see cref="RenderScaleSettings.Custom"/> does, CAS if there's none or it can't do the sizes.</summary>
    Custom,

    /// <summary>
    /// Snapdragon Game Super Resolution 1, a 12 tap lanczos like scaler and adaptive sharpening in one pass, ported from
    /// Qualcomm's shader. Made for Adreno, runs on any Vulkan GPU. Scales directly without CAS's four-times-area prepass.
    /// </summary>
    Gsr1,

    /// <summary>
    /// AMD FidelityFX Super Resolution 1, EASU's edge adaptive upsampling then RCAS's sharpening, two passes, ported from
    /// AMD's header. 1 to 4 times the area in one go, as the header says, see <see cref="UpscaleLimit"/>.
    /// </summary>
    Fsr1
}

/// <summary>Which of the frame is drawn small and blown up, and which is drawn at the window's own size.</summary>
public enum RenderComposition
{
    /// <summary>
    /// The scene is drawn small and the UI over it at the window's own pixels. The first UI drawn straight into the
    /// window ends the scene, which is blown up there and then, and the rest of the frame is drawn at full size in the
    /// order it always was. Text and hairlines stay sharp, and the UI costs what it always did.
    /// </summary>
    NativeOverlay,

    /// <summary>
    /// Everything, UI and all, is drawn small and the whole frame is blown up at its end. For an application that's
    /// nearly all UI (Hex) on a GPU that can't keep up with it at full size. The UI is still laid out, hit tested and
    /// touched at the window's size, only its pixels are fewer.
    /// </summary>
    WholeFrame
}

/// <summary>How a frame came out, see <see cref="FrameScaler.LastFrame"/>.</summary>
public enum ScaledFrame
{
    /// <summary>Drawn at the window's size, nothing scaled.</summary>
    Native,

    /// <summary>The scene drawn small and blown up, a UI over it at the window's size.</summary>
    Overlaid,

    /// <summary>The whole frame drawn small and blown up at its end.</summary>
    Whole
}
