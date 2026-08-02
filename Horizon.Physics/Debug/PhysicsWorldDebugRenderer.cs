using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

using Box2D.NetStandard.Common;
using Box2D.NetStandard.Dynamics.World.Callbacks;

using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Engine;
using Horizon.OpenGL;
using Horizon.OpenGL.Buffers;
using Horizon.OpenGL.Descriptions;

using Silk.NET.OpenGL;

namespace Horizon.Physics.Debug;

public class PhysicsWorldDebugRenderer : IGameComponent
{
    private VertexBufferObject _vbo;
    private Technique _technique;
    private readonly List<BasicVertex> vertices = [];
    private readonly List<uint> indices = [];

    public bool Enabled { get; set; }
    public string Name { get; set; } = "Physics World Debug Renderer";
    public Entity Parent { get; set; }

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

    public void Initialize()
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
            Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, resultTech.Message);
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
            Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, result.Message);
        }
        _vbo.Bind();
        _vbo.VertexBuffer.Bind();
        _vbo.VertexBuffer.SetLayout<BasicVertex>();
        _vbo.Unbind();
    }

    public unsafe void Render(float dt, object? obj = null)
    {
        if (GameEngine.Instance.ActiveCamera is null) return;

        _vbo.VertexBuffer.NamedBufferData(CollectionsMarshal.AsSpan(vertices));
        _vbo.ElementBuffer.NamedBufferData(CollectionsMarshal.AsSpan(indices));

        _technique.Bind();
        _technique.SetUniform("uCameraView", GameEngine.Instance.ActiveCamera.View);
        _technique.SetUniform("uCameraProjection", GameEngine.Instance.ActiveCamera.Projection);

        _vbo.Bind();
        GameEngine.Instance.GL.DrawElements(PrimitiveType.Lines, (uint)indices.Count, DrawElementsType.UnsignedInt, null);

        _technique.Unbind();
    }

    public void UpdatePhysics(float dt)
    {

    }

    public void UpdateState(float dt)
    {

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