using Bogz.Logging;

using Horizon.Engine;
using Horizon.OpenGL;
using Horizon.OpenGL.Buffers;
using Horizon.OpenGL.Descriptions;

using Silk.NET.OpenGL;

namespace Horizon.Rendering;

/// <summary>
/// Base <see cref="Renderer2D"/> technique, check out <seealso cref="DeferredRenderer2DTechnique"/>.
/// </summary>
public class Renderer2DTechnique : Technique
{
    protected readonly FrameBufferObject frameBuffer;
    protected virtual string UNIFORM_ALBEDO { get; } = "uTexAlbedo";
    protected virtual string ShaderFileName { get; } = "standard";

    public Renderer2DTechnique(FrameBufferObject frameBuffer)
        : base()
    {
        this.frameBuffer = frameBuffer;
        if (GameEngine
                .Instance
                .ObjectManager
                .Shaders
                .TryCreateOrGet(
                    $"renderer2d_{ShaderFileName}",
                    ShaderDescription.FromPath("shaders/renderer2d", ShaderFileName),
                    out var result
                ))
        {
            SetShader(result.Asset);
        }
        else
        {
            Logger.Instance.Log(Bogz.Logging.LogLevel.Error, result.Message);
        }
    }

    protected override void SetUniforms()
    {
        frameBuffer.BindAttachment(FramebufferAttachment.ColorAttachment0, 0);
        SetUniform(UNIFORM_ALBEDO, 0);
    }
}