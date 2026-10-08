using Horizon.Logging;
using System.Numerics;

using Horizon.Core.Components;
using Horizon.Core.Threading;
using Horizon.Engine;
using Horizon.OpenGL;
using Horizon.OpenGL.Buffers;
using Horizon.OpenGL.Descriptions;
using Horizon.OpenGL.Managers;

using Silk.NET.OpenGL;

namespace Horizon.Rendering.Primitives;

/// <summary>
/// Flat shapes without a texture in sight: boxes, discs, lines, triangles, filled or as outlines, every one of them
/// smooth at its edge whatever size it is drawn at (see <see cref="ShapeKind"/>). What debug overlays, graphs and
/// placeholders are made of. However many shapes there are it is one draw call: the shapes of a frame are written
/// straight into a <see cref="StreamBuffer{T}"/> and the <see cref="UnitQuad"/> is drawn once per shape, with a
/// shader that works out what of the quad is inside the shape.
/// <para>
/// The shapes are the simulation's, see <see cref="Shapes"/>: written down there (from the updates, or in
/// <see cref="Describe"/>) and published at the end of every tick, so frames drawn alongside the simulation show
/// them between the last two ticks, every shape on its way from where it was to where it is (the list has to be
/// written in the same order every tick for that, see <see cref="ShapeInstance.CanBlend"/>). Something that was put
/// somewhere else rather than moved there says so with <see cref="Break"/>.
/// </para>
/// <code>
/// var overlay = renderer.AddEntity(new PrimitiveRenderer());
/// overlay.Describe = shapes =>                       // simulation thread, every tick
/// {
///     shapes.Rectangle(box.Min, box.Max, 1.0f, Red);
///     shapes.Arrow(body.Position, body.Position + body.Velocity * 0.1f, 2.0f, Yellow);
/// };
/// </code>
/// </summary>
public class PrimitiveRenderer : GameObject
{
    /// <summary>The binding of the storage block the shapes are read out of, which is what shapes.vert says.</summary>
    public const uint SHAPES_BINDING = 1;

    private const string SHADER_NAME = "primitives";
    private const string UNIFORM_MODEL = "uModel";
    private const string UNIFORM_NEARNESS = "uNearness";
    private const string UNIFORM_EMISSIVE = "uEmissive";

    /// <summary>What a frame of shapes looked like as of one tick.</summary>
    private sealed class Captured
    {
        public readonly ShapeList Shapes = new();
    }

    // The one shader every renderer draws with
    private static Technique? shader;

    private readonly SnapshotBuffer<Captured> captured = new(static () => new Captured());
    private StreamBuffer<ShapeInstance>? stream;

    /// <summary>The global transform for everything this renderer draws.</summary>
    public TransformComponent2D Transform { get; }

    /// <summary>A camera to draw through, if null this defaults to the scene camera.</summary>
    public Camera? CustomCamera { get; set; }

    /// <summary>
    /// How near the shapes are, from 0 (the backdrop) to 1 (right in front). Only a renderer that blurs motion goes by
    /// it (see <see cref="DeferredRenderer2D"/>): what is nearer blurs over what is further away.
    /// </summary>
    public float Nearness { get; set; } = 0.8f;

    /// <summary>
    /// How much of the shapes shows no matter the light, from 0 to 1, when they are drawn by a <see cref="DeferredRenderer2D"/>.
    /// All of it unless said otherwise: a debug overlay is to be seen in the dark.
    /// </summary>
    public float Emissive { get; set; } = 1.0f;

    /// <summary>
    /// The shapes, as the simulation has them. Written to from the updates (and kept from tick to tick until they are
    /// cleared), or from <see cref="Describe"/>, which starts afresh every tick. Never from the render thread while
    /// the simulation runs: that is what <see cref="Draw"/> is for.
    /// </summary>
    public ShapeList Shapes { get; } = new();

    /// <summary>
    /// Writes down what is drawn, if set: called at the end of every tick (simulation thread) with the list cleared,
    /// and while the simulation stands still (a scene being set up) from the render thread. The easy way to draw
    /// whatever something looks like right now, see the summary.
    /// </summary>
    public Action<ShapeList>? Describe { get; set; }

    /// <summary>How many shapes were drawn last frame.</summary>
    public int DrawnCount { get; private set; }

    public PrimitiveRenderer()
    {
        Transform = AddComponent<TransformComponent2D>();
    }

    public override void Initialize()
    {
        base.Initialize();

        EnsureShader();
        stream ??= new StreamBuffer<ShapeInstance>(BufferTargetARB.ShaderStorageBuffer, 1024, "shapes");
    }

    /// <summary>
    /// Has the next tick not be blended with the one before: everything was put where it is, not moved there. Simulation thread.
    /// </summary>
    public void Break() => captured.Break();

    /// <summary>
    /// Publishes the shapes as they are (after <see cref="Describe"/> has had its say), for the frames drawn alongside
    /// the simulation. Simulation thread, at the end of every tick.
    /// </summary>
    public override void Capture()
    {
        if (Describe is { } describe)
        {
            Shapes.Clear();
            describe(Shapes);
        }

        if (captured.BeginPublish() is { } into)
            Shapes.CopyTo(into.Shapes);

        base.Capture();
    }

    public override void Render(float dt)
    {
        base.Render(dt);

        if (!Enabled || stream is null) return;

        Camera camera = CustomCamera ?? Engine.ActiveCamera;
        RenderFrame frame = RenderFrame.Active;

        if (frame.IsDecoupled)
        {
            if (!captured.TryGet(frame, out Captured before, out Captured after, out bool continuous))
                return;

            DrawBlended(before.Shapes, after.Shapes, continuous ? frame.Alpha : 1.0f, camera);
            return;
        }

        // The simulation is standing still, the shapes are there to be read (and written) as they are
        if (Describe is { } describe)
        {
            Shapes.Clear();
            describe(Shapes);
        }

        Draw(Shapes.Span, camera);
    }

    /// <summary>
    /// Helper method to draw the shapes of the newer of two snapshots, each on its way from where it was in the older
    /// one when the two can be matched up (the same place in a list of the same length).
    /// </summary>
    private void DrawBlended(ShapeList before, ShapeList after, float alpha, Camera camera)
    {
        int count = after.Count;
        if (count == 0 || stream is null)
        {
            DrawnCount = 0;
            return;
        }

        Span<ShapeInstance> into = stream.Begin(count);
        if (into.IsEmpty) return;

        if (alpha >= 1.0f || before.Count != count)
        {
            after.Span.CopyTo(into);
        }
        else
        {
            ReadOnlySpan<ShapeInstance> from = before.Span, to = after.Span;
            for (int i = 0; i < count; i++)
                into[i] = ShapeInstance.CanBlend(from[i], to[i]) ? ShapeInstance.Blend(from[i], to[i], alpha) : from[i];
        }

        Submit(count, camera);
        stream.End();
    }

    /// <summary>
    /// Draws shapes right now, in the order they are in. For whoever draws on the render thread (an overlay drawn out
    /// of what a frame knows, say) rather than from the simulation. GL thread.
    /// </summary>
    public void Draw(ReadOnlySpan<ShapeInstance> shapes, Camera? camera = null)
    {
        if (shapes.IsEmpty || stream is null)
        {
            DrawnCount = 0;
            return;
        }

        Span<ShapeInstance> into = stream.Begin(shapes.Length);
        if (into.IsEmpty) return;

        shapes.CopyTo(into);
        Submit(shapes.Length, camera ?? CustomCamera ?? Engine.ActiveCamera);
        stream.End();
    }

    /// <summary>
    /// Helper method to draw the shapes that were written into the stream: one instanced draw of the quad.
    /// </summary>
    private unsafe void Submit(int count, Camera camera)
    {
        DrawnCount = count;
        if (shader is not { IsValid: true } || !UnitQuad.Bind()) return;

        CameraBlock.Use(camera);

        shader.Bind();
        shader.SetUniform(UNIFORM_MODEL, Transform.ModelMatrix);
        shader.SetUniform(UNIFORM_NEARNESS, Nearness);
        shader.SetUniform(UNIFORM_EMISSIVE, Emissive);

        stream!.BindRange(BufferTargetARB.ShaderStorageBuffer, SHAPES_BINDING);

        Horizon.Graphics.GraphicsDevice.Current.DrawIndexedInstanced(Horizon.Graphics.Topology.Triangles, UnitQuad.INDICES, (uint)count);
    }

    private static void EnsureShader()
    {
        if (shader is not null) return;

        // By name, so it is the same one for every renderer there ever is and nobody frees it from under the others
        if (!GameEngine.Instance.ObjectManager.Shaders.TryCreateOrGet(SHADER_NAME, ShaderDescription.FromPath("shaders/primitives", "shapes"), out var result))
        {
            Log.Error(result.Message);
            return;
        }

        shader = new Technique(result.Asset);
    }

    protected override void DisposeOther()
    {
        stream?.Dispose();
        stream = null;

        base.DisposeOther();
    }
}
