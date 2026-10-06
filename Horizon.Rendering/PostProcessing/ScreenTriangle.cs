using System.Numerics;

using Horizon.Engine;
using Horizon.OpenGL.Buffers;
using Horizon.OpenGL.Descriptions;

using Silk.NET.OpenGL;

namespace Horizon.Rendering.PostProcessing;

/// <summary>
/// Helper class with the one triangle everything that works on a whole picture is drawn with (the post effects, the scene transitions).
/// One triangle rather than the two of a rectangle, there is no seam down the middle for the pixels along it to be shaded twice.
/// There is only ever one of it and it belongs to no scene, so it is still around after whichever one drew with it first has gone.
/// </summary>
internal static class ScreenTriangle
{
    private static VertexBufferObject? triangle;
    private static bool unavailable;

    /// <summary>
    /// Draws over everything that is bound, with whatever technique is bound. GL thread.
    /// </summary>
    public static void Draw()
    {
        triangle ??= Create();
        if (triangle is null)
            return;

        triangle.Bind();
        GameEngine.Instance.GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        triangle.Unbind();
    }

    private static VertexBufferObject? Create()
    {
        // Asked for once and turned down, asking again every frame only fills the log
        if (unavailable)
            return null;

        using var nobody = Horizon.Content.AssetScope.EnterGlobal();

        if (!GameEngine.Instance.ObjectManager.VertexArrays.TryCreate(VertexArrayObjectDescription.VertexBuffer, out var result))
        {
            Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, result.Message);
            unavailable = true;
            return null;
        }

        var buffers = new VertexBufferObject(result.Asset);

        // Where each corner is on screen and in the picture. It reaches well past two of the edges, what sticks out is never drawn
        var corners = new Vector2[]
        {
            new(-1, -1), new(0, 0),
            new(3, -1), new(2, 0),
            new(-1, 3), new(0, 2)
        };

        buffers.Bind();
        buffers.VertexBuffer.Bind();
        buffers.VertexBuffer.VertexAttributePointer(0, 2, VertexAttribPointerType.Float, sizeof(float) * 4, 0);
        buffers.VertexBuffer.VertexAttributePointer(1, 2, VertexAttribPointerType.Float, sizeof(float) * 4, sizeof(float) * 2);
        buffers.Unbind();
        buffers.VertexBuffer.Unbind();

        buffers.VertexBuffer.NamedBufferData(corners);
        return buffers;
    }
}
