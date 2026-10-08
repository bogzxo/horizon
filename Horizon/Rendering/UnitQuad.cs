using Horizon.Logging;
using Horizon.OpenGL.Buffers;
using Horizon.Rendering.Spriting.Data;

namespace Horizon.Rendering;

/// <summary>
/// The one quad everything that is drawn as instances of a quad is drawn with: the sprites, the shapes of a primitive
/// renderer. A square of one by one around the origin (-0.5 to 0.5) with the texture coordinates of an image that has
/// its first row at the top, two triangles. There is only ever one of it, it belongs to no scene, and every renderer
/// that draws quads binds it rather than making a quad of its own.
/// </summary>
public static class UnitQuad
{
    private static VertexBufferObject? quad;
    private static bool unavailable;

    /// <summary>How many indices a draw of the quad is.</summary>
    public const uint INDICES = 6;

    /// <summary>
    /// Binds the quad, for an instanced draw of <see cref="INDICES"/> indices. False (and nothing bound) if the GPU
    /// wouldn't make it, which has been logged. GL thread.
    /// </summary>
    public static bool Bind()
    {
        quad ??= Create();
        if (quad is null) return false;

        quad.Bind();
        return true;
    }

    private static VertexBufferObject? Create()
    {
        if (unavailable) return null;

        using var nobody = Horizon.Content.AssetScope.EnterGlobal();

        try
        {
            var buffers = VertexBufferObject.Create();

            buffers.SetLayout<Vertex2D>();
            buffers.VertexBuffer.Upload<Vertex2D>(
            [
                new Vertex2D(-0.5f, -0.5f, 0.0f, 1.0f),
                new Vertex2D(0.5f, -0.5f, 1.0f, 1.0f),
                new Vertex2D(0.5f, 0.5f, 1.0f, 0.0f),
                new Vertex2D(-0.5f, 0.5f, 0.0f, 0.0f)
            ], Silk.NET.OpenGL.BufferUsageARB.StaticDraw);
            buffers.ElementBuffer.Upload<uint>([0, 1, 2, 0, 2, 3], Silk.NET.OpenGL.BufferUsageARB.StaticDraw);

            return buffers;
        }
        catch (InvalidOperationException e)
        {
            Log.Error($"[UnitQuad] {e.Message}");
            unavailable = true;
            return null;
        }
    }
}
