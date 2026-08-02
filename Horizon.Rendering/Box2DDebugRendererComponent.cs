using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

using Box2D.NetStandard.Common;
using Box2D.NetStandard.Dynamics.World;
using Box2D.NetStandard.Dynamics.World.Callbacks;

using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Core.Data;
using Horizon.Engine;
using Horizon.OpenGL;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Buffers;
using Horizon.OpenGL.Descriptions;


using Newtonsoft.Json.Linq;

using Silk.NET.OpenGL;

namespace Horizon.Rendering;

public class Box2DDebugRendererComponent : DebugDraw, IGameComponent
{
    private VertexBufferObject _vbo;
    private Technique _technique;
    private readonly List<BasicVertex> vertices = [];
    private readonly List<uint> indices = [];

    public bool Enabled { get; set; }
    public string Name { get; set; } = "Box2D Debug Renderer";
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
            Colour = new Vector3(r, b, g);
        }
    }

    public void Initialize()
    {   
        if (GameObject
                   .Engine
                   .ObjectManager
                   .Shaders
                   .TryCreate(
                   ShaderDescription.FromPath("shaders/basic",
                   "basic_technique"
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
        GameEngine.Instance.GL.DrawElements(PrimitiveType.LineLoop, (uint)indices.Count, DrawElementsType.UnsignedInt, null);

        _technique.Unbind();
    }

    public void UpdatePhysics(float dt)
    {

    }

    public void UpdateState(float dt)
    {

    }

    public override void DrawTransform(in Transform xf)
    {
        
    }

    public override void DrawPoint(in Vector2 position, float size, in Color color)
    {

    }

    public override void DrawPolygon(in Vec2[] _vertices, int vertexCount, in Color color)
    {
        uint baseIndex = (uint)vertices.Count;
        Vector4 col = new Vector4(color.R, color.G, color.B, 1.0f);

        for (int i = 0; i < vertexCount; i++)
        {
            vertices.Add(new BasicVertex(_vertices[i].X, _vertices[i].Y, color.R, color.B, color.G));
            
            // Connect lines in a loop: 0->1, 1->2 ... (N-1)->0
            indices.Add(baseIndex + (uint)i);
            indices.Add(baseIndex + (uint)((i + 1) % vertexCount));
        }
    }
    public void DrawPolygon(in Vector2[] _vertices, in Vector4 color)
    {
        uint baseIndex = (uint)vertices.Count;

        for (int i = 0; i < _vertices.Length; i++)
        {
            vertices.Add(new BasicVertex(_vertices[i].X, _vertices[i].Y, color.X, color.Y, color.Z));

            // Connect lines in a loop: 0->1, 1->2 ... (N-1)->0
            indices.Add(baseIndex + (uint)i);
            indices.Add(baseIndex + (uint)((i + 1) % _vertices.Length));
        }
    }

    public override void DrawSolidPolygon(in Vec2[] vertices, int vertexCount, in Color color)
    {
        DrawPolygon(vertices, vertexCount, color);
    }

    public override void DrawCircle(in Vec2 center, float radius, in Color color)
    {
        const int segments = 16;
        float increment = 2.0f * MathF.PI / segments;
        uint baseIndex = (uint)vertices.Count;
        Vector4 col = new Vector4(color.R, color.G, color.B, 1.0f);

        for (int i = 0; i < segments; i++)
        {
            float theta = i * increment;
            float x = center.X + radius * MathF.Cos(theta);
            float y = center.Y + radius * MathF.Sin(theta);

            vertices.Add(new BasicVertex(x, y, color.R, color.G, color.B));

            indices.Add(baseIndex + (uint)i);
            indices.Add(baseIndex + (uint)((i + 1) % segments));
        }

    }

    public void DrawCircle(Vector2 center, float radius, Vector4 color)
    {
        const int segments = 16;
        float increment = 2.0f * MathF.PI / segments;
        uint baseIndex = (uint)vertices.Count;

        for (int i = 0; i < segments; i++)
        {
            float theta = i * increment;
            float x = center.X + radius * MathF.Cos(theta);
            float y = center.Y + radius * MathF.Sin(theta);

            vertices.Add(new BasicVertex(x, y, color.X, color.Y, color.Z));

            indices.Add(baseIndex + (uint)i);
            indices.Add(baseIndex + (uint)((i + 1) % segments));
        }

    }

    public override void DrawSolidCircle(in Vec2 center, float radius, in Vec2 axis, in Color color)
    {
        // Draw the main circle shell geometry
        DrawCircle(center, radius, color);

        // Draw the inner rotation indicator axis line
        Vector2 p = center + radius * axis;
        DrawSegment(center, p, color);
    }

    public override void DrawSegment(in Vec2 p1, in Vec2 p2, in Color color)
    {
        uint baseIndex = (uint)vertices.Count;
        Vector4 col = new Vector4(color.R, color.G, color.B, 1.0f);

        vertices.Add(new BasicVertex(p1.X, p1.Y, color.R, color.G, color.B));
        vertices.Add(new BasicVertex(p2.X, p2.Y, color.R, color.G, color.B));

        indices.Add(baseIndex);
        indices.Add(baseIndex + 1);
    }

    public void ClearBuffers()
    {
        indices.Clear();
        vertices.Clear();
    }
}
