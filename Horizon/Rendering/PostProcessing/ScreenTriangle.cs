using Horizon.Logging;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Descriptions;
using Horizon.OpenGL.Managers;

using Silk.NET.OpenGL;

namespace Horizon.Rendering.PostProcessing;

/// <summary>
/// Helper class with the one triangle everything that works on a whole picture is drawn with (the post effects, the
/// scene transitions, putting a renderer on screen). One triangle rather than the two of a rectangle, there is no seam
/// down the middle for the pixels along it to be shaded twice. It has no vertices at all: the vertex shader
/// (shaders/common/screen.vert) makes the corners up out of the vertex number, so all there is to bind is an empty
/// vertex array. There is only ever one of it and it belongs to no scene.
/// </summary>
internal static class ScreenTriangle
{
    private static VertexArrayObject? array;
    private static bool unavailable;

    /// <summary>
    /// Draws over everything that is bound, with whatever technique is bound. GL thread.
    /// </summary>
    public static void Draw()
    {
        array ??= Create();
        if (array is null)
            return;

        array.Bind();
        ObjectManager.GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
    }

    private static VertexArrayObject? Create()
    {
        // Asked for once and turned down, asking again every frame only fills the log
        if (unavailable)
            return null;

        using var nobody = Horizon.Content.AssetScope.EnterGlobal();

        if (!ObjectManager.Instance.VertexArrays.TryCreate(new VertexArrayObjectDescription { Buffers = [] }, out var result))
        {
            Log.Error(result.Message);
            unavailable = true;
            return null;
        }

        return result.Asset;
    }
}
