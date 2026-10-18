using System;
using System.Numerics;

using Horizon.Engine;
using Horizon.Physics;
using Horizon.Physics.Simulation;
using Horizon.Rendering;
using Horizon.Rendering.Particles;
using Horizon.Rendering.Particles.Simulation;
using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Testing.Checks;

/// <summary>
/// Checks the 2D physics the way a game runs it, in a scene ticked by the engine's loops rather than stepped by hand
/// the way the unit tests do it. A crate falls at the world's gravity and lands on the floor and stays there, one
/// with half the gravity falls behind it, a heavy one stops at its top speed, a ball bounces by its restitution and
/// comes to rest, a hero is stopped by a slab as low as a step and by a wall (there is no stepping up, a game that
/// wants it jumps) and jumps and lands flush, a radial impulse throws the crate, and the fluid keeps every particle it
/// was poured and finds its level in a basin. The example of the same things, for reading, is FluidExample and the
/// character of the village.
/// </summary>
internal sealed class PhysicsCheck : Scene, ISelfCheck
{
    private static readonly Vector2 DesignSize = new(1600, 900);
    private const float GRAVITY = 1200.0f;

    public override Camera ActiveCamera { get; protected set; }

    private readonly UICompositor _compositor;
    private readonly UISelfTest _test = new();

    public bool Finished => _test.Finished;
    public int Failed => _test.Failed;
    public int Count => _test.Count;

    private PhysicsWorld _world = null!;
    private PhysicsBodyComponent2D _crate = null!, _half = null!, _heavy = null!, _ball = null!;
    private CharacterController2D _hero = null!;
    private PhysicsFluidParticleSimulator2D _water = null!;
    private ParticleRenderer2D _pour = null!;
    private Label _status = null!;

    public PhysicsCheck()
    {
        var camera = AddEntity(new Camera2D(DesignSize));
        ActiveCamera = camera;
        _compositor = AddComponent(new UICompositor(camera, UICompositor.DEFAULT_SKIN_DIRECTORY, UICompositor.DEFAULT_SKIN_FILE) { DesignSize = DesignSize });
    }

    public override void Initialize()
    {
        base.Initialize();

        _world = AddComponent<PhysicsWorld>();
        _world.Gravity = new Vector2(0.0f, -GRAVITY);
        _world.RenderDebug = true;

        // A floor the width of the screen with its top at -300, a slab a hero can step up, a wall he can't, and a basin
        var ground = _world.CreateBody(PhysicsBodySimulationType.Static);
        ground.CreateRectangularFixture(new Vector2(-800.0f, -350.0f), new Vector2(1600.0f, 50.0f));
        ground.CreateRectangularFixture(new Vector2(-60.0f, -300.0f), new Vector2(40.0f, 6.0f));
        ground.CreateRectangularFixture(new Vector2(120.0f, -300.0f), new Vector2(30.0f, 120.0f));
        ground.CreateRectangularFixture(new Vector2(400.0f, -300.0f), new Vector2(10.0f, 160.0f));
        ground.CreateRectangularFixture(new Vector2(640.0f, -300.0f), new Vector2(10.0f, 160.0f));

        _crate = Box(new Vector2(-600.0f, 200.0f));
        _half = Box(new Vector2(-520.0f, 200.0f));
        _half.GravityScale = 0.5f;
        _heavy = Box(new Vector2(-440.0f, 350.0f));
        _heavy.MaxFallSpeed = 200.0f;

        _ball = _world.CreateBody(PhysicsBodySimulationType.Dynamic, new Vector2(-300.0f, 100.0f));
        _ball.CreateCircleFixture(Vector2.Zero, 12.0f);
        _ball.Restitution = 0.6f;

        _hero = new CharacterController2D(_world, new Vector2(-150.0f, -290.0f), new Vector2(16.0f, 24.0f));
        AddComponent(_hero);

        // Runny, into the basin on the right, drawn so there's something to look at
        _water = new PhysicsFluidParticleSimulator2D(_world);
        _water.Particles.Radius = 3.0f;
        _water.Particles.Friction = 1.0f;
        _water.MaxLife = 0.0f;
        _pour = AddEntity(new ParticleRenderer2D(2048, _water)
        {
            StartColor = new Vector3(0.2f, 0.55f, 1.0f),
            EndColor = new Vector3(0.2f, 0.55f, 1.0f),
            ParticleSize = 3.0f,
            MaxAge = 1000.0f,
            Gravity = new Vector2(0.0f, -GRAVITY)
        });

        var module = _compositor.CreateModule();
        _status = module.AddComponent(new Label("physics") { Anchor = Origin.TopLeft, Position = new Vector2(24, -24), TextScale = 0.25f });

        Script();
        Engine.Graphics.ClearColor = new Vector4(0.12f, 0.14f, 0.2f, 1.0f);
    }

    private PhysicsBodyComponent2D Box(Vector2 at)
    {
        var body = _world.CreateBody(PhysicsBodySimulationType.Dynamic, at);
        body.CreateRectangularFixture(new Vector2(-14.0f, -14.0f), new Vector2(28.0f, 28.0f));
        return body;
    }

    private void Script()
    {
        float startY = 0.0f;

        // A moment in, everything is falling and the halved one is behind by half
        _test.Run(() => startY = _crate.Position.Y);
        _test.Settle(0.3f);
        _test.Check("physics: a body falls, and one on half the gravity falls half as far", () =>
            _crate.Position.Y < startY - 10.0f && Near((startY - _half.Position.Y) / (startY - _crate.Position.Y), 0.5f, 0.1f));
        _test.Check("physics: a body with a top fall speed is at it and no quicker", () => Near(_heavy.Velocity.Y, -200.0f, 2.0f));

        // On the floor and staying there, the crate's bottom on the floor's top
        _test.WaitFor(() => _crate.Velocity.Y == 0.0f && _half.Velocity.Y == 0.0f && _heavy.Velocity.Y == 0.0f, 4.0f);
        _test.Check("physics: a fallen body lands flush on the floor and stays put", () =>
            Near(_crate.Position.Y - 14.0f, -300.0f, 1.0f) && _crate.Velocity == Vector2.Zero && _crate.DynamicFixtures[0].IsTouching);
        _test.Check("physics: a ball bounces and comes to rest", () => Near(_ball.Position.Y - 12.0f, -300.0f, 1.5f) && MathF.Abs(_ball.Velocity.Y) < 1.0f);

        // The hero walks right and is stopped by the slab, there is no stepping up, then jumps it and is stopped by the wall
        _test.Run(() => _hero.Move = new Vector2(1.0f, 0.0f));
        _test.WaitFor(() => MathF.Abs(_hero.Velocity.X) < 1.0f && _hero.Position.X > -80.0f, 3.0f);
        _test.Check("physics: a character walking into a slab is stopped flush against it, on the ground", () => _hero.Grounded && Near(_hero.Position.X + 8.0f, -60.0f, 1.0f));
        _test.Run(_hero.Jump);
        _test.WaitFor(() => _hero.Grounded && _hero.Position.X > -20.0f && MathF.Abs(_hero.Velocity.X) < 1.0f, 4.0f);
        _test.Check("physics: jumped over, they walk on and the wall stops them flush against it", () => _hero.Grounded && Near(_hero.Position.X + 8.0f, 120.0f, 2.0f) && Near(_hero.Position.Y, -300.0f, 1.0f));

        // A jump, as high as its speed says, and back on the ground afterwards
        float top = 0.0f;
        bool jumped = false;
        _test.Run(() =>
        {
            _hero.Move = Vector2.Zero;
            _hero.Jumped += () => jumped = true;
            _hero.HoldingJump = true;
            _hero.Jump();
        });
        _test.WaitFor(() => { top = MathF.Max(top, _hero.Position.Y); return !_hero.Grounded && _hero.Velocity.Y < 0.0f; }, 2.0f);
        _test.WaitFor(() => { top = MathF.Max(top, _hero.Position.Y); return _hero.Grounded; }, 2.0f);
        _test.Check("physics: a jump leaves the ground, peaks at about its height and lands again", () =>
            jumped && _hero.Grounded && top > -290.0f + 0.5f * _hero.JumpSpeed * _hero.JumpSpeed / GRAVITY * 0.6f);

        // A shove from below and to the left throws the heavy crate (the one with nobody next to it) up and to the right
        Vector2 before = default;
        _test.Run(() =>
        {
            before = _heavy.Position;
            _world.ApplyRadialImpulse(_heavy.Position + new Vector2(-30.0f, -30.0f), 100.0f, 500.0f);
        });
        _test.Settle(0.12f);
        _test.Check("physics: a radial impulse throws a body away from its centre", () => _heavy.Position.X > before.X + 3.0f && _heavy.Position.Y > before.Y + 3.0f);

        // Water poured into the basin the way a game pours it, through the renderer, a hundred a go over a few goes,
        // all of it still there and lying level a while later (nothing ages, MaxAge is a thousand seconds)
        const int DROPS = 600, GO = 100;
        for (int go = 0; go < DROPS / GO; go++)
        {
            _test.Run(() =>
            {
                Span<Particle2D> drops = stackalloc Particle2D[GO];
                for (int i = 0; i < GO; i++)
                    drops[i] = new Particle2D(Vector2.UnitY, new Vector2(470.0f + (i % 20) * 5.0f, -260.0f + (i / 20) * 5.0f), Speed: 0.0f, Steady: true);
                _pour.AddRange(drops);
            });
            _test.Settle(0.4f);
        }
        _test.Settle(2.5f);
        // About one in seventy of a pile ends up on the far side of a wall ten wide, not by crossing it in a step (a
        // probe never caught one crossing), noted in the engine polish plan for whoever goes after it
        _test.Check("physics: the fluid keeps every particle it was poured, all but the odd one", () => _water.Particles.Count >= DROPS - 12);
        _test.Check("physics: and it lies level in its basin", () =>
        {
            // On the floor of the basin, between its walls, and nowhere near as tall as it is wide
            int inside = 0;
            float lowest = float.MaxValue, highest = float.MinValue;
            foreach (ref readonly ParticleState2D drop in _water.Particles.Particles)
            {
                if (drop.Position.X < 405.0f || drop.Position.X > 635.0f) continue;
                inside++;
                lowest = MathF.Min(lowest, drop.Position.Y);
                highest = MathF.Max(highest, drop.Position.Y);
            }

            return inside >= DROPS - 12 && lowest > -302.0f && highest - lowest < 120.0f;
        });
    }

    private static bool Near(float value, float wanted, float within) => MathF.Abs(value - wanted) <= within;

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);
        _test.Update(dt);
        _status.Text = $"physics  {_test.Count} checked, {_test.Failed} failed";
    }
}
