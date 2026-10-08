using System.Numerics;

using Horizon.Engine;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Hex;

// How big the editor draws itself and how it shares the window out between its parts.
internal sealed partial class HexScene
{
    /* How big the editor draws itself */

    private static string DescribeScale(float scale) => $"{scale * 100:0}%";

    /// <summary>The size a window is best read at when nobody has said. Bigger on a bigger screen.</summary>
    private static float ScaleFor(Vector2 window) => window.Y switch
    {
        >= 2000 => 2.0f,
        >= 1400 => 1.5f,
        >= 1000 => 1.25f,
        _ => 1.0f
    };

    private static string ScalePath => Path.Combine(AppContext.BaseDirectory, SCALE_FILE);

    private float? ReadScale()
    {
        // The editor testing itself starts out the same every time
        if (options.SelfTest || !File.Exists(ScalePath))
            return null;

        return float.TryParse(File.ReadAllText(ScalePath), System.Globalization.CultureInfo.InvariantCulture, out float saved) ? saved : null;
    }

    /// <summary>
    /// Sets how big the editor and the layouts in it are drawn. The editor makes room by itself. The lists,
    /// the canvas and the code get whatever the window has left at that size, see <see cref="FitChrome"/>.
    /// </summary>
    /// <param name="remember">Whether this is what the editor starts with from now on.</param>
    private void SetScale(float scale, bool remember)
    {
        scale = Scales.MinBy(known => MathF.Abs(known - scale));

        // The two are one screen as far as anybody looking at it is concerned
        compositor.Scale = stage.Scale = scale;

        if (!remember || options.SelfTest)
            return;

        try
        {
            File.WriteAllText(ScalePath, scale.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception e)
        {
            Say($"couldn't keep the scale for next time: {e.Message}", error: true);
        }
    }

    /// <summary>
    /// Gives the parts of the editor that have no size of their own the room the window has left. The list on
    /// the left and the inspector the height their columns have to spare, the canvas and the code the width
    /// between the two and the height between them. Everything else keeps the size its layout gives it.
    /// </summary>
    private void FitChrome()
    {
        UIRect screen = chrome.Root.Bounds;
        if (screen.IsEmpty || toolbar.Bounds.IsEmpty)
            return;

        float skinSpacing = compositor.Skin?.Spacing ?? 0.0f;
        float rowGap = root.Spacing >= 0.0f ? root.Spacing : skinSpacing;
        float columnGap = body.Spacing >= 0.0f ? body.Spacing : skinSpacing;

        // Between the toolbar on top and the line the editor talks in at the bottom
        float height = screen.Height - root.Padding.Total.Y - toolbar.Bounds.Height - status.Size.Y - rowGap * 2.0f;
        float leftWidth = treeScroll.Size.X;
        float rightWidth = inspectorScroll.Size.X;
        float centerWidth = MathF.Max(MIN_CENTER, screen.Width - root.Padding.Total.X - columnGap * 2.0f - leftWidth - rightWidth);

        // What a column needs for everything in it that isn't given its height here, as it was last laid out
        float Spoken(StackPanel column, params UIComponent[] flexible) =>
            column.Bounds.Height - flexible.Sum(part => part.Bounds.Height);

        treeScroll.Size = new Vector2(leftWidth, MathF.Max(MIN_FLEXIBLE, height - Spoken(left, treeScroll)));
        inspectorScroll.Size = new Vector2(rightWidth, MathF.Max(MIN_FLEXIBLE, height - Spoken(right, inspectorScroll)));
        status.Size = new Vector2(screen.Width - root.Padding.Total.X, status.Size.Y);

        // The canvas is the shape of the screen the layouts are made for, as long as that leaves room for the code
        float canvasHeight = MathF.Min(centerWidth * DesignSize.Y / DesignSize.X, height * CANVAS_SHARE);

        canvas.Size = new Vector2(centerWidth, MathF.Max(MIN_FLEXIBLE, canvasHeight));
        codeScroll.Size = new Vector2(centerWidth, MathF.Max(MIN_FLEXIBLE, height - Spoken(center, canvas, codeScroll) - canvas.Size.Y));
    }
}
