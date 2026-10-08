using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Horizon.Core.Threading;
using Horizon.Engine;
using Horizon.Graphics;
using Horizon.Rendering.Lighting;

namespace Horizon.Rendering;

/// <summary>
/// Implementation of <see cref="Renderer2D"/> with deferred lighting: everything added to it is drawn unlit into a
/// G-buffer first, which is then lit in one go by every <see cref="Light2D"/> there is when it is put on screen.
/// Attachment0 holds the albedo. Attachment1 holds the surface: the normal in the RG channels (0.5 being none at all,
/// which is what anything without a normal map has) and how emissive it is in the B channel, the share of it that shows
/// no matter the light. Attachment2 holds the material: how shiny it is in the R channel (what a specular map says,
/// none for anything without one), how much of the pixel blocks light in the G channel and how much of it lights what
/// is round it in the B channel, see gbuffer.slang.
/// Where a fragment is in the world isn't stored, that follows from where it is on screen.
/// The alpha of every attachment is how much of what was there before the fragment covers, they are all blended alike.
/// The shaders of the sprite batch, the tile map and the particles write all three, anything else that is drawn in
/// here has to as well (see shaders/renderer2d/deferred.frag for what is made of them).
/// </summary>
public class DeferredRenderer2D : Renderer2D
{
    /// <summary>The most lights a frame is lit by, any more that are in view are left out.</summary>
    public const int MaxLights = 64;

    /// <summary>
    /// A light the way the shader has it, laid out exactly like the std430 <c>Light</c> struct in lighting/direct.slang (80 bytes).
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
        public float SpriteShadow;
        public Vector2 Direction;       // a unit vector, which way a spot shines or a directional light's rays go
        public float CosInner;          // a spot is full inside of this, gone past CosOuter, below -1 for a light that shines every way
        public float CosOuter;
        public float Type;              // LightType as a float
        public float Reach;
        public Vector2 Padding;
    }

    // A light that is only there for a moment, fading out as it goes
    private sealed class Flash(Light2D light, float duration)
    {
        public readonly Light2D Light = light;
        public readonly float Intensity = light.Intensity;
        public readonly float Duration = duration;
        public float Age;
    }

    // Lights come and go from the simulation thread while the render thread is collecting them
    private readonly Lock lightLock = new();
    private readonly List<Light2D> lights = [];
    private readonly List<Flash> flashes = [];

    /// <summary>A light as it was at the end of a tick, and which light it is.</summary>
    private struct LightState
    {
        public Light2D Light;
        public LightType Type;
        public Vector2 Position;
        public Vector3 Color;
        public float Radius, Intensity, Height, Glow, Size, Flicker, Direction, ConeAngle, ConeSoftness, Reach, SpriteShadow;
        public bool CastsShadows, Enabled;

        public static LightState Of(Light2D light) => new()
        {
            Light = light,
            Type = light.Type,
            Position = light.Position,
            Color = light.Color,
            Radius = light.Radius,
            Intensity = light.Intensity,
            Height = light.Height,
            Glow = light.Glow,
            Size = light.Size,
            Flicker = light.Flicker,
            Direction = light.Direction,
            ConeAngle = light.ConeAngle,
            ConeSoftness = light.ConeSoftness,
            Reach = light.Reach,
            SpriteShadow = light.SpriteShadow,
            CastsShadows = light.CastsShadows,
            Enabled = light.Enabled
        };

        /// <summary>Partway from one tick to the next: where it is, which way it points, how far it reaches, how bright and what colour.</summary>
        public static LightState Blend(in LightState from, in LightState to, float amount)
        {
            LightState light = to;
            light.Position = Interpolate.Linear(from.Position, to.Position, amount);
            light.Color = Interpolate.Linear(from.Color, to.Color, amount);
            light.Radius = Interpolate.Linear(from.Radius, to.Radius, amount);
            light.Intensity = Interpolate.Linear(from.Intensity, to.Intensity, amount);
            light.Glow = Interpolate.Linear(from.Glow, to.Glow, amount);

            // The short way round, a light swinging through the seam of the circle mustn't whip round the long way
            float turn = MathF.IEEERemainder(to.Direction - from.Direction, MathF.Tau);
            light.Direction = from.Direction + turn * amount;
            return light;
        }
    }

    /// <summary>The lights and the settings of the lighting as of one tick, for frames drawn alongside the simulation.</summary>
    private sealed class CapturedLighting
    {
        public LightState[] Lights = new LightState[16];
        public int Count;
        public Vector3 Ambient;
        public float LightingPixelSize, Shininess, SpecularIntensity, ShadowSoftness;
        public bool Shadows, SpriteShadows;
        public OcclusionMap2D? Occlusion;
    }

    private readonly SnapshotBuffer<CapturedLighting> captured = new(static () => new CapturedLighting());

    // What the frame that is being drawn goes by, the captured lighting of its two ticks (none with the simulation standing still)
    private CapturedLighting? shownBefore, shownAfter;
    private bool shownBlends;
    private float shownAlpha;

    /// <summary>The ambient light of the frame that is being drawn: as it is, or between the last two ticks.</summary>
    internal Vector3 ShownAmbient => shownAfter is null ? Ambient : shownBlends ? Interpolate.Linear(shownBefore!.Ambient, shownAfter.Ambient, shownAlpha) : shownAfter.Ambient;

    internal float ShownLightingPixelSize => shownAfter?.LightingPixelSize ?? LightingPixelSize;
    internal float ShownShininess => shownAfter?.Shininess ?? Shininess;
    internal float ShownSpecularIntensity => shownAfter?.SpecularIntensity ?? SpecularIntensity;
    internal float ShownShadowSoftness => shownAfter?.ShadowSoftness ?? ShadowSoftness;
    internal bool ShownShadows => shownAfter?.Shadows ?? Shadows;
    internal bool ShownSpriteShadows => shownAfter?.SpriteShadows ?? SpriteShadows;
    internal OcclusionMap2D? ShownOcclusion => shownAfter is null ? Occlusion : shownAfter.Occlusion;

    /// <summary>
    /// The light there is everywhere, before any <see cref="Light2D"/>. At 1 everything looks as it was painted,
    /// the lower it is the more the lights are what there is to see by.
    /// </summary>
    public Vector3 Ambient { get; set; } = Vector3.One;

    /// <summary>
    /// How the picture is lit, see <see cref="LightingMode"/>. Direct unless asked, path traced is the fancy one.
    /// From any thread, it takes at the next frame.
    /// </summary>
    public LightingMode Lighting { get; set; } = LightingMode.Direct;

    /// <summary>The settings of the path traced lighting, used when <see cref="Lighting"/> says so.</summary>
    public PathTracedLighting2D PathTracing { get; } = new();

    /// <summary>Shows what the tracer found instead of the picture, for seeing what it is up to. Only with the path tracing on.</summary>
    public bool ShowTracedLight { get; set; }

    /* The lights as the shaders get them, uploaded once a frame for every pass that lights */

    // Must match the bindings of lighting/direct.slang
    private const uint LIGHT_BINDING = 2;
    private const uint FIELD_UNIT = 3;
    private const uint SPRITE_FIELD_UNIT = 5;

    // The shadows of the sprites, a distance field of what they drew, made anew every frame there is one to make
    private readonly SpriteShadows spriteShadows = new();
    private Texture? spriteField;
    private float spritePixelWorld;
    private int spriteCasters;

    private readonly LightData[] lightData = new LightData[MaxLights];
    private GpuBuffer? lightBuffer;
    private int uploadedLights;
    private bool lightsUploaded;

    // The lights sorted into the tiles of the screen, once a frame, for every pass that lights
    private readonly LightTiles tiles = new();

    // The lighting worked out once per lighting pixel, for the deferred pass to read, see LightingPixelSize.
    // HORIZON_LIGHT_CELLS=off has the deferred pass light every pixel itself instead, for comparing the two
    internal readonly LightCells LightCells = new();
    internal bool HasLightCells { get; private set; }
    private static readonly bool cellsOff = string.Equals(Environment.GetEnvironmentVariable("HORIZON_LIGHT_CELLS"), "off", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What blocks the lights, null for nothing at all (no shadows). Whoever sets it is the one to dispose of it.
    /// </summary>
    public OcclusionMap2D? Occlusion { get; set; }

    /// <summary>
    /// Whether the lights cast shadows, for those that are set to, from the <see cref="Occlusion"/> and from the
    /// sprites that block light (see <see cref="SpriteShadows"/>).
    /// </summary>
    public bool Shadows { get; set; } = true;

    /// <summary>
    /// Whether the sprites that say they block light (<see cref="Spriting.Sprite.CastsShadows"/>) cast shadows as
    /// sharp as their pixels and bounce light. What they drew is turned into a distance field of the picture every
    /// frame, a dozen passes over it, see <see cref="Lighting.SpriteShadows"/>.
    /// </summary>
    public bool SpriteShadows { get; set; } = true;

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

    /// <summary>
    /// How soft the edges of every shadow are, in world units. It is as if every light were this much wider than its
    /// <see cref="Light2D.Size"/> says as far as its shadows go, so what is only hidden from a part of the light is
    /// only partly in shadow, more so the further the shadow falls from what casts it. 0 for shadows as the lights
    /// say, hard from a light without a size.
    /// </summary>
    public float ShadowSoftness { get; set; } = 12.0f;

    protected override RenderTarget CreateFrameBuffer(in uint width, in uint height) =>
        CreateFrameBuffer(
            new RenderTargetDescription
            {
                Width = width,
                Height = height,
                Attachments = new()
                {
                    { AttachmentPoint.Color0, TextureDefinition.RgbaUnsignedByteNearest },
                    { AttachmentPoint.Color1, TextureDefinition.RgbaUnsignedByteNearest },
                    { AttachmentPoint.Color2, TextureDefinition.RgbaUnsignedByteNearest },

                    // Sprite batches cut their sprites out with the stencil
                    { AttachmentPoint.DepthStencil, TextureDefinition.DepthStencil },
                }
            });

    protected override Renderer2DTechnique CreateTechnique() => new DeferredRenderer2DTechnique(this);

    // What is in the frame buffer is what there is to light, the picture is what the lighting makes of it
    protected internal override bool HoldsPicture => false;

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
            into.ShadowSoftness = ShadowSoftness;
            into.Shadows = Shadows;
            into.SpriteShadows = SpriteShadows;
            into.Occlusion = Occlusion;

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

        Occlusion?.Capture();
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

        lightsUploaded = false;
        spriteCasters = 0;
        base.Render(dt);
    }

    /// <summary>
    /// A sprite that blocks light was drawn into this frame, which is what makes the field of sprite shadows worth
    /// building. The sprite batches say so, see <see cref="Spriting.SpriteBatch"/>. Render thread.
    /// </summary>
    internal void NoteSpriteCasters() => spriteCasters++;

    /// <summary>
    /// The lights of this frame go to the GPU, and the tracer runs if it is on. The deferred pass that follows reads both.
    /// </summary>
    protected override void BeforeResolve(float dt)
    {
        Camera camera = Engine.ActiveCamera;
        var device = GraphicsDevice.Current;
        UploadLights(camera);

        bool traced = Lighting == LightingMode.PathTraced && device.Supports(GraphicsFeature.PathTracedLighting);

        // The shadows of the sprites, out of what they drew this frame. Only when there is something to make them
        // of and a light to throw them, a frame without a caster in it (or without a light) has no field to build.
        // The tracer wants it whatever, it is what tells its rays where the lamps and the glowing things are
        spritePixelWorld = camera.Bounds.Width / MathF.Max(ViewportSize.X, 1.0f);
        bool wantsField = ShownShadows && ShownSpriteShadows && (traced || (spriteCasters > 0 && uploadedLights > 0));
        if (wantsField)
        {
            using var shadows = device.BeginGpuScope("sprite shadows");
            spriteField = spriteShadows.Build(FrameBuffer.TextureOf(AttachmentPoint.Color2), FrameBuffer.TextureOf(AttachmentPoint.Color1), FrameBuffer.Width, FrameBuffer.Height, spritePixelWorld);
        }
        else
        {
            spriteField = null;
        }

        // Which lights reach which tile of the screen, for the deferred pass and the tracer alike. No lights, no tiles
        if (uploadedLights > 0)
        {
            using var sorting = device.BeginGpuScope("light tiles");
            CameraBlock.Use(camera);
            tiles.Build(this, camera, ViewportSize);
        }

        // Art lit a pixel of the art at a time is lit once per pixel of the art rather than once per pixel of the
        // screen, see LightCells. With no lights there is nothing to work out, the deferred pass sees to that quickly
        HasLightCells = false;
        if (uploadedLights > 0 && ShownLightingPixelSize > 0.0f && !cellsOff)
        {
            using var lighting = device.BeginGpuScope("light cells");
            CameraBlock.Use(camera);
            HasLightCells = LightCells.Build(this, camera, ShownLightingPixelSize);
        }

        if (Lighting != LightingMode.PathTraced) return;

        if (!device.Supports(GraphicsFeature.PathTracedLighting))
        {
            device.WarnUnsupported(GraphicsFeature.PathTracedLighting, "The lighting is direct instead.");
            Lighting = LightingMode.Direct;
            return;
        }

        using (device.BeginGpuScope("path tracing"))
            PathTracing.Run(this, camera, dt);
    }

    /// <summary>
    /// Helper method to collect the lights that are in view and hand them to the GPU, once a frame.
    /// </summary>
    private void UploadLights(Camera camera)
    {
        if (lightsUploaded) return;
        lightsUploaded = true;

        // Never mapped, the lights of a frame are simply written over those of the last
        lightBuffer ??= GpuBuffer.Create(new BufferDescription(BufferUsage.Storage, BufferAccess.Dynamic, (nuint)(MaxLights * Unsafe.SizeOf<LightData>())));
        lightBuffer.Name = "lights";

        uploadedLights = CollectLights(lightData, camera.Bounds);
        if (uploadedLights > 0)
            lightBuffer.Update<LightData>(lightData.AsSpan(0, uploadedLights));
    }

    /// <summary>
    /// Sets everything a technique that includes shaders/lighting/direct.slang needs, with the technique bound, the
    /// lights of the frame and their tiles, what blocks them and how shiny things are. Render thread, in or after <see cref="BeforeResolve"/>.
    /// </summary>
    internal void BindLighting(Technique technique)
    {
        UploadLights(Engine.ActiveCamera);

        if (lightBuffer is not null && uploadedLights > 0)
            technique.BindBuffer(LIGHT_BINDING, lightBuffer);

        if (tiles.Buffer is { } tileBuffer)
            technique.BindBuffer(LightTiles.TILES_BINDING, tileBuffer);

        technique.SetUniform("uLightCount", uploadedLights);
        technique.SetUniform("uShininess", MathF.Max(ShownShininess, 1.0f));
        technique.SetUniform("uSpecularIntensity", MathF.Max(ShownSpecularIntensity, 0.0f));
        technique.SetUniform("uShadowSoftness", MathF.Max(ShownShadowSoftness, 0.0f));
        technique.SetUniform("uTileCounts", tiles.Counts);
        technique.SetUniform("uTileScreen", tiles.Screen);

        OcclusionMap2D? occlusion = ShownOcclusion;
        Texture? field = occlusion?.GetField();

        technique.SetUniform("uShadows", ShownShadows && (field is not null || spriteField is not null));
        technique.SetUniform("uHasSprites", spriteField is not null);
        technique.SetUniform("uSpritePixelWorld", spritePixelWorld);
        spriteField?.Bind(SPRITE_FIELD_UNIT);

        technique.SetUniform("uHasField", field is not null);
        if (field is null || occlusion is null) return;

        field.Bind(FIELD_UNIT);

        Vector2 origin = occlusion.Origin, texel = occlusion.FieldTexelSize, size = occlusion.FieldSize;
        technique.SetUniform("uFieldOrigin", in origin);
        technique.SetUniform("uFieldCellSize", in texel);
        technique.SetUniform("uFieldSize", in size);
    }

    protected override void DisposeOther()
    {
        PathTracing.Dispose();
        tiles.Dispose();
        LightCells.Dispose();
        spriteShadows.Dispose();
        lightBuffer?.Dispose();
        lightBuffer = null;
        base.DisposeOther();
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

        if (!light.Enabled || light.Intensity <= 0.0f || (light.Radius <= 0.0f && light.Type != LightType.Directional)) return false;

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

        // A light that doesn't reach into the view has nothing to light, every pixel would test it for nothing.
        // A directional light is everywhere
        if (light.Type != LightType.Directional && (
            position.X + radius < view.Left || position.X - radius > view.Right ||
            position.Y + radius < view.Top || position.Y - radius > view.Bottom)) return false;

        // A spot is full within its inner angle and fades to nothing at the outer, which is the edge of the cone
        float outer = Math.Clamp(light.ConeAngle, 0.0f, MathF.Tau) / 2.0f;
        float inner = outer * (1.0f - Math.Clamp(light.ConeSoftness, 0.01f, 1.0f));   // never quite hard, the shader fades between the two
        var (sin, cos) = MathF.SinCos(light.Direction);

        data = new LightData
        {
            Position = position,
            Radius = radius,
            Intensity = intensity,
            Color = light.Color,
            Height = light.Height,
            Glow = light.Glow,
            CastsShadows = light.CastsShadows ? 1.0f : 0.0f,
            Size = light.Size,
            SpriteShadow = Math.Clamp(light.SpriteShadow, 0.0f, 1.0f),
            Direction = new Vector2(cos, sin),
            CosInner = light.Type == LightType.Spot ? MathF.Cos(inner) : -2.0f,
            CosOuter = light.Type == LightType.Spot ? MathF.Cos(outer) : -2.0f,
            Type = (float)light.Type,
            Reach = MathF.Max(light.Reach, 1.0f)
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
        ClearDepthStencil();
    }
}
