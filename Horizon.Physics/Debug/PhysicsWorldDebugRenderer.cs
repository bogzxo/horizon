using Bogz.Logging;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Core.Threading;
using Horizon.Engine;
using Horizon.OpenGL;
using Horizon.OpenGL.Buffers;
using Horizon.OpenGL.Descriptions;

using Silk.NET.OpenGL;

namespace Horizon.Physics.Debug;

public class PhysicsWorldDebugRenderer : GameComponent
{
    private VertexBufferObject _vbo;
    private Technique _technique;
    /// <summary>The lines of one picture of the debug view.</summary>
    private sealed class Lines
    {
        public readonly List<BasicVertex> Vertices = [];
        public readonly List<uint> Indices = [];
    }

    // What is drawn into: the lines of the frame that is drawn in turns with the simulation, or those of the capture
    // that is going on, for frames that are drawn alongside it
    private readonly Lines live = new();
    private readonly SnapshotBuffer<Lines> captured = new(static () => new Lines());
    private Lines target;

    private List<BasicVertex> vertices => target.Vertices;
    private List<uint> indices => target.Indices;

    [StructLayout(LayoutKind.Sequential)] // explicitly set sequential layout
    private readonly struct BasicVertex : IVertex
    {
        public readonly Vector2 Position
        {
            get => position;
            init => position = value;
        }

        public readonly Vector3 Colour
        {
            get => colour;
            init => colour = value;
        }

        public static uint SizeInBytes { get; } = sizeof(float) * 5;

        private readonly Vector2 position;

        private readonly Vector3 colour;
        public static ReadOnlySpan<VertexLayoutDescription> GetLayout() => new VertexLayoutDescription[]
      {
            new() {
                Index = 0,
                Size = sizeof(float) * 2,
                Count = 2,
                Offset = 0,
                Type = VertexAttribPointerType.Float,
                Instanced = false
            },
            new() {
                Index = 1,
                Size = sizeof(float) * 3,
                Count = 3,
                Offset = sizeof(float) * 2,
                Type = VertexAttribPointerType.Float,
                Instanced = false
            },
      };

        public BasicVertex(Vector2 position, Vector3 colour)
        {
            Position = position;
            Colour = colour;
        }

        public BasicVertex(float x, float y, float r, float g, float b)
        {
            Position = new Vector2(x, y);
            Colour = new Vector3(r, g, b);
        }
    }

    public override void Initialize()
    {
        if (GameObject
                   .Engine
                   .ObjectManager
                   .Shaders
                   .TryCreate(
                   ShaderDescription.FromPath("shaders/debug",
                   "physics_2d_debug_technique"
                   ), out var resultTech))
        {
            _technique = new(resultTech.Asset);
        }
        else
        {
            Log.Error(resultTech.Message);
        }

        if (
               GameEngine.Instance
                   .ObjectManager
                   .VertexArrays
                   .TryCreate(
                       new OpenGL.Descriptions.VertexArrayObjectDescription
                       {
                           Buffers = new()
                           {
                                {
                                    VertexArrayBufferAttachmentType.ArrayBuffer,
                                    BufferObjectDescription.ArrayBuffer
                                },
                                {
                                    VertexArrayBufferAttachmentType.ElementBuffer,
                                    BufferObjectDescription.ElementArrayBuffer
                                }
                           }
                       }, out var result
                   )
           )
        {
            _vbo = new(result.Asset);
        }
        else
        {
            Log.Error(result.Message);
        }
        _vbo.Bind();
        _vbo.VertexBuffer.Bind();
        _vbo.VertexBuffer.SetLayout<BasicVertex>();
        _vbo.Unbind();
    }

    public PhysicsWorldDebugRenderer()
    {
        target = live;
    }

    /// <summary>
    /// Has what is drawn from here on go into the capture that is going on, for frames drawn alongside the simulation,
    /// until <see cref="EndCapture"/>. Simulation thread. False (and nothing changes) if no capture is going on.
    /// </summary>
    internal bool BeginCapture()
    {
        if (captured.BeginPublish() is not { } lines)
            return false;

        target = lines;
        ClearBuffers();
        return true;
    }

    internal void EndCapture() => target = live;

    /// <summary>
    /// Draws the lines of the newer snapshot of the frame that is being drawn, if any were captured. Render thread.
    /// </summary>
    internal void RenderCaptured(float dt)
    {
        if (!captured.TryGet(RenderFrame.Active, out Lines lines))
            return;

        Draw(lines);
    }

    public override void Render(float dt) => Draw(live);

    private unsafe void Draw(Lines lines)
    {
        if (GameEngine.Instance.ActiveCamera is null || lines.Indices.Count == 0) return;

        _vbo.VertexBuffer.NamedBufferData(CollectionsMarshal.AsSpan(lines.Vertices));
        _vbo.ElementBuffer.NamedBufferData(CollectionsMarshal.AsSpan(lines.Indices));

        _technique.Bind();
        _technique.SetUniform("uCameraView", GameEngine.Instance.ActiveCamera.View);
        _technique.SetUniform("uCameraProjection", GameEngine.Instance.ActiveCamera.Projection);

        _vbo.Bind();
        GameEngine.Instance.GL.DrawElements(PrimitiveType.Lines, (uint)lines.Indices.Count, DrawElementsType.UnsignedInt, null);

        _technique.Unbind();
    }

    public void DrawPolygon(in Vector2[] _vertices, in Vector3 colour)
    {
        uint baseIndex = (uint)vertices.Count;

        for (int i = 0; i < _vertices.Length; i++)
        {
            vertices.Add(new BasicVertex(_vertices[i], colour));
            // Connect lines in a loop: 0->1, 1->2 ... (N-1)->0
            indices.Add(baseIndex + (uint)i);
            indices.Add(baseIndex + (uint)((i + 1) % _vertices.Length));
        }
    }

    /// <summary>The outline of a rectangle that isn't turned, from its smallest corner to its largest.</summary>
    public void DrawRectangle(Vector2 min, Vector2 max, in Vector3 colour)
    {
        uint baseIndex = (uint)vertices.Count;

        vertices.Add(new BasicVertex(new Vector2(min.X, min.Y), colour));
        vertices.Add(new BasicVertex(new Vector2(max.X, min.Y), colour));
        vertices.Add(new BasicVertex(new Vector2(max.X, max.Y), colour));
        vertices.Add(new BasicVertex(new Vector2(min.X, max.Y), colour));

        for (uint i = 0; i < 4; i++)
        {
            indices.Add(baseIndex + i);
            indices.Add(baseIndex + (i + 1) % 4);
        }
    }

    public void DrawCircle(Vector2 center, float radius, Vector3 colour)
    {
        const int segments = 16;
        float increment = 2.0f * MathF.PI / segments;
        uint baseIndex = (uint)vertices.Count;

        for (int i = 0; i < segments; i++)
        {
            float theta = i * increment;
            float x = center.X + radius * MathF.Cos(theta);
            float y = center.Y + radius * MathF.Sin(theta);

            vertices.Add(new BasicVertex(new Vector2(x, y), colour));

            indices.Add(baseIndex + (uint)i);
            indices.Add(baseIndex + (uint)((i + 1) % segments));
        }
    }
    public void DrawSegment(in Vector2 p1, in Vector2 p2, in Vector3 colour)
    {
        uint baseIndex = (uint)vertices.Count;

        vertices.Add(new BasicVertex(p1.X, p1.Y, colour.X, colour.Y, colour.Z));
        vertices.Add(new BasicVertex(p2.X, p2.Y, colour.X, colour.Y, colour.Z));

        indices.Add(baseIndex);
        indices.Add(baseIndex + 1);
    }

    public void ClearBuffers()
    {
        indices.Clear();
        vertices.Clear();
    }
}