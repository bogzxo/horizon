using System.Numerics;

using Horizon.Engine;
using Horizon.OpenGL;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Descriptions;
using Horizon.Rendering;

using Silk.NET.Core.Native;

namespace CodeKneading.Rendering;

internal class SkyBoxTechnique : Technique
{
    private RenderTarget target;
    private Texture skybox;

    public SkyBoxTechnique()
    {
        if(GameEngine.Instance.ObjectManager.Shaders.TryCreate(ShaderDescription.FromPath("content/shaders", "skybox"), out var skyShaderResult))
        {
            SetShader(skyShaderResult.Asset);
        }
        else
        {
            Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, skyShaderResult.Message);
        }

        if(GameEngine.Instance.ObjectManager.Textures.TryCreate(new TextureDescription
        {
            Width = 2048,
            Height = 2048,
            Definition = TextureDefinition.RgbaUnsignedByte with
            {
                TextureTarget = Silk.NET.OpenGL.TextureTarget.TextureCubeMap
            },
            Paths = ["content/textures/skybox/right.jpg", "content/textures/skybox/left.jpg", "content/textures/skybox/top.jpg", "content/textures/skybox/bottom.jpg", "content/textures/skybox/front.jpg", "content/textures/skybox/back.jpg"]
        }, out var result))
        {
            skybox = result.Asset;
        }
        else
        {
            Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, result.Message);
        }

        GameEngine.Instance.GL.ActiveTexture(Silk.NET.OpenGL.TextureUnit.Texture0);
        GameEngine.Instance.GL.ActiveTexture(Silk.NET.OpenGL.TextureUnit.Texture1);
        GameEngine.Instance.GL.ActiveTexture(Silk.NET.OpenGL.TextureUnit.Texture2);
        GameEngine.Instance.GL.ActiveTexture(Silk.NET.OpenGL.TextureUnit.Texture3);
        GameEngine.Instance.GL.Enable(Silk.NET.OpenGL.EnableCap.Texture2D);
    }

    public void SetRenderTarget(in RenderTarget target)
    {
        this.target = target;
    }

    protected override void SetUniforms()
    {
        SetFboUniforms();
    }

    private void SetFboUniforms()
    {
        skybox.Bind(0);
        SetUniform("uTexAlbedo", 0);

        if (Matrix4x4.Invert(GameEngine.Instance.ActiveCamera.Projection, out var invProj))
            SetUniform("uInvProj", invProj);

        if (Matrix4x4.Invert(GameEngine.Instance.ActiveCamera.View, out var invView))
            SetUniform("uInvView", invView);
    }
}
