using System.Numerics;

using Horizon.Engine;
using Horizon.Rendering;
using Horizon.Rendering.Lighting;
using Horizon.Rendering.Particles;
using Horizon.Rendering.Particles.Simulation;
using Horizon.Rendering.Spriting;
using Horizon.Rendering.Tiling;
using Horizon.UI;
using Horizon.UI.Components;

using Silk.NET.Input;

namespace Horizon.Testing.Examples.Lighting;

/// <summary>
/// A brick cellar, lit. The room is a map (Assets/examples/world/cellar.tmx), its bricks come with a normal map,
/// a specular map that has some of them glazed and an occlusion map, its pillars and ledges block light, and its
/// lamps are objects the map puts down. A white light follows the mouse, a blob wanders about throwing a shadow as
/// sharp as its pixels, and two fountains of particles show what is lit and what glows by itself.
/// Started with the path tracing on it is the fancy lighting's example instead, the same room with light bouncing
/// off the bricks and spilling round the pillars.
/// <para>
/// What to look at. <see cref="DeferredRenderer2D"/> and everything added to it being lit, <see cref="Light2D"/>
/// with its three kinds (<see cref="LightType"/>), <see cref="DeferredRenderer2D.Occlusion"/> made by
/// <see cref="TileMap.CreateOcclusion"/> out of the layers that say they cast shadows,
/// <see cref="Sprite.CastsShadows"/>, <see cref="DeferredRenderer2D.AmbientOcclusion"/> and
/// <see cref="TileMap.GeometryOcclusion"/> for the corners going dark, <see cref="DeferredRenderer2D.LightingPixelSize"/>
/// for lighting pixel art a pixel of the art at a time, and <see cref="DeferredRenderer2D.Lighting"/> with
/// <see cref="LightingMode.PathTraced"/>. The tile set's maps are the files next to its picture, called the same
/// with _normal, _specular and _ao on the end, nothing has to be said for them to be found.
/// </para>
/// <para>
/// In your own game.
/// <code>
/// // Initialize, render thread
/// lighting = AddEntity(new DeferredRenderer2D(width, height) { Ambient = new Vector3(0.18f, 0.2f, 0.28f) });
/// map = lighting.AddEntity(TileMap.Load("Assets/maps/cellar.tmx"));
/// lighting.Occlusion = map.CreateOcclusion();              // what blocks light, by the shape of the tiles
/// lighting.AmbientOcclusion.Enabled = true;
///
/// lamp = lighting.AddLight(new Light2D { Position = where, Radius = 200, Color = warm, Flicker = 0.1f });
///
/// // UpdateState, simulation thread, a light is moved by setting where it is
/// lamp.Position = player.Position;
/// lighting.AddFlash(new Light2D { Position = hit, Radius = 300, Intensity = 3 }, 0.4f);
/// </code>
/// </para>
/// </summary>
public class LightingExample(bool pathTraced = false) : Scene, ITestControls
{
    // How many pixels of the screen a pixel of the art is
    private const float PIXEL = 2.0f;

    private const float FOUNTAIN_RATE = 300.0f;     // particles a second, for each of the two
    private const float BLOB_SCALE = 3.0f;

    private static readonly Vector2 Gravity = new(0, -260);

    public override Camera ActiveCamera { get; protected set; } = null!;

    // Listed on screen by the test host
    public IReadOnlyList<TestControl> Controls { get; } =
    [
        new("Mouse", "a light follows it"),
        new("Left click", "leave a light"),
        new("Right click", "a flash"),
        new("C", "take the lights that were left away"),
        new("F", "path traced lighting on and off"),
        new("B", "what the path tracer found, on its own"),
        new("S", "shadows"),
        new("P", "light a pixel of the art at a time"),
        new("O", "ambient occlusion"),
        new("V", "the occlusion on its own"),
        new("M", "the moon, a directional light"),
        new("T", "the spot on its rope"),
        new("G", "the blob glows, and lights things when traced"),
    ];

    private DeferredRenderer2D _lighting = null!;
    private TileMap _map = null!;
    private OcclusionMap2D? _occlusion;
    private Sprite _blob = null!;
    private ParticleRenderer2D _dust = null!, _sparks = null!;
    private Label _status = null!;

    private Light2D _mouseLight = null!;
    private Light2D? _moon, _spot;
    private readonly List<Light2D> _dropped = [];

    private Vector2 _centre;
    private float _time, _fountainDue;
    private bool _glowing;

    public override void Initialize()
    {
        Vector2 window = Engine.WindowManager.ViewportSize;

        // First, so it is up to date by the time anything is drawn with it
        var camera = AddEntity(new Camera2D(window / PIXEL));
        ActiveCamera = camera;

        _map = TileMap.Load(World.CELLAR);
        _centre = _map.Min + _map.Size / 2.0f;
        camera.Position = new Vector3(_centre, 0.0f);

        // Everything added to a deferred renderer is drawn unlit into its G-buffer (what it looks like, which way
        // it faces, how shiny it is), and then the lights are worked out for the whole picture in one go
        _lighting = AddEntity(new DeferredRenderer2D((uint)window.X, (uint)window.Y)
        {
            FollowWindow = true,
            Ambient = World.AmbientOf(_map),
            Lighting = pathTraced ? LightingMode.PathTraced : LightingMode.Direct,
            ClearColor = new Vector4(0.02f, 0.02f, 0.04f, 1.0f)
        });

        _lighting.AddEntity(_map);

        // What blocks light. The map knows, its "solid" layer has CastsShadows on it, and the occlusion map is
        // made from the shape of those tiles and not the squares they sit in, a pillar is as thin as it is drawn
        _lighting.Occlusion = _occlusion = _map.CreateOcclusion();

        // The corners going dark. The marched kind looks at what stands where every frame, the map's own kind is
        // worked out once from its geometry and costs nothing, and the tile set's _ao map is what the artist
        // (a script, here) painted into the mortar
        _lighting.AmbientOcclusion.Enabled = true;
        _map.GeometryOcclusion = true;

        // A sprite that casts a shadow as sharp as its pixels, wandering about between the lamps
        var sprites = _lighting.AddEntity(new SpriteBatch());
        _blob = World.CreateBlob(sprites, BLOB_SCALE);
        _blob.CastsShadows = true;

        // Lit like everything else, it only shows where there is light
        _dust = _lighting.AddEntity(new ParticleRenderer2D(4096, new ComputeParticleSimulator2D())
        {
            StartColor = new Vector3(0.8f, 0.85f, 0.9f),
            EndColor = new Vector3(0.3f, 0.32f, 0.35f),
            ParticleSize = 1.5f,
            MaxAge = 2.2f,
            Gravity = Gravity
        });

        // Its own light, it shows in the dark (and under the path tracer it lights what is round it)
        _sparks = _lighting.AddEntity(new ParticleRenderer2D(4096, new ComputeParticleSimulator2D())
        {
            StartColor = new Vector3(1.0f, 0.75f, 0.25f),
            EndColor = new Vector3(0.5f, 0.1f, 0.0f),
            ParticleSize = 1.5f,
            MaxAge = 2.2f,
            Gravity = Gravity,
            Emissive = 1.0f
        });

        // The lamps the map has in it, see World.AddLights for how an object becomes a light
        var lamps = World.AddLights(_map, _lighting);
        _spot = lamps.GetValueOrDefault("hanging");

        _mouseLight = _lighting.AddLight(new Light2D { Radius = 170.0f, Intensity = 1.1f, Size = 4.0f });

        // A cold light from high up on the left that reaches everything, the same everywhere
        _moon = _lighting.AddLight(new Light2D
        {
            Type = LightType.Directional,
            Direction = -MathF.PI * 0.3f,
            Color = new Vector3(0.55f, 0.65f, 0.9f),
            Intensity = 0.35f,
            Height = 80.0f,
            Reach = 300.0f,
            Size = 8.0f,
            SpriteShadow = 0.8f,
            Enabled = false
        });

        var ui = AddComponent(UICompositor.ForScreen());
        _status = ui.CreateModule().AddComponent(new Label { Anchor = Origin.Top, Position = new Vector2(0, -28), TextScale = 0.26f });

        base.Initialize();
    }

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);

        _time += dt;

        var keyboard = Engine.Input.Keyboard;
        var mouse = Engine.Input.Mouse;
        Vector2 pointer = ActiveCamera.ScreenToWorld(mouse.Position);

        // A light is moved by saying where it is, every tick if need be, there is nothing else to it
        _mouseLight.Position = pointer;
        if (_spot is not null) _spot.Direction = -MathF.PI / 2.0f + MathF.Sin(_time * 1.3f) * 0.5f;

        // Round and round through the room, its feet on nothing in particular
        _blob.Transform.Position = _centre + new Vector2(MathF.Cos(_time * 0.35f) * 230.0f, MathF.Sin(_time * 0.5f) * 110.0f - 30.0f);

        // One fountain on either side of the floor, the same but for what they are made of
        _fountainDue += FOUNTAIN_RATE * dt;
        int due = (int)_fountainDue;
        _fountainDue -= due;

        float floor = _map.Min.Y + 2.0f * _map.TileSize.Y + 2.0f;
        _dust.AddCone(new Vector2(_centre.X - 120.0f, floor), Vector2.UnitY, MathF.PI / 6.0f, due, 240);
        _sparks.AddCone(new Vector2(_centre.X + 190.0f, floor), Vector2.UnitY, MathF.PI / 6.0f, due, 240);

        if (keyboard.WasPressed(Key.F)) _lighting.Lighting = _lighting.Lighting == LightingMode.Direct ? LightingMode.PathTraced : LightingMode.Direct;
        if (keyboard.WasPressed(Key.B)) _lighting.ShowTracedLight = !_lighting.ShowTracedLight;
        if (keyboard.WasPressed(Key.S)) _lighting.Shadows = !_lighting.Shadows;
        if (keyboard.WasPressed(Key.P)) _lighting.LightingPixelSize = _lighting.LightingPixelSize > 0.0f ? 0.0f : 1.0f;
        if (keyboard.WasPressed(Key.O)) _lighting.AmbientOcclusion.Enabled = !_lighting.AmbientOcclusion.Enabled;
        if (keyboard.WasPressed(Key.V)) _lighting.AmbientOcclusion.Show = !_lighting.AmbientOcclusion.Show;
        if (keyboard.WasPressed(Key.M) && _moon is not null) _moon.Enabled = !_moon.Enabled;
        if (keyboard.WasPressed(Key.T) && _spot is not null) _spot.Enabled = !_spot.Enabled;

        if (keyboard.WasPressed(Key.G))
        {
            // A flash that stays. FlashLights is what makes it a lamp to the path tracer and not just bright
            _glowing = !_glowing;
            _blob.FlashColor = new Vector4(1.0f, 0.7f, 0.3f, 1.0f);
            _blob.FlashAmount = _glowing ? 1.0f : 0.0f;
            _blob.FlashLights = _glowing;
        }

        if (mouse.WasPressed(MouseButton.Left))
        {
            _dropped.Add(_lighting.AddLight(new Light2D
            {
                Position = pointer,
                Color = new Vector3(Random.Shared.NextSingle(), Random.Shared.NextSingle(), Random.Shared.NextSingle()) * 0.7f + new Vector3(0.3f),
                Radius = 150.0f,
                Intensity = 1.3f,
                Glow = 0.15f,
                Size = 4.0f
            }));
        }

        // A light that is there for half a second and takes itself away again, a muzzle flash, a hit
        if (mouse.WasPressed(MouseButton.Right))
            _lighting.AddFlash(new Light2D { Position = pointer, Radius = 260.0f, Intensity = 3.0f, Glow = 0.3f }, 0.5f);

        if (keyboard.WasPressed(Key.C))
        {
            foreach (Light2D light in _dropped)
                _lighting.RemoveLight(light);
            _dropped.Clear();
        }

        _status.Text = _lighting.Lighting == LightingMode.PathTraced
            ? "path traced, light bounces and anything that glows is a lamp"
            : "direct lighting, a light reaches what it can see and nothing else";
    }

    protected override void DisposeOther()
    {
        // The occlusion map belongs to whoever asked for it
        _occlusion?.Dispose();
        base.DisposeOther();
    }
}
