using System.Numerics;
using System.Runtime.CompilerServices;

using Horizon.Engine;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Descriptions;
using Horizon.Rendering.Lighting;

using Silk.NET.OpenGL;

using Texture = Horizon.OpenGL.Assets.Texture;

namespace Horizon.Rendering;

/// <summary>
/// Puts the G-buffer of a <see cref="DeferredRenderer2D"/> on screen, lit by its lights (shaders/renderer2d/deferred.frag).
/// </summary>
public class DeferredRenderer2DTechnique : Renderer2DTechnique
{
    private const string UNIFORM_OCCLUSION_ORIGIN = "uOcclusionOrigin";
    private const string UNIFORM_OCCLUSION_CELL_SIZE = "uOcclusionCellSize";
    private const string UNIFORM_OCCLUSION_SIZE = "uOcclusionSize";
    private const string UNIFORM_SHADOWS = "uShadows";
    private const string UNIFORM_AMBIENT = "uAmbient";
    private const string UNIFORM_PIXEL_SIZE = "uPixelSize";
    private const string UNIFORM_SHININESS = "uShininess";
    private const string UNIFORM_SPECULAR_INTENSITY = "uSpecularIntensity";

    // The G-buffer takes the first texture units, what blocks the lights the one after
    private const uint OCCLUSION_UNIT = 3;
    private const string UNIFORM_LIGHT_COUNT = "uLightCount";

    /// <summary>Must match the binding of LightBuffer in deferred.frag.</summary>
    private const uint LIGHT_BINDING = 0;

    private readonly DeferredRenderer2D renderer;
    private readonly DeferredRenderer2D.LightData[] lights = new DeferredRenderer2D.LightData[DeferredRenderer2D.MaxLights];
    private readonly BufferObject? lightBuffer;

    public DeferredRenderer2DTechnique(DeferredRenderer2D renderer)
        : base(renderer.FrameBuffer, "deferred")
    {
        this.renderer = renderer;

        if (GameEngine
                .Instance
                .ObjectManager
                .Buffers
                .TryCreate(
                    new BufferObjectDescription
                    {
                        IsStorageBuffer = true,
                        // Never mapped, the lights of a frame are simply written over those of the last
                        StorageMasks = BufferStorageMask.DynamicStorageBit,
                        Type = BufferTargetARB.ShaderStorageBuffer,
                        Size = (uint)(DeferredRenderer2D.MaxLights * Unsafe.SizeOf<DeferredRenderer2D.LightData>())
                    },
                    out var result))
        {
            lightBuffer = result.Asset;
        }
    }

    protected override void SetUniforms()
    {
        // The G-buffer takes the first three units, which is where the shader says its samplers are
        frameBuffer.BindAttachment(FramebufferAttachment.ColorAttachment0, 0);
        frameBuffer.BindAttachment(FramebufferAttachment.ColorAttachment1, 1);
        frameBuffer.BindAttachment(FramebufferAttachment.ColorAttachment2, 2);

        // Where a fragment is in the world isn't in the G-buffer, it follows from where it is on screen: the shader
        // works it out with the inverse view projection of the camera block, which is set to the camera here
        var camera = GameEngine.Instance.ActiveCamera;
        CameraBlock.Use(camera);

        // As of the frame that is drawn: between the last two ticks when it is drawn alongside the simulation
        Vector3 ambient = renderer.ShownAmbient;
        SetUniform(UNIFORM_AMBIENT, in ambient);
        SetUniform(UNIFORM_PIXEL_SIZE, renderer.ShownLightingPixelSize);
        SetUniform(UNIFORM_SHININESS, MathF.Max(renderer.ShownShininess, 1.0f));
        SetUniform(UNIFORM_SPECULAR_INTENSITY, MathF.Max(renderer.ShownSpecularIntensity, 0.0f));

        SetLights(camera);
        SetOcclusion();
    }

    private void SetLights(Camera camera)
    {
        int count = lightBuffer is null ? 0 : renderer.CollectLights(lights, camera.Bounds);

        if (count > 0)
        {
            lightBuffer!.Update<DeferredRenderer2D.LightData>(lights.AsSpan(0, count));
            BindBuffer(LIGHT_BINDING, lightBuffer);
        }

        SetUniform(UNIFORM_LIGHT_COUNT, count);
    }

    private void SetOcclusion()
    {
        var occlusion = renderer.ShownOcclusion;
        Texture? texture = renderer.ShownShadows ? occlusion?.GetTexture() : null;

        SetUniform(UNIFORM_SHADOWS, texture is not null);
        if (texture is null || occlusion is null) return;

        texture.Bind(OCCLUSION_UNIT);

        Vector2 origin = occlusion.Origin, cellSize = occlusion.CellSize, size = new(occlusion.Width, occlusion.Height);
        SetUniform(UNIFORM_OCCLUSION_ORIGIN, in origin);
        SetUniform(UNIFORM_OCCLUSION_CELL_SIZE, in cellSize);
        SetUniform(UNIFORM_OCCLUSION_SIZE, in size);
    }
}
