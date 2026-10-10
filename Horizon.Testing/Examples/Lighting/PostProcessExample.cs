using System;
using System.Numerics;

using Horizon.Core.Tweening;
using Horizon.Engine;
using Horizon.Rendering;
using Horizon.Rendering.PostProcessing;
using Horizon.Rendering.Tiling;
using Horizon.UI;
using Horizon.UI.Components;

using Silk.NET.Input;

using Button = Horizon.UI.Components.Button;

namespace Horizon.Testing.Examples.Lighting;

/// <summary>
/// What goes over the finished picture. The street of the tile map example, lit this time, and a HUD, both seen
/// through the glass of an old telly, with a blur that can be put in front of the tube.
/// <para>
/// What to look at. <see cref="Renderer2D.PostProcessing"/>, a list of effects that are run over what the
/// renderer drew, in the order they were added (<see cref="CrtEffect"/> wants to be last, it is the glass),
/// <see cref="PostEffect.Enabled"/> for switching one off without taking it out, a renderer inside a renderer
/// (the lit world is an entity of the screen, so the tube is over the world and the HUD alike), and a
/// <see cref="UICompositor"/> added to a renderer instead of the scene so it goes through the effects too.
/// </para>
/// <para>
/// In your own game.
/// <code>
/// screen = AddEntity(new Renderer2D(width, height));
/// blur = screen.PostProcessing.Add(new BlurEffect { Enabled = false });
/// screen.PostProcessing.Add(new CrtEffect { PixelSize = 3 });      // last, on top of what the others did
///
/// world = screen.AddEntity(new DeferredRenderer2D(width, height)); // lit, and behind the glass
/// screen.AddComponent(new UICompositor(camera));                  // so is the HUD
///
/// blur.Enabled = paused;                                          // any thread, takes at the next frame
/// </code>
/// </para>
/// </summary>
public class PostProcessExample : Scene, ITestControls
{
    private static readonly Vector2 DesignSize = new(1600, 900);

    // How many pixels of the screen a pixel of the art is, which is also how big a dot of the tube is
    private const float PIXEL_SIZE = 3.0f;

    private const float PAN_RANGE = 70.0f;
    private const float PAN_SPEED = 1.6f;

    // How far the button that slides goes to either side of the middle, and how long it takes one way
    private const float SLIDE_RANGE = 420.0f;
    private const float SLIDE_TIME = 0.55f;

    public override Camera ActiveCamera { get; protected set; }

    // Listed on screen by the test host
    public IReadOnlyList<TestControl> Controls { get; } =
    [
        new("C", "the picture tube on and off"),
        new("W", "the bulge of the glass on and off"),
        new("B", "a blur in front of the tube"),
        new("P", "stop and start the camera")
    ];

    private readonly Camera2D _camera;
    private readonly Renderer2D _screen;
    private readonly DeferredRenderer2D _world;
    private readonly CrtEffect _tube;
    private readonly BlurEffect _blur;
    private Horizon.Rendering.Lighting.OcclusionMap2D? _occlusion;
    private readonly TileMap _map;

    private Label _status = null!;
    private Vector2 _centre;
    private float _time;
    private bool _panning = true;

    public PostProcessExample()
    {
        Vector2 viewport = Engine.WindowManager.ViewportSize;

        _camera = AddEntity(new Camera2D(viewport / PIXEL_SIZE));
        ActiveCamera = _camera;

        // The glass: whatever is put in here is seen through the tube
        _map = TileMap.Load(World.TOWN);
        Vector4 sky = _map.BackgroundColor ?? new Vector4(0.1f, 0.13f, 0.24f, 1.0f);

        // Effects run in the order they are added, each over what the one before it left. The blur first, the
        // tube last, or the scanlines get blurred along with everything else
        _screen = AddEntity(new Renderer2D((uint)viewport.X, (uint)viewport.Y) { ClearColor = sky });
        _blur = _screen.PostProcessing.Add(new BlurEffect { Radius = 5.0f, Enabled = false });
        _tube = _screen.PostProcessing.Add(new CrtEffect { PixelSize = PIXEL_SIZE });

        // The world, lit, shown in the glass
        _world = _screen.AddEntity(new DeferredRenderer2D((uint)viewport.X, (uint)viewport.Y)
        {
            ClearColor = sky,
            Ambient = World.AmbientOf(_map),
            LightingPixelSize = 1.0f
        });

        _world.AddEntity(_map);
        _world.AddEntity(_map.Foreground);

        // The HUD: behind the glass as well, but not lit
        var viewportCamera = AddEntity(new Camera2D(viewport));
        var compositor = _screen.AddComponent(new UICompositor(viewportCamera) { DesignSize = DesignSize });

        var module = compositor.CreateModule();
        _status = module.AddComponent(new Label
        {
            Anchor = Origin.Top,
            Position = new Vector2(0, -60),
            TextScale = 0.3f
        });

        // Something of the HUD that moves, for its blur to have something to do: from side to side...
        var slider = module.AddComponent(new Button
        {
            Anchor = Origin.Top,
            Position = new Vector2(0, -130),
            Size = new Vector2(240, 64),
            Label = "SLIDING",
            VisualOffset = new Vector2(-SLIDE_RANGE, 0)
        });
        slider.TweenOffset(new Vector2(SLIDE_RANGE, 0), SLIDE_TIME).SetEasing(Easing.InOutCubic).SetLoops(-1, LoopMode.PingPong);

        // ...and in and out, where the edges move and the middle doesn't
        var grower = module.AddComponent(new Button
        {
            Anchor = Origin.TopLeft,
            Position = new Vector2(130, -250),
            Size = new Vector2(200, 64),
            Label = "GROWING",
            VisualScale = new Vector2(0.6f)
        });
        grower.TweenScale(1.5f, 0.4f).SetEasing(Easing.InOutCubic).SetLoops(-1, LoopMode.PingPong);
    }

    public override void PostInit()
    {
        base.PostInit();

        // Low enough that the street is in the picture, the map is taller than the view
        _centre = new Vector2(_map.Min.X + _map.Size.X / 2.0f, _map.Min.Y + 9.0f * _map.TileSize.Y);
        _map.ParallaxOrigin = _centre;
        _camera.Position = new Vector3(_centre, 0.0f);

        // The lamps the map has in it, and what blocks them, see the lighting example
        World.AddLights(_map, _world);
        _world.Occlusion = _occlusion = _map.CreateOcclusion();
    }

    public override void UpdateState(float dt)
    {
        var keyboard = Engine.Input.Keyboard;

        if (keyboard.WasPressed(Key.C)) _tube.Enabled = !_tube.Enabled;
        if (keyboard.WasPressed(Key.P)) _panning = !_panning;
        if (keyboard.WasPressed(Key.B)) _blur.Enabled = !_blur.Enabled;
        if (keyboard.WasPressed(Key.W)) _tube.Warp = _tube.Warp == Vector2.Zero ? new Vector2(1.0f / 32.0f, 1.0f / 24.0f) : Vector2.Zero;

        if (_panning)
            _time += dt;

        // On whole pixels of the art, which is what makes it step rather than glide
        float x = _centre.X + MathF.Sin(_time * PAN_SPEED) * PAN_RANGE;
        _camera.Position = new Vector3(MathF.Round(x), MathF.Round(_centre.Y), 0.0f);

        _status.Text = $"tube {(_tube.Enabled ? "on" : "off")}    warp {(_tube.Warp == Vector2.Zero ? "off" : "on")}    blur {(_blur.Enabled ? "on" : "off")}    camera {(_panning ? "panning" : "stopped")}";

        base.UpdateState(dt);
    }

    protected override void DisposeOther()
    {
        _occlusion?.Dispose();
        base.DisposeOther();
    }
}
