using System.Numerics;

using Horizon.Engine;
using Horizon.Input;
using Horizon.Rendering.Lighting;
using Horizon.UI.Components;

using Silk.NET.Input;

namespace Horizon.Testing.Examples.Basics;

/// <summary>
/// The least a scene can be and still have a lit world, shapes in it and a UI over it, which is what
/// <see cref="Scene2D"/> hands over before a line of your own runs. A ball bouncing about with a light on it, a
/// couple of walls for the light to bounce off, and a label. Nothing in here makes a renderer, a camera or a UI.
/// <para>
/// What to look at: <see cref="Scene2D"/> and what it comes with (<see cref="Scene2D.Camera"/>,
/// <see cref="Scene2D.Renderer"/>, <see cref="Scene2D.Lighting"/>, <see cref="Scene2D.Sprites"/>,
/// <see cref="Scene2D.Shapes"/>, <see cref="Scene2D.UI"/>), <see cref="Scene2D.Fancy"/> for the path traced
/// lighting, and <see cref="Renderer2D.FollowWindow"/> keeping all of it the size of the window.
/// </para>
/// </summary>
public class QuickStartExample : Scene2D, ITestControls
{
    private static readonly Vector3 Wall = new(0.55f, 0.5f, 0.45f);
    private static readonly Vector3 Ball = new(1.0f, 0.85f, 0.4f);

    private Vector2 ball = new(0.0f, 120.0f), velocity = new(260.0f, 180.0f);
    private Light2D? lamp;
    private Label status = null!;

    public IReadOnlyList<TestControl> Controls { get; } =
    [
        new("F", "fancy lighting on / off"),
    ];

    public QuickStartExample()
        : base(lit: true, fancy: true)
    {
        status = UI.CreateModule().AddComponent(new Label { Position = new Vector2(0.0f, -40.0f), Anchor = Horizon.Rendering.Origin.Top, TextScale = 0.3f });
    }

    public override void Initialize()
    {
        base.Initialize();

        Lighting!.Ambient = new Vector3(0.15f, 0.16f, 0.2f);
        lamp = Lighting.AddLight(new Light2D { Radius = 360.0f, Intensity = 1.4f, Size = 10.0f, Color = new Vector3(1.0f, 0.9f, 0.7f) });

        // The walls block light (an occlusion map says what does), the tracer bounces it off them
        var occlusion = new OcclusionMap2D(40, 24, new Vector2(-640.0f, -384.0f), new Vector2(32.0f));
        for (int x = 8; x < 32; x++) occlusion.Set(new Vector2(-640.0f + x * 32.0f + 16.0f, -384.0f + 4 * 32.0f + 16.0f), true);
        for (int y = 6; y < 18; y++) occlusion.Set(new Vector2(-640.0f + 30 * 32.0f + 16.0f, -384.0f + y * 32.0f + 16.0f), true);
        Lighting.Occlusion = occlusion;

        Shapes.Describe = shapes =>
        {
            shapes.FillRectangle(new Vector2(-384.0f, -256.0f), new Vector2(384.0f, -224.0f), Wall);
            shapes.FillRectangle(new Vector2(320.0f, -192.0f), new Vector2(352.0f, 192.0f), Wall);
            shapes.FillCircle(ball, 14.0f, Ball);
        };
    }

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);

        ball += velocity * dt;
        if (MathF.Abs(ball.X) > 300.0f) velocity.X = -velocity.X;
        if (ball.Y < -200.0f || ball.Y > 300.0f) velocity.Y = -velocity.Y;

        if (lamp is not null) lamp.Position = ball;

        if (Engine.Input.Keyboard.WasPressed(Key.F)) Fancy = !Fancy;
        status.Text = Fancy ? "fancy lighting, F for the plain one" : "plain lighting, F for the fancy one";
    }
}
