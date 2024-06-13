﻿using System.Numerics;

using Horizon.Engine;
using Horizon.OpenGL;
using Horizon.OpenGL.Buffers;
using Horizon.OpenGL.Descriptions;

namespace Horizon.Rendering;

public class RenderRectangle : GameObject
{
    private static VertexBufferObject vbo;

    public Technique Technique { get; set; }

    public RenderRectangle(in Technique technique)
    {
        this.Technique = technique;
    }

    public override void Initialize()
    {
        base.Initialize();
        if (vbo is null)
        {
            var verts = new Vector2[]
            {
                new Vector2(-1, -1),
                new Vector2(0, 0),
                new Vector2(1, -1),
                new Vector2(1, 0),
                new Vector2(1, 1),
                new Vector2(1, 1),
                new Vector2(-1, 1),
                new Vector2(0, 1)
            };

            var indices = new uint[] { 0, 1, 2, 0, 2, 3 };

            if (Engine.ObjectManager.VertexArrays.TryCreate(
                VertexArrayObjectDescription.VertexBuffer,
                out var result))
            {
                vbo = new VertexBufferObject(result.Asset);
            }
            else
            {
                Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, result.Message);
            }

            vbo.Bind();
            {
                // setup vertex buffer
                vbo.VertexBuffer.Bind();
                vbo.VertexBuffer.VertexAttributePointer(
                    0,
                    2,
                    Silk.NET.OpenGL.VertexAttribPointerType.Float,
                    sizeof(float) * 4,
                    0
                );
                vbo.VertexBuffer.VertexAttributePointer(
                    1,
                    2,
                    Silk.NET.OpenGL.VertexAttribPointerType.Float,
                    sizeof(float) * 4,
                    sizeof(float) * 2
                );

                // setup element buffer
                vbo.ElementBuffer.Bind();
            }
            vbo.Unbind();
            vbo.VertexBuffer.Unbind();
            vbo.ElementBuffer.Unbind();

            vbo.VertexBuffer.NamedBufferData(verts);
            vbo.ElementBuffer.NamedBufferData(indices);
        }
    }

    public override unsafe void Render(float dt, object? obj = null)
    {
        base.Render(dt);
        Technique.Bind();

        vbo.Bind();
        Engine
            .GL
            .DrawElements(
                Silk.NET.OpenGL.PrimitiveType.Triangles,
                6,
                Silk.NET.OpenGL.DrawElementsType.UnsignedInt,
                null
            );
        vbo.Unbind();
        Technique.Unbind();
    }
}