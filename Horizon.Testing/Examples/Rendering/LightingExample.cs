using System.Numerics;

using Horizon.Engine;
using Horizon.Input;
using Horizon.Graphics;
using Horizon.Physics;
using Horizon.Rendering;
using Horizon.Rendering.Lighting;
using Horizon.Rendering.Particles;
using Horizon.Rendering.Particles.Simulation;
using Horizon.Rendering.Spriting;

using Silk.NET.Input;

namespace Horizon.Testing.Examples.Rendering;

/// <summary>
/// A brick wall (with a normal map, and a specular one that has every fourth brick glazed), a few solid blocks in front
/// of it and two fountains of particles, drawn through a <see cref="DeferredRenderer2D"/>. Three coloured lights circle
/// the middle and a white one follows the mouse, the blocks cast shadows. The left fountain is lit like the wall,
/// the right one is emissive and shows regardless.
/// Left click: leave a light. Right click: flash. C: remove the lights that were left. S: toggle shadows.
/// N: toggle the normal map. G: toggle the specular map. P: toggle lighting in big pixels. A: next ambient.
/// Started without the lighting this is the test of a plain <see cref="Renderer2D"/> instead: the same wall and particles
/// drawn into a frame buffer a quarter of the size of the window, and blown up from there.
/// </summary>
public class LightingExample(bool deferred = true, bool pathTraced = false, bool showTraced = false, bool pan = false) : Scene, ITestControls
{
    // Panning, nothing moves but the camera, which drifts about in whole pixels. The lights stand still and don't
    // waver, the blob glows and stays put, the fountains are off. Anything that changes from frame to frame in this
    // is the lighting itself changing its mind, which is what it is for, see the notebook on the lanterns breathing
    private const float PanAcross = 140.0f, PanUp = 60.0f;

    private const float CellSize = 32.0f;
    private const float StatusInterval = 5.0f;
    private const float FountainRate = 400.0f;      // Particles per second, for each of the two
    private const float OrbitRadius = 230.0f;

    private static readonly Vector3[] Ambients = [new(0.18f, 0.2f, 0.28f), new(0.5f), Vector3.One, new(0.04f)];
    private static readonly Vector2 Gravity = new(0, -500);

    // Where the solid blocks are, in cells of the occlusion map (x, y, across, up) counted from the middle of the view
    private static readonly (int X, int Y, int Width, int Height)[] Blocks =
    [
        (-9, -3, 2, 6),
        (-3, 3, 6, 1),
        (6, -2, 1, 5),
        (9, 2, 3, 2),
        (-1, -6, 3, 2),
    ];

    /// <summary>
    /// The wall: a pass over the whole view (see <see cref="FullScreenPass"/>), drawn by shaders/testing/wall.
    /// </summary>
    private sealed class WallTechnique : Technique
    {
        public bool Normals { get; set; } = true;
        public bool Specular { get; set; } = true;

        public WallTechnique()
        {
            LoadShader("shaders/testing", "wall");
        }

        protected override void SetUniforms()
        {
            // Where every pixel is in the world comes out of the camera block's inverse view projection
            CameraBlock.Use(GameEngine.Instance.ActiveCamera);

            SetUniform("uNormals", Normals);
            SetUniform("uSpecular", Specular);
        }
    }

    public override Camera ActiveCamera { get; protected set; }

    // Listed on screen by the test host, all of them are about the lighting so the plain renderer has none
    public IReadOnlyList<TestControl> Controls { get; } = deferred
        ?
        [
            new("Left click", "leave a light"),
            new("Right click", "flash"),
            new("C", "remove the lights that were left"),
            new("S", "toggle shadows"),
            new("N", "toggle the normal map"),
            new("G", "toggle the specular map"),
            new("P", "toggle lighting in big pixels"),
            new("A", "next ambient"),
            new("F", "toggle the path traced lighting (the fancy one)"),
            new("B", "show what the path tracer found, on its own"),
            new("D", "the moon on (a directional light)"),
            new("T", "the swinging spot on"),
            new("O", "toggle the ambient occlusion"),
            new("V", "show the ambient occlusion on its own"),
        ]
        : [];

    private Renderer2D renderer = null!;
    private DeferredRenderer2D? lighting;

    // A sprite that blocks light the way it is drawn, pixel for pixel
    private Sprite? blob;
    private OcclusionMap2D? occlusion;
    private WallTechnique wall = null!;
    private ParticleRenderer2D dust = null!, sparks = null!;

    private Light2D? mouseLight, moon, spot;
    private readonly Light2D[] orbit = new Light2D[3];
    private readonly List<Light2D> dropped = [];

    private int ambient;
    private float time, fountainDue, statusTimer;
    private bool prevPrimary, prevSecondary;

    public override void Initialize()
    {
        // First, so it is up to date by the time anything is drawn with it
        ActiveCamera = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize) { PixelSnap = pan ? 1.0f : 0.0f });

        uint width = (uint)Engine.WindowManager.ViewportSize.X, height = (uint)Engine.WindowManager.ViewportSize.Y;

        // Either way everything below is drawn into a frame buffer first
        renderer = AddEntity(deferred
            ? lighting = new DeferredRenderer2D(width, height) { Ambient = Ambients[ambient], Lighting = pathTraced ? LightingMode.PathTraced : LightingMode.Direct, ShowTracedLight = showTraced }
            : new Renderer2D(width / 4, height / 4));

        // The renderers compile their shaders as they are constructed, so they need the GL context.
        wall = new WallTechnique();
        renderer.AddEntity(new FullScreenPass(wall));

        // A sprite that casts a shadow as sharp as its pixels (see Sprite.CastsShadows), wandering about the lights
        var casters = renderer.AddEntity(new SpriteBatch());
        if (Engine.ObjectManager.Textures.TryCreate(
                new TextureDescription { Paths = [Basics.SpritesExample.BlobSheet()], Definition = TextureDefinition.RgbaUnsignedByteNearest },
                out var blobArt))
        {
            var sheet = SpriteSheet.FromTexture(blobArt.Asset, new Vector2(Basics.SpritesExample.CELL));
            blob = casters.AddEntity(new Sprite(new Vector2(Basics.SpritesExample.CELL * 6)) { CastsShadows = true });
            if (pan)
            {
                // Glowing, so the tracer has a lamp with a shape to find
                blob.FlashColor = new Vector4(1.0f, 0.7f, 0.3f, 1.0f);
                blob.FlashAmount = 1.0f;
                blob.FlashLights = true;
                blob.Transform.Position = new Vector2(120.0f, -20.0f);
            }
            blob.ConfigureSpriteSheet(sheet, "idle");
            blob.AddAnimation("idle", Vector2.Zero, 4, 0.25f);
            casters.Add(blob);
        }

        // Lit like everything else, it only shows where there is light
        dust = renderer.AddEntity(
            new ParticleRenderer2D(8192, new ComputeParticleSimulator2D())
            {
                StartColor = new Vector3(0.8f, 0.85f, 0.9f),
                EndColor = new Vector3(0.3f, 0.32f, 0.35f),
                ParticleSize = 3.0f,
                MaxAge = 2.5f,
                Gravity = Gravity
            });

        // Its own light, it shows in the dark
        sparks = renderer.AddEntity(
            new ParticleRenderer2D(8192, new ComputeParticleSimulator2D())
            {
                StartColor = new Vector3(1.0f, 0.75f, 0.25f),
                EndColor = new Vector3(0.5f, 0.1f, 0.0f),
                ParticleSize = 3.0f,
                MaxAge = 2.5f,
                Gravity = Gravity,
                Emissive = 1.0f
            });

        BuildBlocks();
        CreateLights();

        base.Initialize();
    }

    /// <summary>
    /// Helper method to put the solid blocks into the world, for the lights to be blocked by, and for the physics
    /// world to outline (nothing else draws them).
    /// </summary>
    private void BuildBlocks()
    {
        var world = AddComponent<PhysicsWorld>();
        world.RenderDebug = true;

        // An even number of cells either way, so the middle of the view is where four of them meet: the blocks are
        // placed in whole cells from there
        Vector2 half = Engine.WindowManager.ViewportSize / 2.0f;
        int across = 2 * (int)MathF.Ceiling(half.X / CellSize), up = 2 * (int)MathF.Ceiling(half.Y / CellSize);
        Vector2 origin = -new Vector2(across, up) / 2 * CellSize;

        occlusion = new OcclusionMap2D(across, up, origin, new Vector2(CellSize));
        var body = world.CreateBody(PhysicsBodySimulationType.Static);

        foreach (var (x, y, width, height) in Blocks)
        {
            body.CreateRectangularFixture(new Vector2(x, y) * CellSize, new Vector2(width, height) * CellSize);

            for (int cellY = 0; cellY < height; cellY++)
            {
                for (int cellX = 0; cellX < width; cellX++)
                {
                    occlusion.Set((new Vector2(x + cellX, y + cellY) + new Vector2(0.5f)) * CellSize, true);
                }
            }
        }

        if (lighting is not null) lighting.Occlusion = occlusion;
    }

    private void CreateLights()
    {
        if (lighting is null) return;

        mouseLight = lighting.AddLight(new Light2D { Radius = 280.0f, Intensity = 1.2f, Size = 6.0f, Enabled = !pan });

        // A cold light from high up on the left that reaches everything, the blocks and the blob throw long shadows from it
        moon = lighting.AddLight(new Light2D
        {
            Type = LightType.Directional,
            Direction = -MathF.PI * 0.3f,
            Color = new Vector3(0.55f, 0.65f, 0.9f),
            Intensity = 0.35f,
            Height = 80.0f,
            Reach = 500.0f,
            Size = 10.0f,
            SpriteShadow = 0.8f,
            Enabled = false
        });

        // A warm spot hanging from the top of the view, swinging like a lamp on a rope
        spot = lighting.AddLight(new Light2D
        {
            Type = LightType.Spot,
            Position = new Vector2(0.0f, Engine.WindowManager.ViewportSize.Y / 2.0f - 20.0f),
            Color = new Vector3(1.0f, 0.85f, 0.6f),
            Radius = 520.0f,
            Intensity = 1.6f,
            ConeAngle = MathF.PI / 3.0f,
            ConeSoftness = 0.4f,
            Glow = 0.1f,
            Size = 8.0f,
            SpriteShadow = 0.6f,
            Enabled = false
        });

        Vector3[] colours = [new(1.0f, 0.35f, 0.25f), new(0.35f, 1.0f, 0.4f), new(0.35f, 0.5f, 1.0f)];
        for (int i = 0; i < orbit.Length; i++)
        {
            orbit[i] = lighting.AddLight(new Light2D
            {
                Color = colours[i],
                Radius = 300.0f,
                Intensity = 1.4f,
                Glow = 0.2f,
                Flicker = pan ? 0.0f : 0.2f,
                Size = 6.0f
            });
        }
    }

    public override void PostInit()
    {
        base.PostInit();

        if (lighting is null)
        {
            Console.WriteLine("LightingExample\r\n\r\n A plain Renderer2D: the wall and the particles are drawn at a quarter of the size and blown up.");
        }
        else
        {
            Console.WriteLine("LightingExample\r\n\r\n Three lights circle the middle, a fourth follows the mouse. The blocks cast shadows.");
        }

        Engine.Graphics.ClearColor = new Vector4(0.02f, 0.02f, 0.04f, 1.0f);
    }

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);

        time += dt;

        // One on either side, the same but for what they are made of
        fountainDue += FountainRate * dt;
        int due = (int)fountainDue;
        fountainDue -= due;

        float quarter = Engine.WindowManager.ViewportSize.X / 4.0f;
        float floor = -Engine.WindowManager.ViewportSize.Y / 2.0f + 40.0f;
        if (!pan)
        {
            dust.AddCone(new Vector2(-quarter, floor), Vector2.UnitY, MathF.PI / 5.0f, due, 520);
            sparks.AddCone(new Vector2(quarter, floor), Vector2.UnitY, MathF.PI / 5.0f, due, 520);
        }
        else
        {
            ActiveCamera.Position = new Vector3(MathF.Sin(time * 0.7f) * PanAcross, MathF.Cos(time * 0.5f) * PanUp, ActiveCamera.Position.Z);
        }

        if (lighting is not null)
        {
            UpdateLights();
        }

        // Something to go by when nobody is looking at the window
        statusTimer += dt;
        if (statusTimer >= StatusInterval)
        {
            statusTimer = 0.0f;
            Console.WriteLine(lighting is null
                ? $"plain renderer, frame buffer {renderer.ViewportSize.X}x{renderer.ViewportSize.Y}"
                : $"lights: {4 + dropped.Count}, shadows: {lighting.Shadows}, normals: {wall.Normals}, specular: {wall.Specular}, pixel size: {lighting.LightingPixelSize}, ambient: {lighting.Ambient}, lighting: {lighting.Lighting}");
        }
    }

    private void UpdateLights()
    {
        var keyboard = Engine.Input.Keyboard;
        var mouse = Engine.Input.Mouse;
        Vector2 mousePos = ActiveCamera.ScreenToWorld(mouse.Position);

        mouseLight!.Position = mousePos;

        if (blob is not null && !pan)
            blob.Transform.Position = new Vector2(MathF.Cos(time * 0.35f) * 260.0f, MathF.Sin(time * 0.5f) * 140.0f);

        for (int i = 0; i < orbit.Length; i++)
        {
            // Evenly spread around the circle, each a bit closer in or further out than the next
            var (sin, cos) = MathF.SinCos((pan ? 0.0f : time * 0.6f) + i * MathF.Tau / orbit.Length);
            orbit[i].Position = new Vector2(cos, sin * 0.7f) * (OrbitRadius + i * 40.0f);
        }

        if (keyboard.WasPressed(Key.S)) lighting!.Shadows = !lighting.Shadows;
        if (keyboard.WasPressed(Key.N)) wall.Normals = !wall.Normals;
        if (keyboard.WasPressed(Key.G)) wall.Specular = !wall.Specular;
        if (keyboard.WasPressed(Key.P)) lighting!.LightingPixelSize = lighting.LightingPixelSize > 0.0f ? 0.0f : 8.0f;
        if (keyboard.WasPressed(Key.A)) lighting!.Ambient = Ambients[ambient = (ambient + 1) % Ambients.Length];
        if (keyboard.WasPressed(Key.F)) lighting!.Lighting = lighting.Lighting == LightingMode.Direct ? LightingMode.PathTraced : LightingMode.Direct;
        if (keyboard.WasPressed(Key.B)) lighting!.ShowTracedLight = !lighting.ShowTracedLight;
        if (keyboard.WasPressed(Key.D) && moon is not null) moon.Enabled = !moon.Enabled;
        if (keyboard.WasPressed(Key.T) && spot is not null) spot.Enabled = !spot.Enabled;
        if (keyboard.WasPressed(Key.O)) lighting!.AmbientOcclusion.Enabled = !lighting.AmbientOcclusion.Enabled;
        if (keyboard.WasPressed(Key.V)) lighting!.AmbientOcclusion.Show = !lighting.AmbientOcclusion.Show;

        // Swinging to and fro, straight down in the middle of the swing
        if (spot is not null) spot.Direction = -MathF.PI / 2.0f + MathF.Sin(time * 1.3f) * 0.45f;

        if (keyboard.WasPressed(Key.C))
        {
            foreach (var light in dropped)
            {
                lighting!.RemoveLight(light);
            }
            dropped.Clear();
        }

        bool primary = mouse.IsDown(MouseButton.Left);
        if (primary && !prevPrimary)
        {
            dropped.Add(lighting!.AddLight(new Light2D
            {
                Position = mousePos,
                Color = new Vector3(Random.Shared.NextSingle(), Random.Shared.NextSingle(), Random.Shared.NextSingle()) * 0.7f + new Vector3(0.3f),
                Radius = 220.0f,
                Intensity = 1.3f,
                Glow = 0.15f,
                Size = 6.0f
            }));
        }
        prevPrimary = primary;

        bool secondary = mouse.IsDown(MouseButton.Right);
        if (secondary && !prevSecondary)
        {
            lighting!.AddFlash(new Light2D { Position = mousePos, Radius = 420.0f, Intensity = 3.0f, Glow = 0.3f }, 0.5f);
        }
        prevSecondary = secondary;
    }

    protected override void DisposeOther()
    {
        occlusion?.Dispose();
        base.DisposeOther();
    }
}
