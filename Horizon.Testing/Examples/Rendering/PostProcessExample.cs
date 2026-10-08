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

namespace Horizon.Testing.Examples.Rendering;

/// <summary>
/// The post processing of the 2D renderers.
/// The HUD has a blur of its own instead (<see cref="UICompositor.PostProcessing"/>): a button that slides back and
/// forth and one that grows and shrinks are smeared the way they move, the line of text that stands still is not.
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
        new("M", "the motion blur of the world on and off"),
        new("U", "the motion blur of the HUD on and off"),
        new("Up / Down", "open the shutter for longer or shorter"),
        new("W", "the bulge of the glass on and off"),
        new("P", "stop and start the camera")
    ];

    private readonly Camera2D _camera;
    private readonly Renderer2D _screen;
    private readonly DeferredRenderer2D _world;
    private readonly VelocityBlurEffect _blur, _hudBlur;
    private readonly CrtEffect _tube;
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
        _screen = AddEntity(new Renderer2D((uint)viewport.X, (uint)viewport.Y) { ClearColor = new Vector4(0.36f, 0.62f, 0.86f, 1.0f) });
        _tube = _screen.PostProcessing.Add(new CrtEffect { PixelSize = PIXEL_SIZE });

        // The world, lit and blurred, shown in the glass
        _world = _screen.AddEntity(new DeferredRenderer2D((uint)viewport.X, (uint)viewport.Y) { ClearColor = new Vector4(0.36f, 0.62f, 0.86f, 1.0f) });
        _blur = _world.PostProcessing.Add(new VelocityBlurEffect());

        string directory = Path.Combine(Path.GetTempPath(), "horizon-tilemap-test");
        TileMapExample.WriteFiles(directory);

        _map = _world.AddEntity(TileMap.Load(Path.Combine(directory, "town.tmx")));
        _world.AddEntity(_map.Foreground);

        // The HUD: behind the glass as well, but neither lit nor blurred
        var viewportCamera = AddEntity(new Camera2D(viewport));
        var compositor = _screen.AddComponent(new UICompositor(viewportCamera) { DesignSize = DesignSize });
        _hudBlur = compositor.PostProcessing.Add(new VelocityBlurEffect());

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

        _centre = _map.Min + _map.Size / 2.0f;
        _map.ParallaxOrigin = _centre;
        _camera.Position = new Vector3(_centre, 0.0f);
    }

    public override void UpdateState(float dt)
    {
        var keyboard = Engine.Input.Keyboard;

        if (keyboard.WasPressed(Key.C)) _tube.Enabled = !_tube.Enabled;
        if (keyboard.WasPressed(Key.M)) _blur.Enabled = !_blur.Enabled;
        if (keyboard.WasPressed(Key.U)) _hudBlur.Enabled = !_hudBlur.Enabled;
        if (keyboard.WasPressed(Key.P)) _panning = !_panning;
        if (keyboard.WasPressed(Key.W)) _tube.Warp = _tube.Warp == Vector2.Zero ? new Vector2(1.0f / 32.0f, 1.0f / 24.0f) : Vector2.Zero;
        if (keyboard.WasPressed(Key.Up)) _blur.Shutter = MathF.Min(_blur.Shutter * 1.5f, 0.2f);
        if (keyboard.WasPressed(Key.Down)) _blur.Shutter = MathF.Max(_blur.Shutter / 1.5f, 1.0f / 480.0f);

        if (_panning)
            _time += dt;

        // On whole pixels of the art, which is what makes it step rather than glide
        float x = _centre.X + MathF.Sin(_time * PAN_SPEED) * PAN_RANGE;
        _camera.Position = new Vector3(MathF.Round(x), MathF.Round(_centre.Y), 0.0f);

        _status.Text =
            $"tube {(_tube.Enabled ? "on" : "off")}    blur {(_blur.Enabled ? "on" : "off")}    hud blur {(_hudBlur.Enabled ? "on" : "off")}    " +
            $"shutter 1/{1.0f / _blur.Shutter:0}s    camera {MathF.Abs(_camera.Velocity.X):0} px/s";

        base.UpdateState(dt);
    }
}
