using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Horizon.Core.Threading;
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

    /// <summary>A light as it was at the end of a tick, and which light it is.</summary>
    private struct LightState
    {
        public Light2D Light;
        public Vector2 Position;
        public Vector3 Color;
        public float Radius, Intensity, Height, Glow, Size, Flicker;
        public bool CastsShadows, Enabled;

        public static LightState Of(Light2D light) => new()
        {
            Light = light,
            Position = light.Position,
            Color = light.Color,
            Radius = light.Radius,
            Intensity = light.Intensity,
            Height = light.Height,
            Glow = light.Glow,
            Size = light.Size,
            Flicker = light.Flicker,
            CastsShadows = light.CastsShadows,
            Enabled = light.Enabled
        };

        /// <summary>Partway from one tick to the next: where it is, how far it reaches, how bright and what colour.</summary>
        public static LightState Blend(in LightState from, in LightState to, float amount)
        {
            LightState light = to;
            light.Position = Interpolate.Linear(from.Position, to.Position, amount);
            light.Color = Interpolate.Linear(from.Color, to.Color, amount);
            light.Radius = Interpolate.Linear(from.Radius, to.Radius, amount);
            light.Intensity = Interpolate.Linear(from.Intensity, to.Intensity, amount);
            light.Glow = Interpolate.Linear(from.Glow, to.Glow, amount);
            return light;
        }
    }

    /// <summary>The lights and the settings of the lighting as of one tick, for frames drawn alongside the simulation.</summary>
    private sealed class CapturedLighting
    {
        public LightState[] Lights = new LightState[16];
        public int Count;
        public Vector3 Ambient;
        public float LightingPixelSize, Shininess, SpecularIntensity;
        public bool Shadows;
    }

    private readonly SnapshotBuffer<CapturedLighting> captured = new(static () => new CapturedLighting());

    // What the frame that is being drawn goes by, the captured lighting of its two ticks (none when drawn in turns)
    private CapturedLighting? shownBefore, shownAfter;
    private bool shownBlends;
    private float shownAlpha;

    /// <summary>The ambient light of the frame that is being drawn: as it is, or between the last two ticks.</summary>
    internal Vector3 ShownAmbient => shownAfter is null ? Ambient : shownBlends ? Interpolate.Linear(shownBefore!.Ambient, shownAfter.Ambient, shownAlpha) : shownAfter.Ambient;

    internal float ShownLightingPixelSize => shownAfter?.LightingPixelSize ?? LightingPixelSize;
    internal float ShownShininess => shownAfter?.Shininess ?? Shininess;
    internal float ShownSpecularIntensity => shownAfter?.SpecularIntensity ?? SpecularIntensity;
    internal bool ShownShadows => shownAfter?.Shadows ?? Shadows;

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

    public override void UpdateState(float dt)
    {
        // Flashes fade by the game's clock: they stand still when it does, and fade as the game goes rather than the frames
        FadeFlashes(dt);

        base.UpdateState(dt);
    }

    /// <summary>
    /// Publishes every light as it is and the settings of the lighting, for frames that are drawn alongside the
    /// simulation. Simulation thread, at the end of every tick.
    /// </summary>
    public override void Capture()
    {
        if (captured.BeginPublish() is { } into)
        {
            into.Ambient = Ambient;
            into.LightingPixelSize = LightingPixelSize;
            into.Shininess = Shininess;
            into.SpecularIntensity = SpecularIntensity;
            into.Shadows = Shadows;

            lock (lightLock)
            {
                int count = lights.Count + flashes.Count;
                if (into.Lights.Length < count)
                    into.Lights = new LightState[Math.Max(count, into.Lights.Length * 2)];

                int at = 0;
                foreach (Light2D light in lights)
                    into.Lights[at++] = LightState.Of(light);
                foreach (Flash flash in flashes)
                    into.Lights[at++] = LightState.Of(flash.Light);

                // What was there before isn't held on to by a slot that has fewer now
                Array.Clear(into.Lights, at, into.Lights.Length - at);
                into.Count = at;
            }
        }

        base.Capture();
    }

    public override void Render(float dt)
    {
        RenderFrame frame = RenderFrame.Active;
        if (frame.IsDecoupled && captured.TryGet(frame, out CapturedLighting before, out CapturedLighting after, out bool continuous))
        {
            (shownBefore, shownAfter, shownBlends, shownAlpha) = (before, after, continuous, frame.Alpha);
        }
        else
        {
            (shownBefore, shownAfter, shownBlends) = (null, null, false);
        }

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
        double time = Engine.TotalTime;

        // Drawn alongside the simulation: the lights as they were between the last two ticks, a light that is in both
        // (in the same place among the others) blended, any other as it is
        if (shownAfter is { } after)
        {
            CapturedLighting? before = shownBlends ? shownBefore : null;
            for (int i = 0; i < after.Count && count < into.Length; i++)
            {
                ref readonly LightState now = ref after.Lights[i];
                LightState light = before is not null && i < before.Count && before.Lights[i].Light == now.Light
                    ? LightState.Blend(before.Lights[i], now, shownAlpha)
                    : now;

                if (TryCollect(light, view, time, out into[count])) count++;
            }

            return count;
        }

        lock (lightLock)
        {
            foreach (var light in lights)
            {
                if (count == into.Length) return count;
                if (TryCollect(LightState.Of(light), view, time, out into[count])) count++;
            }

            foreach (var flash in flashes)
            {
                if (count == into.Length) return count;
                if (TryCollect(LightState.Of(flash.Light), view, time, out into[count])) count++;
            }
        }

        return count;
    }

    private static bool TryCollect(in LightState light, RectangleF view, double time, out LightData data)
    {
        data = default;

        if (!light.Enabled || light.Radius <= 0.0f || light.Intensity <= 0.0f) return false;

        Vector2 position = light.Position;
        float radius = light.Radius;
        float intensity = light.Intensity;

        if (light.Flicker > 0.0f)
        {
            // A few waves that never line up, started somewhere else for every light so no two waver together
            float phase = RuntimeHelpers.GetHashCode(light.Light) % 1024;
            // In double precision, single precision would have every light judder along after a few hours of play
            float waver = (float)(
                Math.Sin(time * 11.0 + phase) * 0.5 +
                Math.Sin(time * 17.3 + phase * 1.7) * 0.3 +
                Math.Sin(time * 29.1 + phase * 2.3) * 0.2);

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
