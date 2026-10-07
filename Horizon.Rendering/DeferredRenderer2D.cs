using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Horizon.Engine;
using Horizon.OpenGL.Buffers;
using Horizon.OpenGL.Descriptions;
using Horizon.Rendering.Lighting;

using Silk.NET.OpenGL;

namespace Horizon.Rendering;

/// <summary>
/// Implementation of <see cref="Renderer2D"/> with deferred lighting: everything added to it is drawn unlit into a
/// G-buffer first, which is then lit in one go by every <see cref="Light2D"/> there is when it is put on screen.
/// Attachment0 holds the albedo. Attachment1 holds the surface: the normal in the RG channels (0.5 being none at all,
/// which is what anything without a normal map has) and how emissive it is in the B channel, the share of it that shows
/// no matter the light. Attachment2 holds the material: how shiny it is in the R channel (what a specular map says,
/// none for anything without one), the other channels are still free. Attachment3 holds the motion: how fast the
/// fragment is going across the screen in the RG channels (halves of the screen a second, see <c>encodeMotion</c> in
/// the shaders) and how near it is in the B channel, from 0 for the backdrop to 1 for right in front. Nothing is lit
/// by those two, they are for whatever comes after the lighting: <see cref="PostProcessing.VelocityBlurEffect"/> blurs
/// by the one and decides what blurs over what by the other.
/// Where a fragment is in the world isn't stored, that follows from where it is on screen.
/// The alpha of every attachment is how much of what was there before the fragment covers, they are all blended alike.
/// The shaders of the sprite batch, the tile map and the particles write all four, anything else that is drawn in
/// here has to as well (see shaders/renderer2d/deferred.frag for what is made of them).
/// </summary>
public class DeferredRenderer2D : Renderer2D
{
    /// <summary>The most lights a frame is lit by, any more that are in view are left out.</summary>
    public const int MaxLights = 64;

    /// <summary>
    /// A light the way the shader has it, laid out exactly like the std430 <c>Light</c> struct in deferred.frag (48 bytes).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct LightData
    {
        public Vector2 Position;
        public float Radius;
        public float Intensity;
        public Vector3 Color;
        public float Height;
        public float Glow;
        public float CastsShadows;      // 1 if it does, 0 if not
        public float Size;
        public float Padding;
    }

    // A light that is only there for a moment, fading out as it goes
    private sealed class Flash(Light2D light, float duration)
    {
        public readonly Light2D Light = light;
        public readonly float Intensity = light.Intensity;
        public readonly float Duration = duration;
        public float Age;
    }

    // Lights come and go from the logic thread while the render thread is collecting them
    private readonly Lock lightLock = new();
    private readonly List<Light2D> lights = [];
    private readonly List<Flash> flashes = [];

    /// <summary>
    /// The light there is everywhere, before any <see cref="Light2D"/>. At 1 everything looks as it was painted,
    /// the lower it is the more the lights are what there is to see by.
    /// </summary>
    public Vector3 Ambient { get; set; } = Vector3.One;

    /// <summary>
    /// What blocks the lights, null for nothing at all (no shadows). Whoever sets it is the one to dispose of it.
    /// </summary>
    public OcclusionMap2D? Occlusion { get; set; }

    /// <summary>
    /// Whether the lights cast shadows, for those that are set to and as long as there is an <see cref="Occlusion"/>.
    /// </summary>
    public bool Shadows { get; set; } = true;

    /// <summary>
    /// The size (in world units) of the squares the lighting is worked out for, 0 for every pixel of the screen by itself.
    /// Pixel art is usually drawn a few times its size, lit per pixel of the screen the light would be finer than
    /// what it falls on. Set to the size of a pixel of the art (1, for art drawn at a unit per pixel) every one of its
    /// pixels is lit as a whole.
    /// </summary>
    public float LightingPixelSize { get; set; } = 0.0f;

    /// <summary>
    /// How tight the highlights on what is shiny are: the higher this is the smaller and sharper they get,
    /// low values spread a dull sheen over a wide area.
    /// </summary>
    public float Shininess { get; set; } = 24.0f;

    /// <summary>
    /// How bright the highlights on what is shiny are, on top of what the specular map itself says. 0 for none at all.
    /// </summary>
    public float SpecularIntensity { get; set; } = 1.0f;

    protected override FrameBufferObject CreateFrameBuffer(in uint width, in uint height) =>
        CreateFrameBuffer(
            new FrameBufferObjectDescription
            {
                Width = width,
                Height = height,
                Attachments = new() {
                    { FramebufferAttachment.ColorAttachment0, FrameBufferAttachmentDefinition.TextureRGBAByteNearest },
                    { FramebufferAttachment.ColorAttachment1, FrameBufferAttachmentDefinition.TextureRGBAByteNearest },
                    { FramebufferAttachment.ColorAttachment2, FrameBufferAttachmentDefinition.TextureRGBAByteNearest },
                    { FramebufferAttachment.ColorAttachment3, FrameBufferAttachmentDefinition.TextureRGBAByteNearest },

                    // Sprite batches cut their sprites out with the stencil
                    { FramebufferAttachment.DepthStencilAttachment, FrameBufferAttachmentDefinition.DepthStencilComponent },
                }
            });

    protected override Renderer2DTechnique CreateTechnique() => new DeferredRenderer2DTechnique(this);

    // What is in the frame buffer is what there is to light, the picture is what the lighting makes of it
    protected internal override bool HoldsPicture => false;

    /// <inheritdoc/>
    public override Horizon.OpenGL.Assets.Texture? MotionTexture =>
        FrameBuffer?.Attachments[FramebufferAttachment.ColorAttachment3].Texture;

    public DeferredRenderer2D(in uint width, in uint height)
        : base(width, height) { }

    /// <summary>
    /// Adds a light, which stays until it is removed. Can be called from any thread.
    /// </summary>
    /// <returns>The light that was handed over, to change or remove later.</returns>
    public Light2D AddLight(Light2D light)
    {
        lock (lightLock)
        {
            if (!lights.Contains(light)) lights.Add(light);
        }

        return light;
    }

    public bool RemoveLight(Light2D light)
    {
        lock (lightLock)
            return lights.Remove(light);
    }

    /// <summary>
    /// Removes every light, the ones that are still fading out included.
    /// </summary>
    public void ClearLights()
    {
        lock (lightLock)
        {
            lights.Clear();
            flashes.Clear();
        }
    }

    /// <summary>
    /// Adds a light that is only there for a moment (the flash of a hit, an explosion): it starts as bright as it is
    /// handed over and fades to nothing over the duration, after which it is gone. Can be called from any thread.
    /// </summary>
    /// <param name="duration">How long the light lasts, in seconds.</param>
    public void AddFlash(Light2D light, float duration)
    {
        if (duration <= 0.0f) return;

        lock (lightLock)
            flashes.Add(new Flash(light, duration));
    }

    public override void Render(float dt)
    {
        FadeFlashes(dt);

        base.Render(dt);
    }

    private void FadeFlashes(float dt)
    {
        lock (lightLock)
        {
            for (int i = flashes.Count - 1; i >= 0; i--)
            {
                var flash = flashes[i];
                flash.Age += dt;

                if (flash.Age >= flash.Duration)
                {
                    flashes.RemoveAt(i);
                    continue;
                }

                // Quick to drop off at first, then lingering: that is how a flash looks
                float left = 1.0f - flash.Age / flash.Duration;
                flash.Light.Intensity = flash.Intensity * left * left;
            }
        }
    }

    /// <summary>
    /// Fills in the lights that reach into what a camera sees, as many as fit.
    /// </summary>
    /// <param name="view">What of the world is on screen.</param>
    /// <returns>How many lights were filled in.</returns>
    internal int CollectLights(Span<LightData> into, RectangleF view)
    {
        int count = 0;
        float time = Engine.TotalTime;

        lock (lightLock)
        {
            foreach (var light in lights)
            {
                if (count == into.Length) return count;
                if (TryCollect(light, view, time, out into[count])) count++;
            }

            foreach (var flash in flashes)
            {
                if (count == into.Length) return count;
                if (TryCollect(flash.Light, view, time, out into[count])) count++;
            }
        }

        return count;
    }

    private static bool TryCollect(Light2D light, RectangleF view, float time, out LightData data)
    {
        data = default;

        if (!light.Enabled || light.Radius <= 0.0f || light.Intensity <= 0.0f) return false;

        Vector2 position = light.Position;
        float radius = light.Radius;
        float intensity = light.Intensity;

        if (light.Flicker > 0.0f)
        {
            // A few waves that never line up, started somewhere else for every light so no two waver together
            float phase = RuntimeHelpers.GetHashCode(light) % 1024;
            float waver =
                MathF.Sin(time * 11.0f + phase) * 0.5f +
                MathF.Sin(time * 17.3f + phase * 1.7f) * 0.3f +
                MathF.Sin(time * 29.1f + phase * 2.3f) * 0.2f;

            intensity *= 1.0f + waver * light.Flicker * 0.5f;
            radius *= 1.0f + waver * light.Flicker * 0.1f;
        }

        // A light that doesn't reach into the view has nothing to light, every pixel would test it for nothing
        if (position.X + radius < view.Left || position.X - radius > view.Right ||
            position.Y + radius < view.Top || position.Y - radius > view.Bottom) return false;

        data = new LightData
        {
            Position = position,
            Radius = radius,
            Intensity = intensity,
            Color = light.Color,
            Height = light.Height,
            Glow = light.Glow,
            CastsShadows = light.CastsShadows ? 1.0f : 0.0f,
            Size = light.Size
        };
        return true;
    }

    protected override void Clear()
    {
        Clear(0, ClearColor);

        // No normal, not emissive, and nothing covering the pixel yet
        Clear(1, new Vector4(0.5f, 0.5f, 0.0f, 0.0f));

        // Not shiny either
        Clear(2, Vector4.Zero);

        // Standing still (which is 128 out of 255, see encodeMotion in the shaders) and as far away as it gets
        Clear(3, new Vector4(128.0f / 255.0f, 128.0f / 255.0f, 0.0f, 0.0f));
        ClearDepthStencil();
    }
}
