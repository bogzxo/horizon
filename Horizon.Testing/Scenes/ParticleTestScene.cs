using System.Numerics;

using Horizon.Engine;
using Horizon.Input;
using Horizon.Rendering.Particles;
using Horizon.Rendering.Particles.Simulation;

using Silk.NET.Input;

namespace Horizon.Testing.Scenes;

/// <summary>
/// Runs both particle simulators side by side: the CPU one owns the left half of the screen (orange),
/// the compute shader one the right half (blue). Each side sets off an explosion every so often.
/// Left click: explosion. Hold right click: fountain. G: toggle gravity.
/// </summary>
public class ParticleTestScene : Scene
{
    private const float BurstInterval = 1.5f;

    private static readonly Vector2 Gravity = new(0, -600);

    public override Camera ActiveCamera { get; protected set; }

    private ParticleRenderer2D cpuParticles = null!;
    private ParticleRenderer2D gpuParticles = null!;

    private float burstTimer = BurstInterval;
    private bool prevMouseClicked, autoBomb;

    public ParticleTestScene()
    {
        ActiveCamera = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize));
    }

    public override void Initialize()
    {
        // The renderers compile their shaders as they are constructed, so they need the GL context.
        cpuParticles = AddEntity(
            new ParticleRenderer2D(65536, new CpuParticleSimulator2D())
            {
                StartColor = new Vector3(1.0f, 0.8f, 0.3f),
                EndColor = new Vector3(0.5f, 0.1f, 0.0f),
                ParticleSize = 1.5f,
                Gravity = Gravity
            });

        gpuParticles = AddEntity(
            new ParticleRenderer2D(65536, new ComputeParticleSimulator2D())
            {
                StartColor = new Vector3(0.5f, 0.9f, 1.0f),
                EndColor = new Vector3(0.0f, 0.1f, 0.5f),
                ParticleSize = 1.5f,
                Gravity = Gravity
            });

        base.Initialize();
    }

    public override void PostInit()
    {
        base.PostInit();

        Console.WriteLine("ParticleTestScene\r\n\r\n CPU simulator on the left, compute shader simulator on the right.");
        Console.WriteLine("Left click: explosion. Hold right click: fountain. G: toggle gravity. F: toggle autobomb");
        Engine.GL.ClearColor(0.02f, 0.02f, 0.04f, 1.0f);
    }

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);

        if (Engine.InputManager.KeyboardManager.IsKeyPressed(Key.G))
        {
            cpuParticles.Gravity = gpuParticles.Gravity =
                cpuParticles.Gravity == Vector2.Zero ? Gravity : Vector2.Zero;
        }
        if (Engine.InputManager.KeyboardManager.IsKeyPressed(Key.F))
        {
            autoBomb = !autoBomb;
        }

        // The same explosion on both sides, so the two simulators can be compared by eye.
        burstTimer += dt;
        if (autoBomb && burstTimer >= BurstInterval)
        {
            burstTimer = 0.0f;

            float quarter = Engine.WindowManager.ViewportSize.X / 4.0f;
            cpuParticles.AddBurst(new Vector2(-quarter, 100), 4000, 350);
            gpuParticles.AddBurst(new Vector2(quarter, 100), 4000, 350);
        }

        var mouseData = Engine.InputManager.MouseManager.GetData();
        var mousePos = ActiveCamera.ScreenToWorld(mouseData.Position);
        var particles = mousePos.X < 0 ? cpuParticles : gpuParticles;

        bool mouseClicked = (mouseData.Actions & VirtualAction.PrimaryAction) != 0;
        if (mouseClicked && !prevMouseClicked)
            particles.AddBurst(mousePos, 8000, 500);
        prevMouseClicked = mouseClicked;

        if ((mouseData.Actions & VirtualAction.SecondaryAction) != 0)
            particles.AddCone(mousePos, Vector2.UnitY, MathF.PI / 6.0f, 64, 700);
    }
}
