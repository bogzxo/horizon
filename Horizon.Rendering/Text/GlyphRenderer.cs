using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

using Horizon.Core.Components;
using Horizon.Core.Data;
using Horizon.Engine;
using Horizon.OpenGL;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Descriptions;

namespace Horizon.Rendering.Text;

public class GlyphRenderer : GameObject
{
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct TextVertex(in float x, in float y, in float tx, in float ty)
    {
        private readonly Vector2 Position = new(x, y);
        private readonly Vector2 TexCoords = new(tx, ty);
    }

    public TransformComponent2D Transform { get; init; }

    public Texture FontTexture { get => FontImporter.Texture; }

    private CharDefinition GetDefinition(in char ch) => FontImporter.Definitions.ContainsKey(ch) ? FontImporter.Definitions[ch] : default;

    private BMFontImporter FontImporter;
    private VertexArrayObject vao;
    private Technique Technique;
    private uint count;

    public GlyphRenderer()
    {
        Transform = AddComponent<TransformComponent2D>();
    }

    public override void Initialize()
    {
        base.Initialize();

        FontImporter = new("fonts/vcr_mono/", "vcr_mono.fnt");
        vao = Engine.ObjectManager.VertexArrays.Create(new VertexArrayObjectDescription
        {
            Buffers = new() {
                { VertexArrayBufferAttachmentType.ArrayBuffer, BufferObjectDescription.ArrayBuffer }
            }
        }).Asset;

        vao.Bind();
        vao[VertexArrayBufferAttachmentType.ArrayBuffer].Bind();
        vao[VertexArrayBufferAttachmentType.ArrayBuffer].VertexAttributePointer(0, 2, Silk.NET.OpenGL.VertexAttribPointerType.Float, 16, 0);
        vao[VertexArrayBufferAttachmentType.ArrayBuffer].VertexAttributePointer(1, 2, Silk.NET.OpenGL.VertexAttribPointerType.Float, 16, 8);
        vao[VertexArrayBufferAttachmentType.ArrayBuffer].Unbind();
        vao.Unbind();

        var verts = GenerateCharacterVerticesFromString("Hello, world!");
        vao[VertexArrayBufferAttachmentType.ArrayBuffer].NamedBufferData(verts);
        count = (uint)verts.Length;

        Technique = new(Engine.ObjectManager.Shaders.CreateOrGet("bitmap_font", ShaderDescription.FromPath("shaders/font/", "bitmap")));
    }

    private TextVertex[] GenerateCharacterVerticesFromString(in string str)
    {
        List<TextVertex> vertices = [];

        foreach (var ch in str.ToCharArray())
        {
            vertices.AddRange(GenerateCharacterVertices(GetDefinition(ch)));
        }

        return [.. vertices];
    }

    float offsetX = 0, offsetY = 0;
    TextVertex[] GenerateCharacterVertices(CharDefinition charDef)
    {
        // Calculate vertices positions
        float x1 = offsetX + charDef.Offset.X;
        float y1 = offsetY ;
        float x2 = x1 + charDef.Size.X;
        float y2 = y1 + charDef.Size.Y;

        offsetX += charDef.Size.X + charDef.Offset.X;

        // Calculate texture coordinates
        float tx1 = charDef.Position.X / FontTexture.Width;
        float ty2 = charDef.Position.Y / FontTexture.Height;
        float tx2 = (charDef.Position.X + charDef.Size.X) / FontTexture.Width;
        float ty1 = (charDef.Position.Y + charDef.Size.Y) / FontTexture.Height;

        // Define vertices
        TextVertex[] vertices =
        {
            // First triangle
            new TextVertex(x1, y1, tx1, ty1),
            new TextVertex(x1, y2, tx1, ty2),
            new TextVertex(x2, y1, tx2, ty1),
        
            // Second triangle
            new TextVertex(x2, y1, tx2, ty1),
            new TextVertex(x1, y2, tx1, ty2),
            new TextVertex(x2, y2, tx2, ty2)
        };

        return vertices;
    }


    public override void Render(float dt, object? obj = null)
    {
        base.Render(dt, obj);

        vao.Bind();
        Technique.Bind();
        Technique.SetUniform("u_vp", Engine.ActiveCamera.ProjView);
        Technique.SetUniform("u_model", Transform.ModelMatrix);

        Engine.GL.BindTextureUnit(0, FontTexture.Handle);
        Technique.SetUniform("u_bitmap", 0);

        Engine.GL.DrawArrays(Silk.NET.OpenGL.PrimitiveType.Triangles, 0, count);
        Engine.GL.UseProgram(0);
        vao.Unbind();

    }
}
