using System.Numerics;

using Horizon.Engine;
using Horizon.OpenGL;
using Horizon.OpenGL.Buffers;
using Horizon.OpenGL.Descriptions;
using Horizon.Rendering.UIX.Skinning;

using Silk.NET.OpenGL;

namespace Horizon.Rendering.UIX.Drawing;

/// <summary>
/// The GL side of the UI: one vertex buffer, one shader, and a draw call per batch of a <see cref="UIDrawList"/>.
/// </summary>
internal sealed class UIRenderer
{
    private const string UNIFORM_VIEW_PROJECTION = "uViewProjection";
    private const string UNIFORM_SKIN = "uSkin";
    private const string UNIFORM_FONT = "uFont";
    private const string UNIFORM_IMAGE = "uImage";

    private const int SKIN_UNIT = 0;
    private const int FONT_UNIT = 1;
    private const int IMAGE_UNIT = 2;

    private Technique? technique;
    private VertexBufferObject? buffer;

    // One sampler serves every renderer, so making and dropping compositors doesn't pile samplers up.
    private static uint fontSampler;

    // How many quads the element buffer has indices for. The pattern never changes, so it only grows.
    private int indexedQuads;

    private readonly List<UIDrawList.Batch> batches = [];

    public void Initialize(UISkin skin)
    {
        var engine = GameEngine.Instance;
        var gl = engine.GL;

        // Failures are logged by the asset managers; without both there is nothing to draw with.
        if (!engine
                .ObjectManager
                .Shaders
                .TryCreateOrGet("uix", ShaderDescription.FromPath("shaders/ui", "ui"), out var shader)
            || !engine
                .ObjectManager
                .VertexArrays
                .TryCreate(VertexArrayObjectDescription.VertexBuffer, out var vertexArray))
        {
            return;
        }

        technique = new Technique(shader.Asset);
        buffer = new VertexBufferObject(vertexArray.Asset);

        buffer.Bind();
        buffer.VertexBuffer.VertexAttributePointer(0, 2, VertexAttribPointerType.Float, UIVertex.SizeInBytes, 0);
        buffer.VertexBuffer.VertexAttributePointer(1, 2, VertexAttribPointerType.Float, UIVertex.SizeInBytes, 8);
        buffer.VertexBuffer.VertexAttributeIPointer(2, 1, VertexAttribIType.UnsignedInt, UIVertex.SizeInBytes, 16);
        buffer.VertexBuffer.VertexAttributeIPointer(3, 1, VertexAttribIType.UnsignedInt, UIVertex.SizeInBytes, 20);
        buffer.Unbind();

        // Text is nearly always drawn smaller than the atlas, which a nearest filter turns to SHIT, so
        // the font gets as many mipmaps as can be and is sampled through a smooth sampler. The texture's own filter is
        // left alone for anything else drawing with the same font image...
        uint font = skin.Font.Texture.Handle;
        if (font != 0)
        {
            gl.TextureParameter(font, TextureParameterName.TextureMaxLevel, 1000);
            gl.GenerateTextureMipmap(font);
        }

        if (fontSampler != 0)
            return;

        fontSampler = gl.CreateSampler();
        gl.SamplerParameter(fontSampler, SamplerParameterI.MinFilter, (int)GLEnum.LinearMipmapLinear);
        gl.SamplerParameter(fontSampler, SamplerParameterI.MagFilter, (int)GLEnum.Linear);
        gl.SamplerParameter(fontSampler, SamplerParameterI.WrapS, (int)GLEnum.ClampToEdge);
        gl.SamplerParameter(fontSampler, SamplerParameterI.WrapT, (int)GLEnum.ClampToEdge);
    }

    /// <summary>
    /// Copies a finished draw list to the GPU. The list is free to be reused afterwards.
    /// </summary>
    public void Upload(UIDrawList list)
    {
        if (buffer is null)
            return;

        batches.Clear();
        batches.AddRange(list.Batches);

        if (list.QuadCount == 0)
            return;

        buffer.VertexBuffer.NamedBufferData(list.Vertices);

        if (list.QuadCount > indexedQuads)
        {
            indexedQuads = (int)BitOperations.RoundUpToPowerOf2((uint)list.QuadCount);

            uint[] indices = new uint[indexedQuads * 6];
            for (uint quad = 0; quad < indexedQuads; quad++)
            {
                uint vertex = quad * 4;
                uint index = quad * 6;

                indices[index + 0] = vertex + 0;
                indices[index + 1] = vertex + 1;
                indices[index + 2] = vertex + 2;
                indices[index + 3] = vertex + 0;
                indices[index + 4] = vertex + 2;
                indices[index + 5] = vertex + 3;
            }

            buffer.ElementBuffer.NamedBufferData(indices);
        }
    }

    /// <summary>
    /// Draws what was last uploaded, on top of whatever is already there.
    /// </summary>
    public unsafe void Draw(Camera camera, UISkin skin)
    {
        if (technique is null || buffer is null || batches.Count == 0)
            return;

        var gl = GameEngine.Instance.GL;

        // The UI is painted back to front with alpha blending. The rest of the engine doesn't expect
        // either, so everything touched here is put back afterwards.
        bool blend = gl.IsEnabled(EnableCap.Blend);
        bool depthTest = gl.IsEnabled(EnableCap.DepthTest);
        var sourceRgb = (BlendingFactor)gl.GetInteger(GetPName.BlendSrcRgb);
        var destinationRgb = (BlendingFactor)gl.GetInteger(GetPName.BlendDstRgb);
        var sourceAlpha = (BlendingFactor)gl.GetInteger(GetPName.BlendSrcAlpha);
        var destinationAlpha = (BlendingFactor)gl.GetInteger(GetPName.BlendDstAlpha);

        gl.Enable(EnableCap.Blend);
        gl.BlendFuncSeparate(
            BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha,
            BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
        gl.Disable(EnableCap.DepthTest);

        Matrix4x4 viewProjection = camera.ViewProj;

        technique.Bind();
        technique.SetUniform(UNIFORM_VIEW_PROJECTION, in viewProjection);
        technique.SetUniform(UNIFORM_SKIN, SKIN_UNIT);
        technique.SetUniform(UNIFORM_FONT, FONT_UNIT);
        technique.SetUniform(UNIFORM_IMAGE, IMAGE_UNIT);

        gl.BindTextureUnit(SKIN_UNIT, skin.Texture.Handle);
        gl.BindTextureUnit(FONT_UNIT, skin.Font.Texture.Handle);
        gl.BindSampler(FONT_UNIT, fontSampler);

        buffer.Bind();

        foreach (var batch in batches)
        {
            // A batch without an image of its own still needs something valid behind the sampler.
            gl.BindTextureUnit(IMAGE_UNIT, batch.Image != 0 ? batch.Image : skin.Texture.Handle);

            gl.DrawElements(
                PrimitiveType.Triangles,
                (uint)batch.QuadCount * 6,
                DrawElementsType.UnsignedInt,
                (void*)(batch.FirstQuad * 6 * sizeof(uint)));
        }

        buffer.Unbind();
        technique.Unbind();
        gl.BindSampler(FONT_UNIT, 0);

        gl.BlendFuncSeparate(sourceRgb, destinationRgb, sourceAlpha, destinationAlpha);
        if (!blend)
            gl.Disable(EnableCap.Blend);
        if (depthTest)
            gl.Enable(EnableCap.DepthTest);
    }
}
