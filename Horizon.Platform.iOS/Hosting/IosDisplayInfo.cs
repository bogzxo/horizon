using System.Numerics;

using UIKit;

namespace Horizon.Platform.iOS;

/// <summary>
/// What the screen is like as the view sees it, in pixels and in points. UIKit counts in points and a phone has two
/// or three pixels to one, so everything the engine is told is turned into pixels here.
/// </summary>
/// <param name="ViewPixels">The view in pixels, what's drawn into.</param>
/// <param name="ViewPoints">The view in points, what touches come in.</param>
/// <param name="ScreenPixels">The screen the way it's turned right now, in pixels.</param>
/// <param name="Scale">Pixels to a point, 3 on most phones.</param>
public sealed record IosDisplayInfo(Vector2 ViewPixels, Vector2 ViewPoints, Vector2 ScreenPixels, float Scale)
{
    /// <summary>Measures a view, the main screen's numbers when it isn't in a window yet. Main thread.</summary>
    public static IosDisplayInfo Of(UIView? view)
    {
        UIScreen screen = view?.Window?.WindowScene?.Screen ?? UIScreen.MainScreen;
        float scale = (float)(view?.ContentScaleFactor ?? screen.NativeScale);
        if (scale <= 0.0f) scale = (float)screen.NativeScale;

        Vector2 screenPoints = new((float)screen.Bounds.Width, (float)screen.Bounds.Height);
        Vector2 points = view is null || view.Bounds.Width <= 0 ? screenPoints : new Vector2((float)view.Bounds.Width, (float)view.Bounds.Height);
        return new IosDisplayInfo(Pixels(points, scale), points, Pixels(screenPoints, scale), scale);
    }

    private static Vector2 Pixels(Vector2 points, float scale) => new(MathF.Round(points.X * scale), MathF.Round(points.Y * scale));
}
