using System.Numerics;

using CodeKneading.Player;
using CodeKneading.Voxel;

using Horizon.Core;
using Horizon.Engine;
using Horizon.OpenGL;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Buffers;
using Horizon.OpenGL.Descriptions;
using Horizon.Rendering;

using Silk.NET.Maths;
using Silk.NET.OpenGL;

namespace CodeKneading.Rendering;

internal class PostProcessingTechnique : Technique
{
    private RenderTarget target;
    private Horizon.OpenGL.Assets.Texture skybox;
    private uint skyTextureHandle;
    private FrameBufferObject skyTextureFbo;

    public PostProcessingTechnique()
    {
        if(GameEngine.Instance.ObjectManager.Shaders.TryCreate(
            ShaderDescription.FromPath(
                "content/shaders",
                "post"
                ),
            out var result))
        {
            SetShader(result.Asset);
        }
        else
        {
            Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, result.Message);
        }

        GameEngine.Instance.GL.ActiveTexture(Silk.NET.OpenGL.TextureUnit.Texture5);
        GameEngine.Instance.GL.ActiveTexture(Silk.NET.OpenGL.TextureUnit.Texture6);
    }

    public void SetRenderTarget(in RenderTarget target)
    {
        this.target = target;
    }

    protected override void SetUniforms()
    {
        SetFboUniforms();
    }

    internal void SetSkyBox(FrameBufferObject frameBuffer)
    {
        this.skyTextureFbo = frameBuffer;
    }

    private void SetFboUniforms()
    {
        target.BindForReading(Silk.NET.OpenGL.FramebufferAttachment.ColorAttachment0, 5);
        SetUniform("uTexAlbedo", 5);

        target.BindForReading(Silk.NET.OpenGL.FramebufferAttachment.DepthAttachment, 6);
        SetUniform("uTexDepth", 6);

        skyTextureFbo.BindAttachment(FramebufferAttachment.ColorAttachment0, 7);
        SetUniform("uTexSky", 7);

        SetUniform("uCameraDir", GameEngine.Instance.ActiveCamera.Direction);
    }
}