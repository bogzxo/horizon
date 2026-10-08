using Horizon.Engine;
using Horizon.Rendering.Lighting;

using Silk.NET.OpenGL;

namespace Horizon.Rendering;

/// <summary>
/// Puts the G-buffer of a <see cref="DeferredRenderer2D"/> on screen, lit by its lights (shaders/renderer2d/deferred.frag)
/// and, when the renderer path traces, by what the tracer found.
/// </summary>
public class DeferredRenderer2DTechnique : Renderer2DTechnique
{
    private const string UNIFORM_AMBIENT = "uAmbient";
    private const string UNIFORM_PIXEL_SIZE = "uPixelSize";
    private const string UNIFORM_LIGHTING_MODE = "uLightingMode";
    private const string UNIFORM_GI_STRENGTH = "uGiStrength";
    private const string UNIFORM_GI_AMBIENT_SCALE = "uGiAmbientScale";

    // The G-buffer takes the first texture units, what blocks the lights unit 3 (see lighting/direct.glsl), the tracer's result unit 4
    private const uint GI_UNIT = 4;

    private readonly DeferredRenderer2D renderer;

    public DeferredRenderer2DTechnique(DeferredRenderer2D renderer)
        : base(renderer.FrameBuffer, "deferred")
    {
        this.renderer = renderer;
    }

    protected override void SetUniforms()
    {
        // The G-buffer takes the first three units, which is where the shader says its samplers are
        frameBuffer.BindAttachment(FramebufferAttachment.ColorAttachment0, 0);
        frameBuffer.BindAttachment(FramebufferAttachment.ColorAttachment1, 1);
        frameBuffer.BindAttachment(FramebufferAttachment.ColorAttachment2, 2);

        // Where a fragment is in the world isn't in the G-buffer, it follows from where it is on screen, the shader
        // works it out with the inverse view projection of the camera block
        CameraBlock.Use(GameEngine.Instance.ActiveCamera);

        renderer.BindLighting(this);

        var ambient = renderer.ShownAmbient;
        SetUniform(UNIFORM_AMBIENT, in ambient);
        SetUniform(UNIFORM_PIXEL_SIZE, renderer.ShownLightingPixelSize);

        bool traced = renderer.Lighting == LightingMode.PathTraced && renderer.PathTracing.Result is { } found;
        SetUniform(UNIFORM_LIGHTING_MODE, traced ? renderer.ShowTracedLight ? 2 : 1 : 0);
        if (traced)
        {
            renderer.PathTracing.Result!.Bind(GI_UNIT);
            SetUniform(UNIFORM_GI_STRENGTH, MathF.Max(renderer.PathTracing.Strength, 0.0f));
            SetUniform(UNIFORM_GI_AMBIENT_SCALE, Math.Clamp(renderer.PathTracing.AmbientScale, 0.0f, 1.0f));
        }
    }
}
