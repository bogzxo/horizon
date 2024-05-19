using CodeKneading.Voxel;

using Horizon.Core;
using Horizon.Engine;
using Horizon.OpenGL;
using Horizon.OpenGL.Buffers;
using Horizon.OpenGL.Descriptions;

namespace CodeKneading.Rendering;

internal class PostProcessingTechnique : Technique
{
    private readonly WorldRenderer renderer;

    public PostProcessingTechnique(in Entity parent)
    {
        SetShader(GameEngine.Instance.ObjectManager.Shaders.Create(ShaderDescription.FromPath("content/shaders", "post")).Asset);

        var world = parent as VoxelWorld;
        this.renderer = world.Renderer;


        GameEngine.Instance.GL.ActiveTexture(Silk.NET.OpenGL.TextureUnit.Texture0);
        GameEngine.Instance.GL.ActiveTexture(Silk.NET.OpenGL.TextureUnit.Texture1);
        GameEngine.Instance.GL.ActiveTexture(Silk.NET.OpenGL.TextureUnit.Texture2);
        GameEngine.Instance.GL.ActiveTexture(Silk.NET.OpenGL.TextureUnit.Texture3);
        GameEngine.Instance.GL.Enable(Silk.NET.OpenGL.EnableCap.Texture2D);

    }

    protected override void SetUniforms()
    {
        SetFboUniforms();
    }

    private void SetFboUniforms()
    {
        GameEngine.Instance.GL.BindTextureUnit(0, renderer.FrameBuffer.Attachments[Silk.NET.OpenGL.FramebufferAttachment.ColorAttachment0].Handle);
        SetUniform("uTexAlbedo", 0);

        GameEngine.Instance.GL.BindTextureUnit(1, renderer.FrameBuffer.Attachments[Silk.NET.OpenGL.FramebufferAttachment.ColorAttachment1].Handle);
        SetUniform("uTexSun", 1);

        GameEngine.Instance.GL.BindTextureUnit(2, renderer.FrameBuffer.Attachments[Silk.NET.OpenGL.FramebufferAttachment.DepthAttachment].Handle);
        SetUniform("uTexDepth", 2);

        GameEngine.Instance.GL.BindTextureUnit(3, renderer.FrameBuffer.Attachments[Silk.NET.OpenGL.FramebufferAttachment.ColorAttachment2].Handle);
        SetUniform("uTexFrag", 3);
    }
}