using System.Numerics;

using Android.App;
using Android.OS;
using Android.Util;

namespace Horizon.Platform.Android;

/// <summary>
/// What the screen the activity is on is like, as of one moment. Read on the main thread whenever the configuration
/// changes (a turn, a density change, a freeform window resized) and kept whole, so another thread never gets half of it.
/// </summary>
/// <param name="ScreenSize">
/// The activity's window in pixels as it is now, the way it's turned. The whole screen in fullscreen, less in split
/// screen or a freeform window (a Chromebook's, Samsung DeX), which is what there is to draw into.
/// </param>
/// <param name="Density">Pixels to a density independent pixel, 2 at xhdpi. The engine's display scale.</param>
/// <param name="DensityDpi">The density bucket in dots an inch, 320 at xhdpi. Logical, not the panel's real inches.</param>
/// <param name="RefreshRate">How many times a second the screen shows a new picture, as the display says now.</param>
/// <param name="Rotation">How far the screen is turned from its natural way, 0, 90, 180 or 270.</param>
/// <param name="MaximumSize">The most the activity's window could be (the screen, less nothing), for the log only.</param>
public sealed record AndroidDisplayInfo(Vector2 ScreenSize, float Density, int DensityDpi, float RefreshRate, int Rotation, Vector2 MaximumSize)
{
    /// <summary>How big the window is in dp, its pixels over the density.</summary>
    public Vector2 LogicalSize => ScreenSize / Density;

    /// <summary>What an activity's screen is like now. Main thread.</summary>
    public static AndroidDisplayInfo Of(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        DisplayMetrics metrics = activity.Resources?.DisplayMetrics ?? new DisplayMetrics();

        Vector2 window, maximum;
        global::Android.Views.Display? display;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
        {
            // The window the activity has now, which is less than the screen in a split or a freeform window
            global::Android.Graphics.Rect bounds = activity.WindowManager!.CurrentWindowMetrics.Bounds;
            window = new Vector2(bounds.Width(), bounds.Height());
            global::Android.Graphics.Rect most = activity.WindowManager.MaximumWindowMetrics.Bounds;
            maximum = new Vector2(most.Width(), most.Height());
            display = activity.Display;
        }
        else
        {
#pragma warning disable CA1422 // the way it was done before API 30, which is the only way there
            display = activity.WindowManager?.DefaultDisplay;
            var real = new DisplayMetrics();
            display?.GetRealMetrics(real);
            maximum = new Vector2(real.WidthPixels, real.HeightPixels);
            window = metrics.WidthPixels > 0 && metrics.HeightPixels > 0 ? new Vector2(metrics.WidthPixels, metrics.HeightPixels) : maximum;
#pragma warning restore CA1422
        }

        int rotation = display is null ? 0 : (int)display.Rotation * 90;
        return new AndroidDisplayInfo(window, metrics.Density > 0.0f ? metrics.Density : 1.0f, (int)metrics.DensityDpi, display?.RefreshRate ?? 60.0f, rotation, maximum);
    }
}
