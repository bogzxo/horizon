using Horizon.Engine;
using Horizon.OpenGL;
using Horizon.OpenGL.Buffers;
using Horizon.OpenGL.Managers;
using Horizon.Rendering.Spriting.Data;

using Silk.NET.OpenGL;

namespace Horizon.Rendering.Primitives;

/// <summary>
/// Triangles of your own, drawn with a technique of your own: vertices (<see cref="Vertex2D"/>, a position and a
/// texture coordinate) and the indices of the triangles between them. Hand them over with <see cref="Upload"/> once
/// they are built; the technique is bound before every draw and sets whatever it needs (the camera it draws through
/// comes out of the <see cref="CameraBlock"/>, like everything else).
/// </summary>
public class Mesh2D : GameObject
{
    public VertexBufferObject Buffer { get; private set; } = null!;

    public Technique? Shader { get; protected set; }

    private uint elementCount;

    public Mesh2D(in Technique? shader = null)
    {
        Shader = shader;
    }

    public override void Initialize()
    {
        base.Initialize();

        Buffer = VertexBufferObject.Create();
        Buffer.SetLayout<Vertex2D>();
    }

    /// <summary>
    /// Hands over what the mesh is made of: its vertices, and the triangles between them three indices each. Render
    /// thread, after it has been set up. Until this is called there is nothing to draw.
    /// </summary>
    public void Upload(ReadOnlySpan<Vertex2D> vertices, ReadOnlySpan<uint> indices)
    {
        Buffer.VertexBuffer.Upload(vertices);
        Buffer.ElementBuffer.Upload(indices);
        elementCount = (uint)indices.Length;
    }

    public override unsafe void Render(float dt)
    {
        if (elementCount < 1 || Shader is not { IsValid: true })
            return;

        Shader.Bind();
        Buffer.Bind();
        Horizon.Graphics.GraphicsDevice.Current.DrawIndexed(Horizon.Graphics.Topology.Triangles, elementCount);
    }
}
