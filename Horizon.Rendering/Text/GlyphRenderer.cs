using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

using Horizon.Core.Components;
using Horizon.Core.Data;
using Horizon.Engine;
using Horizon.OpenGL;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Descriptions;

using Silk.NET.OpenGL;
using Silk.NET.SDL;

using Logger = Bogz.Logging.Loggers.ConcurrentLogger;

namespace Horizon.Rendering.Text;

public class TextLabel
{
    public string Text { get; set; } = string.Empty;
    public TransformComponent2D Transform { get; init; }
    public float Width { get; internal set; }

    public TextLabel()
    {
        Transform = new TransformComponent2D();
        Transform.Initialize();
    }
}

public class GlyphRenderer : GameObject
{
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct TextLabelDef
    {
        public readonly Matrix4x4 ModelMat { get; init; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct TextVertex(in uint id, in float x, in float y, in float tx, in float ty)
    {
        private readonly Vector2 Position = new(x, y);
        private readonly Vector2 TexCoords = new(tx, ty);
        private readonly uint LabelID = id;
    }

    private bool _isDirty = false;
    public TransformComponent2D Transform { get; init; }

    public Dictionary<string, TextLabel> Labels { get; init; }

    public OpenGL.Assets.Texture FontTexture { get => FontImporter.Texture; }

    private CharDefinition GetDefinition(in char ch) => FontImporter.Definitions.ContainsKey(ch) ? FontImporter.Definitions[ch] : default;

    private BMFontImporter FontImporter;
    private VertexArrayObject vao;
    private Technique Technique;
    private uint count;

    public GlyphRenderer()
    {
        Transform = AddComponent<TransformComponent2D>();
        Labels = new Dictionary<string, TextLabel>();
    }

    public TextLabel this[string key]
    {
        get => Labels[key];
    }

    public void AddLabel(in string id, in TextLabel label)
    {
        if (Labels.ContainsKey(id)) return;

        Labels.Add(id, label);
        _isDirty = true;
    }

    private BufferObject labelBuffer;

    public Vector2 CalculateSize(in string text)
    {
        float offsetX = 0, offsetY = 0;
        for (int i = 0; i < text.Length; i++)
        {
            var charDef = GetDefinition(text[i]);

            // Calculate vertices positions
            float x1 = offsetX + charDef.Offset.X;
            float y1 = offsetY;
            float x2 = x1 + charDef.Size.X;
            float y2 = y1 + charDef.Size.Y;

            offsetX += charDef.Size.X + charDef.Offset.X;
            offsetY += charDef.Size.Y + charDef.Offset.Y;

            // Calculate texture coordinates
            float tx1 = charDef.Position.X / FontTexture.Width;
            float ty2 = charDef.Position.Y / FontTexture.Height;
            float tx2 = (charDef.Position.X + charDef.Size.X) / FontTexture.Width;
            float ty1 = (charDef.Position.Y + charDef.Size.Y) / FontTexture.Height;
        }

        return new Vector2(offsetX, offsetY);
    }

    private void BuildBuffer()
    {
        List<TextVertex> vertices = [];
        List <TextLabelDef> defs = [];

        uint index = 0;
        float offsetX = 0, offsetY = 0;

        // TODO: this is slow and shit as shit fuck fuuuckk
        foreach (var (identifier, lbl) in Labels)
        {
            offsetX = offsetY = 0;
            for (int i = 0; i < lbl.Text.Length; i++)
            {
                var charDef = GetDefinition(lbl.Text[i]);

                // Calculate vertices positions
                float x1 = offsetX + charDef.Offset.X;
                float y1 = offsetY;
                float x2 = x1 + charDef.Size.X;
                float y2 = y1 + charDef.Size.Y;

                offsetX += charDef.Size.X + charDef.Offset.X;

                // Calculate texture coordinates
                float tx1 = charDef.Position.X / FontTexture.Width;
                float ty2 = charDef.Position.Y / FontTexture.Height;
                float tx2 = (charDef.Position.X + charDef.Size.X) / FontTexture.Width;
                float ty1 = (charDef.Position.Y + charDef.Size.Y) / FontTexture.Height;

                vertices.AddRange([
                    // First triangle
                    new TextVertex(index, x1, y1, tx1, ty1),
                    new TextVertex(index, x1, y2, tx1, ty2),
                    new TextVertex(index, x2, y1, tx2, ty1),
        
                    // Second triangle
                    new TextVertex(index, x2, y1, tx2, ty1),
                    new TextVertex(index, x1, y2, tx1, ty2),
                    new TextVertex(index, x2, y2, tx2, ty2)]);

            }

            defs.Add(new TextLabelDef {
                ModelMat = lbl.Transform.ModelMatrix
            });
            // TODO: modifying an array while we index it :))))))))))))))))))))))
            Labels[identifier].Width = offsetX;

            index++;
        }

        labelBuffer.NamedBufferData(CollectionsMarshal.AsSpan(defs));
        vao[VertexArrayBufferAttachmentType.ArrayBuffer].NamedBufferData(CollectionsMarshal.AsSpan(vertices));
        count = (uint)vertices.Count;
    }

    public override void Initialize()
    {
        base.Initialize();
        FontImporter = new("fonts/vcr_mono/", "vcr_mono.fnt");

        unsafe
        {
            if (Engine
             .ObjectManager
             .Buffers
             .TryCreate(
                 new BufferObjectDescription
                 {
                     IsStorageBuffer = true,
                 },
                 out var storeResult)
             )
            {
                labelBuffer = storeResult.Asset;
            }
            else
            {
                Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, storeResult.Message);
            }
        }

        if (Engine.ObjectManager.VertexArrays.TryCreate(new VertexArrayObjectDescription
        {
            Buffers = new() {
                { VertexArrayBufferAttachmentType.ArrayBuffer, BufferObjectDescription.ArrayBuffer }
            }
        }, out var result)) { vao = result.Asset; }
        else
        {
            Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, result.Message);
        }

        vao.Bind();
        vao[VertexArrayBufferAttachmentType.ArrayBuffer].Bind();
        vao[VertexArrayBufferAttachmentType.ArrayBuffer].VertexAttributePointer(0, 2, Silk.NET.OpenGL.VertexAttribPointerType.Float, 20, 0);
        vao[VertexArrayBufferAttachmentType.ArrayBuffer].VertexAttributePointer(1, 2, Silk.NET.OpenGL.VertexAttribPointerType.Float, 20, 8);
        vao[VertexArrayBufferAttachmentType.ArrayBuffer].VertexAttributeIPointer(2, 1, Silk.NET.OpenGL.VertexAttribIType.UnsignedInt, 20, 16);
        vao[VertexArrayBufferAttachmentType.ArrayBuffer].Unbind();
        vao.Unbind();


        if (Engine.ObjectManager.Shaders.TryCreateOrGet(
            "bitmap_font",
            ShaderDescription.FromPath(
                "shaders/font/",
           "bitmap"),
            out var shaderResult
            ))
        {
            Technique = new(shaderResult.Asset);
        }
        else
        {
            Logger.Instance.Log(Bogz.Logging.LogLevel.Error, shaderResult.Message);
        }
    }

    public override void Render(float dt, object? obj = null)
    {
        base.Render(dt, obj);

        if (_isDirty)
        {
            BuildBuffer();
            _isDirty = false;
        }

        if (count == 0) return;
        
        vao.Bind();
        Technique.Bind();
        Technique.BindBuffer("labelData", labelBuffer);
        Technique.SetUniform("u_vp", Engine.ActiveCamera.ViewProj);
        Technique.SetUniform("u_model", Transform.ModelMatrix);

        Engine.GL.BindTextureUnit(0, FontTexture.Handle);
        Technique.SetUniform("u_bitmap", 0);

        Engine.GL.DrawArrays(Silk.NET.OpenGL.PrimitiveType.Triangles, 0, count);
        Engine.GL.UseProgram(0);
        vao.Unbind();

    }
}
