using System.Numerics;

using Horizon.Core.Components;
using Horizon.Engine;
using Horizon.Graphics;

namespace Horizon.Rendering.Meshes;

/// <summary>
/// Triangles of your own in three dimensions, lit and drawn where its <see cref="Transform"/> puts them. Vertices
/// (<see cref="Vertex3D"/>, a position, a normal, a texture coordinate and a colour) and the indices of the
/// triangles between them, three each, going round anticlockwise as seen from outside. Hand them over with
/// <see cref="Upload"/> once they are built, or start from one of the shapes it knows (<see cref="Cube"/>,
/// <see cref="Sphere"/>, <see cref="Plane"/>, <see cref="Cylinder"/>) and never touch a vertex.
/// <code>
/// var crate = world.AddEntity(Mesh3D.Cube(1.0f));          // Initialize, render thread
/// crate.Texture = texture;
/// crate.Transform.Position = new Vector3(2, 0.5f, 0);
///
/// var ground = world.AddEntity(new Mesh3D());
/// ground.Upload(vertices, indices);                        // render thread, whenever they change
/// </code>
/// It is lit by one light, the sun, which every mesh shares (<see cref="Sun"/>, <see cref="SunColor"/>,
/// <see cref="Ambient"/>), plus whatever glows of its own accord (<see cref="Emissive"/>), and is painted with its
/// <see cref="Texture"/> if it has one times its <see cref="Tint"/> times the colour of each vertex. The shader is
/// shaders/models/mesh.slang, swap it for one of your own with <see cref="Shader"/>, the vertex layout is the
/// contract. Backs are culled (<see cref="Cull"/>), so a shape has to be closed or seen from the right side. The
/// camera it draws through is the scene's, which had better be a <see cref="Camera3D"/>.
/// </summary>
public partial class Mesh3D : GameObject
{
    private const string SHADER_DIRECTORY = "shaders/models";
    private const string SHADER_NAME = "mesh";

    private static Technique? shared;

    private uint elementCount;

    public VertexBuffer Buffer { get; private set; } = null!;

    /// <summary>What draws it, the engine's mesh shader unless given another.</summary>
    public Technique? Shader { get; set; }

    public TransformComponent3D Transform { get; }

    /// <summary>The picture over it, by the texture coordinates of its vertices, none for a plain coloured mesh.</summary>
    public Texture? Texture { get; set; }

    /// <summary>What the whole mesh is multiplied by, white for as it is.</summary>
    public Vector4 Tint { get; set; } = Vector4.One;

    /// <summary>How much of it shows whether or not the sun gets to it, 0 to 1. A lamp is 1.</summary>
    public float Emissive { get; set; }

    /// <summary>Which side of its triangles is thrown away, the backs unless it is a sheet seen from both sides.</summary>
    public CullMode Cull { get; set; } = CullMode.Back;

    /// <summary>Which way the sun shines, for every mesh. Down and a bit to the side to begin with.</summary>
    public static Vector3 Sun { get; set; } = Vector3.Normalize(new Vector3(-0.4f, -1.0f, -0.6f));

    public static Vector3 SunColor { get; set; } = new(1.0f, 0.96f, 0.9f);

    /// <summary>What is lit where the sun isn't, for every mesh.</summary>
    public static Vector3 Ambient { get; set; } = new(0.25f, 0.27f, 0.32f);

    /// <summary>How many triangles there are to draw, 0 until something has been uploaded.</summary>
    public uint TriangleCount => elementCount / 3;

    public Mesh3D(Technique? shader = null)
    {
        Shader = shader;
        Transform = AddComponent<TransformComponent3D>();
    }

    public override void Initialize()
    {
        base.Initialize();

        Buffer = VertexBuffer.Create();
        Buffer.SetLayout<Vertex3D>();

        if (pending is { } built)
        {
            Upload(built.Vertices, built.Indices);
            pending = null;
        }
    }

    // What a shape made before the mesh had a GPU to go on is kept in until it has
    private (Vertex3D[] Vertices, uint[] Indices)? pending;

    /// <summary>What was handed over before there was a GPU to put it on, for the tests of the shapes.</summary>
    internal (Vertex3D[] Vertices, uint[] Indices)? Pending => pending;

    /// <summary>
    /// Hands over what the mesh is made of, its vertices and the triangles between them three indices each, going
    /// round anticlockwise as seen from the outside. Render thread once it has been set up, before that it is kept
    /// and goes up when it is.
    /// </summary>
    public void Upload(ReadOnlySpan<Vertex3D> vertices, ReadOnlySpan<uint> indices)
    {
        if (Buffer is null)
        {
            pending = (vertices.ToArray(), indices.ToArray());
            return;
        }

        Buffer.Vertices.Upload(vertices);
        Buffer.Indices.Upload(indices);
        elementCount = (uint)indices.Length;
    }

    public override void Render(float dt)
    {
        base.Render(dt);

        if (elementCount < 1)
            return;

        // The engine's shader is one for every mesh, loaded the first time one is drawn
        Technique? technique = Shader ?? (shared ??= Technique.Load(SHADER_DIRECTORY, SHADER_NAME));
        if (technique is not { IsValid: true })
            return;

        var device = GraphicsDevice.Current;
        var before = RenderState.Save();

        CameraBlock.Use(Engine.ActiveCamera);
        RenderState.DepthTest = true;
        RenderState.DepthWrite = true;
        RenderState.Cull = Cull;

        technique.Bind();
        technique.SetUniform("uModel", Transform.Model);
        technique.SetUniform("uTint", Tint);
        technique.SetUniform("uSun", Sun);
        technique.SetUniform("uSunColor", SunColor);
        technique.SetUniform("uAmbient", Ambient);
        technique.SetUniform("uEmissive", Math.Clamp(Emissive, 0.0f, 1.0f));
        technique.SetUniform("uHasTexture", Texture is not null);
        Texture?.Bind(0);

        Buffer.Bind();
        device.DrawIndexed(Topology.Triangles, elementCount);
        technique.Unbind();

        RenderState.Restore(before);
    }
}
