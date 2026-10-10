using Horizon.Logging;
using Horizon.Graphics;

namespace Horizon.Rendering;

/// <summary>
/// A technique that works on a whole picture. A Slang file with a fragment stage of its own that includes the vertex
/// stage every screen pass shares (shaders/common/screen.slang, which hands it where it is as <c>texCoords</c>). The
/// post processing passes are these, and so is whatever puts a renderer on screen.
/// </summary>
public class ScreenTechnique : Technique
{
    /// <param name="name">What to keep the program under, so everybody who asks for the same file shares it.</param>
    /// <param name="path">The shader file, with its folder and extension.</param>
    public ScreenTechnique(string name, string path)
    {
        bool created = Horizon.Engine.GameEngine.Instance.ObjectManager.Shaders.TryCreateOrGet(name, ShaderDescription.Screen(path), out var result);

        if (created)
            SetShader(result.Asset);
        else
            Log.Error(result.Message);
    }
}

/// <summary>
/// Puts the picture of a <see cref="Renderer2D"/> on screen (shaders/renderer2d/standard.slang), check out
/// <seealso cref="DeferredRenderer2DTechnique"/> for the one that lights it on the way.
/// </summary>
public class Renderer2DTechnique : ScreenTechnique
{
    protected readonly RenderTarget frameBuffer;

    public Renderer2DTechnique(RenderTarget frameBuffer)
        : this(frameBuffer, "standard") { }

    /// <param name="fragment">The name of the shader in shaders/renderer2d, without its extension.</param>
    protected Renderer2DTechnique(RenderTarget frameBuffer, string fragment)
        : base($"renderer2d_{fragment}", $"shaders/renderer2d/{fragment}.slang")
    {
        this.frameBuffer = frameBuffer;
    }

    protected override void SetUniforms()
    {
        // The albedo is on unit 0, which is where the shader says its sampler is
        frameBuffer.BindAttachment(AttachmentPoint.Color0, 0);
    }
}
