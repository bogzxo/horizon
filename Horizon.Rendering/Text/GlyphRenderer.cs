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

using Logger = Bogz.Logging.Loggers.ConcurrentLogger;

namespace Horizon.Rendering.Text;

public class TextLabel
{
    public string Text { get; set; } = string.Empty;
    public TransformComponent2D Transform { get; init; }
    public float Width { get; internal set; }
    public Origin Origin { get; init; } = Origin.TopLeft;
    public bool IsVisible { get; set; } = true;

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

    public void MarkDirty() => _isDirty = true;

    public TransformComponent2D Transform { get; init; }

    public Dictionary<string, TextLabel> Labels { get; init; }

    public OpenGL.Assets.Texture FontTexture { get => FontImporter.Texture; }

    //private CharDefinition GetDefinition(in char ch) => FontImporter.Definitions.ContainsKey(ch) ? FontImporter.Definitions[ch] : default;

    private CharDefinition GetDefinition(char c)
    {
        // Return the actual definition if it exists
        if (FontImporter.Definitions.TryGetValue(c, out var def))
            return def;

        // Fallback gracefully (e.g., return the '?' character if a glyph is missing)
        if (FontImporter.Definitions.TryGetValue('?', out var fallback))
            return fallback;

        // Absolute worst-case fallback, return an empty struct
        return default;
    }

    private BMFontImporter FontImporter;
    private VertexArrayObject vao;
    private Technique Technique;
    private uint count;
    private readonly string fontFile = "vcr_mono.fnt", fontPath = "fonts/vcr_mono/";

    public GlyphRenderer(in string fontPath, in string fontFile) : this()
    {
        this.fontPath = fontPath;
        this.fontFile = fontFile;
    }

    public GlyphRenderer()
    {
        Transform = AddComponent<TransformComponent2D>();
        Labels = new Dictionary<string, TextLabel>();
    }

    public TextLabel this[string key]
    {
        get
        {
            MarkDirty();
            return Labels[key];
        }
    }

    public void AddLabel(in string id, in TextLabel label)
    {
        if (Labels.ContainsKey(id)) return;

        Labels.Add(id, label);
        _isDirty = true;
    }

    private BufferObject labelBuffer;

    public Vector2 CalculateSize(in string text) => CalculateSize(text, Vector2.One);

    public Vector2 CalculateSize(in string text, Vector2 scale)
    {
        float totalWidth = 0f;
        float maxHeight = 0f;

        for (int i = 0; i < text.Length; i++)
        {
            var charDef = GetDefinition(text[i]);

            // Multiply character dimensions by the label's scale
            totalWidth += charDef.XAdvance * scale.X;

            float charHeight = (charDef.Offset.Y + charDef.Size.Y) * scale.Y;
            if (charHeight > maxHeight)
            {
                maxHeight = charHeight;
            }
        }

        return new Vector2(totalWidth, maxHeight);
    }

    private void BuildBuffer()
    {
        List<TextVertex> vertices = [];
        List<TextLabelDef> defs = [];

        uint index = 0;

        foreach (var (identifier, lbl) in Labels)
        {
            if (!lbl.IsVisible)
                continue;

            // Extract scale from the label's transform matrix/component
            Vector2 scale = lbl.Transform.Size;

            // 1. Calculate bounding size scaled properly
            Vector2 textSize = CalculateSize(lbl.Text, scale);
            lbl.Width = textSize.X;

            // 2. Compute translation offset based on scaled origin dimensions
            float originOffsetX = 0f;
            float originOffsetY = 0f;

            switch (lbl.Origin)
            {
                case Origin.TopLeft:
                    originOffsetX = 0f;
                    originOffsetY = 0f;
                    break;

                case Origin.Top:
                    originOffsetX = -textSize.X * 0.5f;
                    originOffsetY = 0f;
                    break;

                case Origin.TopRight:
                    originOffsetX = -textSize.X;
                    originOffsetY = 0f;
                    break;

                case Origin.Left:
                    originOffsetX = 0f;
                    originOffsetY = -textSize.Y * 0.5f;
                    break;

                case Origin.Center:
                    originOffsetX = -textSize.X * 0.5f;
                    originOffsetY = -textSize.Y * 0.5f;
                    break;

                case Origin.Right:
                    originOffsetX = -textSize.X;
                    originOffsetY = -textSize.Y * 0.5f;
                    break;

                case Origin.BottomLeft:
                    originOffsetX = 0f;
                    originOffsetY = -textSize.Y;
                    break;

                case Origin.Bottom:
                    originOffsetX = -textSize.X * 0.5f;
                    originOffsetY = -textSize.Y;
                    break;

                case Origin.BottomRight:
                    originOffsetX = -textSize.X;
                    originOffsetY = -textSize.Y;
                    break;
            }

            float offsetX = 0f;
            float offsetY = 0f;

            // 3. Generate vertices applying the scale factor to glyph positions and dimensions
            for (int i = 0; i < lbl.Text.Length; i++)
            {
                var charDef = GetDefinition(lbl.Text[i]);

                float x1 = offsetX + (charDef.Offset.X * scale.X) + originOffsetX;
                float y1 = offsetY + originOffsetY;

                float x2 = x1 + (charDef.Size.X * scale.X);
                float y2 = y1 + (charDef.Size.Y * scale.Y);

                // Texture coordinates remain standard UV range [0, 1]
                float tx1 = charDef.Position.X / FontTexture.Width;
                float ty2 = charDef.Position.Y / FontTexture.Height;
                float tx2 = (charDef.Position.X + charDef.Size.X) / FontTexture.Width;
                float ty1 = (charDef.Position.Y + charDef.Size.Y) / FontTexture.Height;

                // First triangle
                vertices.Add(new TextVertex(index, x1, y1, tx1, ty1));
                vertices.Add(new TextVertex(index, x1, y2, tx1, ty2));
                vertices.Add(new TextVertex(index, x2, y1, tx2, ty1));

                // Second triangle
                vertices.Add(new TextVertex(index, x2, y1, tx2, ty1));
                vertices.Add(new TextVertex(index, x1, y2, tx1, ty2));
                vertices.Add(new TextVertex(index, x2, y2, tx2, ty2));

                // Advance scaled X offset
                offsetX += charDef.XAdvance * scale.X;
            }

            // Pass Matrix without Scale if building mesh scaled on CPU, OR
            // construct Matrix translation-only to avoid double-scaling in shader.
            Matrix4x4 translationMat = Matrix4x4.CreateTranslation(lbl.Transform.Position.X, lbl.Transform.Position.Y, 0f);

            defs.Add(new TextLabelDef
            {
                ModelMat = translationMat
            });

            index++;
        }

        labelBuffer.NamedBufferData(CollectionsMarshal.AsSpan(defs));
        vao[VertexArrayBufferAttachmentType.ArrayBuffer].NamedBufferData(CollectionsMarshal.AsSpan(vertices));
        count = (uint)vertices.Count;
    }

    public override void Initialize()
    {
        base.Initialize();
        FontImporter = new(fontPath, fontFile);

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

        if (!Enabled || Engine.ActiveCamera == null) return;

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