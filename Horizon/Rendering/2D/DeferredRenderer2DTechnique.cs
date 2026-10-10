using System.Numerics;

using Horizon.Engine;
using Horizon.Graphics;
using Horizon.Rendering.Lighting;

namespace Horizon.Rendering;

/// <summary>
/// Puts the G-buffer of a <see cref="DeferredRenderer2D"/> on screen, lit by its lights (shaders/renderer2d/deferred.slang)
/// and, when the renderer path traces, by what the tracer found.
/// </summary>
public class DeferredRenderer2DTechnique : Renderer2DTechnique
{
    private const string UNIFORM_AMBIENT = "uAmbient";
    private const string UNIFORM_PIXEL_SIZE = "uPixelSize";
    private const string UNIFORM_LIGHTING_MODE = "uLightingMode";
    private const string UNIFORM_GI_STRENGTH = "uGiStrength";
    private const string UNIFORM_GI_AMBIENT_SCALE = "uGiAmbientScale";
    private const string UNIFORM_AO_ENABLED = "uAoEnabled";
    private const string UNIFORM_AO_SAMPLES = "uAoSamples";
    private const string UNIFORM_AO_RADIUS = "uAoRadius";
    private const string UNIFORM_AO_STRENGTH = "uAoStrength";
    private const string UNIFORM_AO_BAKED = "uAoBaked";
    private const string UNIFORM_AO_DIRECT = "uAoDirect";

    private const string UNIFORM_HAS_CELLS = "uHasCells";
    private const string UNIFORM_CELL_ORIGIN = "uCellOrigin";

    // The G-buffer takes the first texture units, the distance fields units 3 and 5 (see lighting/direct.slang), the
    // tracer's result unit 4, the lighting per square units 6 and 7
    private const uint GI_UNIT = 4, CELLS_UNIT = 6, CELLS_EXTRA_UNIT = 7;

    private readonly DeferredRenderer2D renderer;

    public DeferredRenderer2DTechnique(DeferredRenderer2D renderer)
        : base(renderer.FrameBuffer, "deferred")
    {
        this.renderer = renderer;
    }

    protected override void SetUniforms()
    {
        // The G-buffer takes the first three units, which is where the shader says its samplers are
        frameBuffer.BindAttachment(AttachmentPoint.Color0, 0);
        frameBuffer.BindAttachment(AttachmentPoint.Color1, 1);
        frameBuffer.BindAttachment(AttachmentPoint.Color2, 2);

        // Where a fragment is in the world isn't in the G-buffer, it follows from where it is on screen, the shader
        // works it out with the inverse view projection of the camera block
        CameraBlock.Use(GameEngine.Instance.ActiveCamera);

        renderer.BindLighting(this);

        var ambient = renderer.ShownAmbient;
        SetUniform(UNIFORM_AMBIENT, in ambient);
        SetUniform(UNIFORM_PIXEL_SIZE, renderer.ShownLightingPixelSize);

        bool cells = renderer.HasLightCells;
        SetUniform(UNIFORM_HAS_CELLS, cells);
        if (cells)
        {
            renderer.LightCells.Light!.Bind(CELLS_UNIT);
            renderer.LightCells.Extra!.Bind(CELLS_EXTRA_UNIT);
            Vector2 origin = renderer.LightCells.Origin;
            SetUniform(UNIFORM_CELL_ORIGIN, in origin);
        }

        bool traced = renderer.Lighting == LightingMode.PathTraced && renderer.PathTracing.Result is { } found;
        int mode = traced ? renderer.ShowTracedLight ? 2 : 1 : 0;
        if (traced)
        {
            renderer.PathTracing.Result!.Bind(GI_UNIT);
            SetUniform(UNIFORM_GI_STRENGTH, MathF.Max(renderer.PathTracing.Strength, 0.0f));
            SetUniform(UNIFORM_GI_AMBIENT_SCALE, Math.Clamp(renderer.PathTracing.AmbientScale, 0.0f, 1.0f));
        }

        // The ambient occlusion, the baked maps come along in the material attachment
        var occlusion = renderer.ShownAmbientOcclusion;
        // With the strength at 0 nothing is marched and the maps (and a tile map's own geometry) still count
        bool occluding = occlusion.Enabled && (occlusion.Strength > 0.0f || occlusion.BakedStrength > 0.0f);
        SetUniform(UNIFORM_AO_ENABLED, occluding);
        if (occluding)
        {
            SetUniform(UNIFORM_AO_SAMPLES, Math.Clamp(occlusion.Samples, 1, 32));
            SetUniform(UNIFORM_AO_RADIUS, MathF.Max(occlusion.Radius, 1.0f));
            SetUniform(UNIFORM_AO_STRENGTH, Math.Clamp(occlusion.Strength, 0.0f, 1.0f));
            SetUniform(UNIFORM_AO_BAKED, Math.Clamp(occlusion.BakedStrength, 0.0f, 1.0f));
            SetUniform(UNIFORM_AO_DIRECT, Math.Clamp(occlusion.DirectStrength, 0.0f, 1.0f));
            if (occlusion.Show) mode = 3;
        }

        SetUniform(UNIFORM_LIGHTING_MODE, mode);
    }
}
