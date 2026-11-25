using System.Numerics;

using Horizon.Engine;

using ThreeBody.Simulation;

namespace ThreeBody.Rendering;

/// <summary>Keeps every body in view by smoothly moving and zooming a camera that sees a given size of world.</summary>
public sealed class CameraFit
{
    private Vector2 centre;
    private float span = 4.0f;   // world units across the shorter screen side
    private bool first = true;

    public Vector2 Centre => centre;

    /// <summary>World units across the shorter screen side.</summary>
    public float Span => span;

    public void Snap() => first = true;

    /// <param name="leftInset">Screen pixels on the left covered by the HUD, the bodies are fitted into the rest.</param>
    public void Update(NBodySystem sim, Camera2D camera, Vector2 window, float dt, float leftInset = 0.0f)
    {
        var full = window;
        window = new Vector2(MathF.Max(1.0f, window.X - leftInset), window.Y);
        var (minX, minY, maxX, maxY) = sim.Bounds();
        var targetCentre = new Vector2((float)(minX + maxX) * 0.5f, (float)(minY + maxY) * 0.5f);
        float extent = MathF.Max((float)(maxX - minX), (float)(maxY - minY));
        float targetSpan = MathF.Max(extent * 1.6f + 0.6f, 2.0f);

        // The bodies must fit on both axes of the window
        float aspect = window.X / MathF.Max(1.0f, window.Y);
        float width = (float)(maxX - minX), height = (float)(maxY - minY);
        float needForWidth = (width * 1.6f + 0.6f) / MathF.Max(aspect, 0.1f);
        targetSpan = MathF.Max(targetSpan, needForWidth);
        targetSpan = MathF.Max(targetSpan, height * 1.6f + 0.6f);

        if (first)
        {
            centre = targetCentre;
            span = targetSpan;
            first = false;
        }
        else
        {
            float k = 1.0f - MathF.Exp(-3.0f * dt);
            centre = Vector2.Lerp(centre, targetCentre, k);
            // Zoom out fast, in slowly, so a body never leaves the screen
            float kz = targetSpan > span ? 1.0f - MathF.Exp(-4.0f * dt) : 1.0f - MathF.Exp(-1.0f * dt);
            span += (targetSpan - span) * kz;
        }

        // The window is wider than the free area by the inset: move the view left so the bodies sit in the free part
        float fullAspect = full.X / MathF.Max(1.0f, full.Y);
        camera.ViewSize = new Vector2(span * fullAspect, span);
        camera.Position = new Vector3(centre.X - leftInset * 0.5f * span / MathF.Max(1.0f, window.Y), centre.Y, 0.0f);
    }

    /// <summary>World units per screen pixel, for line widths.</summary>
    public float UnitsPerPixel(Vector2 window) => span / MathF.Max(1.0f, window.Y);
}
