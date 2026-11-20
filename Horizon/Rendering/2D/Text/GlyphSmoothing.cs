using System.Runtime.InteropServices;

namespace Horizon.Rendering.Text;

/// <summary>How the edges of letters are smoothed, see <see cref="GlyphSmoothing"/>.</summary>
public enum TextSmoothing
{
    /// <summary>The way the system and the screen say: subpixels where Windows draws ClearType on a screen of ordinary density, grey everywhere else.</summary>
    Auto,

    /// <summary>Each of a pixel's red, green and blue stripes covered on its own, a third of a pixel sharper across, with colour at the edges up close.</summary>
    Subpixel,

    /// <summary>Grey edges, the same on every screen and in every screenshot.</summary>
    Grayscale
}

/// <summary>
/// Whether letters are drawn ClearType's way, a coverage for each of a pixel's stripes (<see cref="GlyphAtlas.Subpixel"/>),
/// or grey, and whether their x height is hinted, for a screen of a density. Cross platform by being careful:
/// <list type="bullet">
/// <item>Subpixels only where they're right. On Windows with ClearType on and its stripes across (RGB or BGR as it was
/// set up), on a screen under 150%, drawn straight into the window. Not on a Mac (Apple stopped drawing them in 2018,
/// its screens are Retina), a phone or a tablet (they turn, and a stripe across becomes one down), or on Linux unless
/// asked for (there's no one place it says). Not into a frame drawn small and blown up, where a stripe isn't a stripe.</item>
/// <item>Retina screens, twice the pixels and more, are drawn grey and unhinted the way the Mac draws them: a pixel is
/// so small that fitting the x height to a row only changes the letters' shape, and colour fringes would buy nothing.</item>
/// </list>
/// </summary>
public static partial class GlyphSmoothing
{
    /// <summary>What was asked for, the editor's setting. Auto unless told.</summary>
    public static TextSmoothing Mode { get; set; } = TextSmoothing.Auto;

    /// <summary>The screen's scale from which it's drawn grey whatever, under it ClearType is worth its fringes.</summary>
    public const float SUBPIXEL_BELOW = 1.5f;

    /// <summary>The screen's scale from which a screen is Retina, its letters unhinted.</summary>
    public const float RETINA = 1.75f;

    // What Windows says of ClearType, read the once: whether it's on, and whether its stripes go blue first
    private static readonly Lazy<(bool On, bool Bgr)> windows = new(ReadWindows);

    /// <summary>
    /// Whether letters on a screen of a scale are drawn with subpixels, and in which order their stripes go.
    /// </summary>
    /// <param name="displayScale">The screen's scale, 1 at 100%, 2 on a Retina screen.</param>
    /// <param name="native">Whether the UI is drawn at the window's own pixels, straight into it.</param>
    public static bool Subpixels(float displayScale, bool native, out bool bgr)
    {
        bgr = false;
        if (!native || Mode == TextSmoothing.Grayscale)
            return false;

        (bool on, bool blueFirst) = OperatingSystem.IsWindows() ? windows.Value : (false, false);
        bgr = blueFirst;
        if (Mode == TextSmoothing.Subpixel)
            return true;

        // Auto, the system's say and the screen's density
        return on && displayScale < SUBPIXEL_BELOW;
    }

    /// <summary>Whether letters are hinted on a screen of a scale, everything but Retina.</summary>
    public static bool Hints(float displayScale) => displayScale < RETINA;

    private const uint SPI_GETFONTSMOOTHING = 0x004A, SPI_GETFONTSMOOTHINGTYPE = 0x200A, SPI_GETFONTSMOOTHINGORIENTATION = 0x2012;
    private const uint FE_FONTSMOOTHINGCLEARTYPE = 2, FE_FONTSMOOTHINGORIENTATIONBGR = 0;

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint parameter, out uint value, uint update);

    private static (bool, bool) ReadWindows()
    {
        try
        {
            if (!SystemParametersInfo(SPI_GETFONTSMOOTHING, 0, out uint smoothing, 0) || smoothing == 0)
                return (false, false);
            if (!SystemParametersInfo(SPI_GETFONTSMOOTHINGTYPE, 0, out uint type, 0) || type != FE_FONTSMOOTHINGCLEARTYPE)
                return (false, false);
            // Orientation isn't said on older Windows, which only ever drew RGB
            bool bgr = SystemParametersInfo(SPI_GETFONTSMOOTHINGORIENTATION, 0, out uint orientation, 0) && orientation == FE_FONTSMOOTHINGORIENTATIONBGR;
            return (true, bgr);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return (false, false);
        }
    }
}
