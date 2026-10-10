using Horizon.Graphics;

namespace Horizon.Rendering.PostProcessing;

/// <summary>
/// Helper class with the one triangle everything that works on a whole picture is drawn with (the post effects, the
/// scene transitions, putting a renderer on screen). One triangle rather than the two of a rectangle, there is no seam
/// down the middle for the pixels along it to be shaded twice. It has no vertices at all. The vertex shader
/// (shaders/common/screen.slang) makes the corners up out of the vertex number, so there is nothing to bind.
/// </summary>
public static class ScreenTriangle
{
    /// <summary>Draws over everything that is bound, with whatever technique is bound. Render thread.</summary>
    public static void Draw()
    {
        var device = GraphicsDevice.Current;
        device.BindVertexArray(null);
        device.Draw(Topology.Triangles, 3);
    }
}
