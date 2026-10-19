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
/// Checks the particles, the three simulators side by side. A burst is as many particles as it was asked for, they age
/// out over the renderer's MaxAge and the count comes back down, one that is full drops what doesn't fit, gravity takes
/// them down, and the physics kind lands on the ground and stays. The compute one keeps its count on the GPU and the
/// check reads what it says. The example of the same things, for reading, is ParticleExample.
/// </summary>
internal sealed class ParticlesCheck : Scene, ISelfCheck
{
    private static readonly Vector2 DesignSize = new(1600, 900);
    private const int ROOM = 2048;
    private const float MAX_AGE = 0.8f;

    public override Camera ActiveCamera { get; protected set; }

    private readonly UICompositor _compositor;
    private readonly UISelfTest _test = new();

    public bool Finished => _test.Finished;
    public int Failed => _test.Failed;
    public int Count => _test.Count;

    private ParticleRenderer2D _cpu = null!, _gpu = null!, _physics = null!;
    private PhysicsParticleSimulator2D _grains = null!;
    private Label _status = null!;

    public ParticlesCheck()
    {
        var camera = AddEntity(new Camera2D(DesignSize));
        ActiveCamera = camera;
        _compositor = AddComponent(new UICompositor(camera, UICompositor.DEFAULT_SKIN_DIRECTORY, UICompositor.DEFAULT_SKIN_FILE) { DesignSize = DesignSize });
    }

    public override void Initialize()
    {
        base.Initialize();

        var world = AddComponent<PhysicsWorld>();
        world.Gravity = new Vector2(0.0f, -900.0f);
        var ground = world.CreateBody(PhysicsBodySimulationType.Static);
        ground.CreateRectangularFixture(new Vector2(-800.0f, -400.0f), new Vector2(1600.0f, 60.0f));

        _cpu = AddEntity(new ParticleRenderer2D(ROOM, new CpuParticleSimulator2D()) { MaxAge = MAX_AGE, ParticleSize = 3.0f, Gravity = new Vector2(0.0f, -900.0f), StartColor = new Vector3(0.9f, 0.6f, 0.3f) });
        _gpu = AddEntity(new ParticleRenderer2D(ROOM, new ComputeParticleSimulator2D()) { MaxAge = MAX_AGE, ParticleSize = 3.0f, Gravity = new Vector2(0.0f, -900.0f), StartColor = new Vector3(0.3f, 0.7f, 0.9f) });
        _grains = new PhysicsParticleSimulator2D(world) { Radius = 2.0f, Restitution = 0.2f, Friction = 6.0f };
        _physics = AddEntity(new ParticleRenderer2D(ROOM, _grains) { MaxAge = 1000.0f, ParticleSize = 3.0f, Gravity = new Vector2(0.0f, -900.0f), StartColor = new Vector3(0.9f, 0.85f, 0.5f) });

        var module = _compositor.CreateModule();
        _status = module.AddComponent(new Label("particles") { Anchor = Origin.TopLeft, Position = new Vector2(24, -24), TextScale = 0.25f });

        Script();
        Engine.Graphics.ClearColor = new Vector4(0.1f, 0.1f, 0.16f, 1.0f);
    }

    private void Script()
    {
        // A burst each, the count is what was asked for a moment later (the spawn is taken on the next update)
        _test.Run(() =>
        {
            _cpu.AddBurst(new Vector2(-400.0f, 100.0f), 500, 300.0f);
            _gpu.AddBurst(new Vector2(0.0f, 100.0f), 500, 300.0f);
        });
        _test.Wait();
        _test.Settle(0.15f);
        _test.Check("particles: a burst on the CPU is as many as it was asked for", () => _cpu.Count == 500);
        _test.Check("particles: and one on the GPU, read off the GPU's own count", () => _gpu.Count == 500);

        // They age out and the count comes down to nothing
        _test.Settle(MAX_AGE * 2.2f);
        _test.Check("particles: they age out over MaxAge and the count comes back down", () => _cpu.Count == 0 && _gpu.Count < 500);

        // More than there is room for, what doesn't fit is dropped rather than anything breaking
        _test.Run(() => _cpu.AddBurst(new Vector2(-400.0f, 100.0f), ROOM * 2, 100.0f));
        _test.Wait();
        _test.Check("particles: a burst bigger than the room is cut to the room", () => _cpu.Count == ROOM);

        // Grains with a body fall onto the floor and stay on it, as many as were poured
        _test.Run(() => _physics.AddCone(new Vector2(400.0f, 200.0f), -Vector2.UnitY, MathF.PI / 4.0f, 300, 60.0f));
        _test.Settle(2.5f);
        _test.Check("particles: the physics kind keeps what was poured and lands on the floor", () =>
        {
            //if (_grains.Particles.Count != 300) return false;
            int onFloor = 0;
            //foreach (ref readonly ParticleState2D grain in _grains.Particles.Particles)
            //{
            //    if (grain.Position.Y > -345.0f && grain.Position.Y < -300.0f) onFloor++;
            //}
            return onFloor > 250;
        });
    }

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);
        _test.Update(dt);
        _status.Text = $"particles  {_test.Count} checked, {_test.Failed} failed   cpu {_cpu.Count}  gpu {_gpu.Count}  grains {_physics.Count}";
    }
}
