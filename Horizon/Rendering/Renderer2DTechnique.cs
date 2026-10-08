using Horizon.Logging;
using Horizon.OpenGL;
using Horizon.OpenGL.Buffers;
using Horizon.OpenGL.Descriptions;

using Silk.NET.OpenGL;

namespace Horizon.Rendering;

/// <summary>
/// A technique that works on a whole picture: a fragment shader of its own over the vertex stage every screen pass
/// shares (shaders/common/screen.vert, which hands it where it is as <c>texCoords</c>). The post processing passes
/// are these, and so is whatever puts a renderer on screen.
/// </summary>
public class ScreenTechnique : Technique
{
    /// <summary>The vertex stage of every screen pass.</summary>
    public const string VERTEX_SHADER = "shaders/common/screen.vert";

    /// <param name="name">What to keep the program under, so everybody who asks for the same fragment shader shares it.</param>
    /// <param name="fragmentPath">The fragment shader, with its folder and extension.</param>
    public ScreenTechnique(string name, string fragmentPath)
    {
        bool created = Horizon.Engine.GameEngine.Instance.ObjectManager.Shaders.TryCreateOrGet(
            name,
            new ShaderDescription
            {
                Definitions =
                [
                    new ShaderDefinition(ShaderType.VertexShader, VERTEX_SHADER, string.Empty),
                    new ShaderDefinition(ShaderType.FragmentShader, fragmentPath, string.Empty)
                ]
            },
            out var result);

        if (created)
            SetShader(result.Asset);
        else
            Log.Error(result.Message);
    }
}

/// <summary>
/// Puts the picture of a <see cref="Renderer2D"/> on screen (shaders/renderer2d/standard.frag), check out
/// <seealso cref="DeferredRenderer2DTechnique"/> for the one that lights it on the way.
/// </summary>
public class Renderer2DTechnique : ScreenTechnique
{
    protected readonly FrameBufferObject frameBuffer;

    public Renderer2DTechnique(FrameBufferObject frameBuffer)
        : this(frameBuffer, "standard") { }

    /// <param name="fragment">The name of the fragment shader in shaders/renderer2d, without its extension.</param>
    protected Renderer2DTechnique(FrameBufferObject frameBuffer, string fragment)
        : base($"renderer2d_{fragment}", $"shaders/renderer2d/{fragment}.frag")
    {
        this.frameBuffer = frameBuffer;
    }

    protected override void SetUniforms()
    {
        // The albedo is on unit 0, which is where the shader says its sampler is
        frameBuffer.BindAttachment(FramebufferAttachment.ColorAttachment0, 0);
    }
}
