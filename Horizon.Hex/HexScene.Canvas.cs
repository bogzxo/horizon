using System.Numerics;

using Horizon.Rendering.UIX;
using Horizon.Rendering.UIX.Components;

using Key = Silk.NET.Input.Key;
using MouseButton = Silk.NET.Input.MouseButton;

namespace Horizon.Hex;

// The canvas: zooming and panning about the layout, the screen it's previewed on, and what the layout says it's made for.
internal sealed partial class HexScene
{
    // How much a notch of the wheel zooms by, and how far in and out it goes
    private const float ZOOM_STEP = 1.15f;
    private const float MIN_ZOOM = 0.1f, MAX_ZOOM = 16.0f;

    // The edges of the layout, and of the screen it's previewed on when that's another shape
    private static readonly Vector4 FrameColor = new(0.38f, 0.62f, 1.0f, 0.9f);

    // Screens a layout can be made for, the usual suspects
    private static readonly Vector2[] Designs = [new(1600, 900), new(1920, 1080), new(1280, 720), new(2560, 1080), new(1080, 1920)];

    // Shapes of screen to try a layout out on: its own, and the ones that break layouts made for 16:9
    private static readonly (string Name, float Aspect)[] Previews =
    [
        ("its own shape", 0.0f), ("16:9", 16.0f / 9.0f), ("21:9, an ultrawide", 21.0f / 9.0f), ("32:9, a silly ultrawide", 32.0f / 9.0f),
        ("4:3", 4.0f / 3.0f), ("9:16, a phone", 9.0f / 16.0f)
    ];

    // How far past fitting the screen into the canvas it's zoomed in, and how far it's been dragged about (in the editor's units)
    private float canvasZoom = 1.0f;
    private Vector2 canvasPan;
    private float previewAspect;

    // Dragging the canvas about with the middle button, or space and the left one
    private bool panning;
    private Vector2 panLast;

    /// <summary>
    /// Helper method to work out the screen the layout is previewed on, in the layout's own units: the one it's made for,
    /// or that stretched into the shape that's being tried out (as tall as it, or as wide, whichever keeps it whole).
    /// </summary>
    private UIRect PreviewScreen()
    {
        Vector2 design = document.Design ?? DesignSize;
        if (previewAspect <= 0.0f)
            return UIRect.FromCenter(Vector2.Zero, design);

        float own = design.X / design.Y;
        Vector2 size = previewAspect >= own
            ? new Vector2(design.Y * previewAspect, design.Y)
            : new Vector2(design.X, design.X / previewAspect);

        return UIRect.FromCenter(Vector2.Zero, size);
    }

    /// <summary>
    /// Helper method to put the layout in the canvas: the preview screen fitted into it, then zoomed and panned however
    /// it's been, and cut off at the edges of the canvas.
    /// </summary>
    private void PlaceCanvas()
    {
        UIRect area = canvas.Bounds;
        if (area.IsEmpty)
            return;

        UIRect screen = PreviewScreen();
        float scale = CanvasFit(area, screen) * canvasZoom;

        // In the units of the editor's layout, the UI scales the two of them together
        var module = document.Module;
        module.Viewport = screen;
        module.Scale = new Vector2(scale);
        module.Position = area.Center + canvasPan;
        module.Clip = new UIRect((area.Min - module.Position) / scale, (area.Max - module.Position) / scale);
        module.FrameColor = FrameColor;
    }

    private static float CanvasFit(UIRect area, UIRect screen) => MathF.Min(area.Width / screen.Width, area.Height / screen.Height);

    /// <summary>
    /// Helper method to zoom the canvas by a factor, keeping whatever is under a point (in the editor's units) where it is.
    /// </summary>
    private void ZoomCanvas(float factor, Vector2 around)
    {
        var module = document.Module;
        float before = module.Scale.X;
        if (before <= 0.0f)
            return;

        float zoom = Math.Clamp(canvasZoom * factor, MIN_ZOOM, MAX_ZOOM);
        float after = before / canvasZoom * zoom;

        // What's under the point stays under it: the layout moves the other way as it grows
        Vector2 local = (around - module.Position) / before;
        canvasPan = around - local * after - canvas.Bounds.Center;
        canvasZoom = zoom;

        Say($"zoomed to {after * 100.0f / compositor.Scale:0}%");
    }

    private void ZoomToFit()
    {
        canvasZoom = 1.0f;
        canvasPan = Vector2.Zero;
        Say("zoomed to fit");
    }

    private void ZoomToActualSize()
    {
        // A unit of the layout a unit of the editor, which is a pixel at the editor's own scale of 1
        float fit = CanvasFit(canvas.Bounds, PreviewScreen());
        canvasZoom = fit > 0.0f ? 1.0f / fit : 1.0f;
        canvasPan = Vector2.Zero;
        Say("actual size");
    }

    /// <summary>
    /// Helper method to zoom with the wheel and pan with the middle button (or space and the left one) while the pointer is
    /// over the canvas. True while the canvas is being panned, which is all the pointer is doing then.
    /// </summary>
    private bool UpdateCanvasView(Vector2 inChrome, bool overCanvas, bool pressed)
    {
        var mouse = Engine.Input.Mouse;
        var keyboard = Engine.Input.Keyboard;

        if (overCanvas && mouse.Scroll != 0.0f && chrome.Popup is null)
            ZoomCanvas(MathF.Pow(ZOOM_STEP, mouse.Scroll), inChrome);

        bool spaceDrag = keyboard.IsDown(Key.Space) && compositor.Focus is null && stage.Focus is null;
        bool held = mouse.IsDown(MouseButton.Middle) || (spaceDrag && compositor.Pointer.Down);

        if (panning)
        {
            if (!held)
            {
                panning = false;
                return false;
            }

            canvasPan += inChrome - panLast;
            panLast = inChrome;
            return true;
        }

        if (overCanvas && held && (mouse.WasPressed(MouseButton.Middle) || pressed))
        {
            panning = true;
            panLast = inChrome;
            return true;
        }

        return false;
    }

    /* What the layout is made for */

    private void SetDesign(Vector2? size, UIFit fit)
    {
        document.SetDesign(size, fit);
        codeDirty = inspectorDirty = true;

        Say(size is { } made
            ? $"made for {made.X:0} x {made.Y:0}, {(fit == UIFit.Contain ? "keeping its shape in the middle of other screens" : "stretching out over other screens")}"
            : "made for any screen: laid out against whatever it's shown on");
    }

    private static string DescribeDesign(Vector2 size) => $"Made for {size.X:0} x {size.Y:0}";

    private void BuildLayoutMenu()
    {
        Menu made = menu.AddMenu("Layout");

        made.Add("Made for any screen", () => SetDesign(null, document.Fit)).IsChecked = () => document.Design is null;
        foreach (Vector2 size in Designs)
            made.Add(DescribeDesign(size), () => SetDesign(size, document.Fit)).IsChecked = () => document.Design == size;

        made.AddSeparator();
        made.Add("Keep its shape (contain)", () => SetDesign(document.Design ?? DesignSize, UIFit.Contain)).IsChecked =
            () => document.Design is not null && document.Fit == UIFit.Contain;
        made.Add("Take the whole screen (stretch)", () => SetDesign(document.Design ?? DesignSize, UIFit.Stretch)).IsChecked =
            () => document.Design is not null && document.Fit == UIFit.Stretch;

        made.AddSeparator();
        foreach (var (name, aspect) in Previews)
        {
            made.Add($"Preview on {name}", () =>
            {
                previewAspect = aspect;
                Say($"previewing on {name}");
            }).IsChecked = () => previewAspect == aspect;
        }
    }
}
