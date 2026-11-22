using System.Collections.Concurrent;
using System.Numerics;

using Horizon.Engine;
using Horizon.Input;

using ThreeBody.Rendering;
using ThreeBody.Simulation;
using ThreeBody.UI;

namespace ThreeBody;

/// <summary>The three body problem in 2D: simulation by Dev PC, drawing and HUD here.</summary>
public sealed class MainScene : Scene2D
{
    private const float VelocityPerWorldUnit = 1.0f / BodyRenderer.VelocityArrowScale;   // the drag is the velocity arrow the body will show
    private const float PanThresholdPixels = 6.0f;
    private const float HudWidthPixels = 330.0f;   // left panel plus its margin, the automatic camera keeps bodies clear of it

    private readonly NBodySystem sim = new(Preset.FigureEight);
    private readonly BodyRenderer bodies;
    private readonly CameraFit fit = new();
    private readonly Effects effects = new();
    private readonly NameLabels labels;
    private readonly Hud hud;

    // Everything that changes the simulation runs on the physics tick, where Update runs
    private readonly ConcurrentQueue<Action> simActions = new();

    // Intents set by UI callbacks, applied on the next update
    private Preset? pendingPreset;
    private bool pendingReset, pendingPause;
    private double? pendingSpeed;

    // Manual camera (zoom and pan), F hands it back to the automatic fit
    private bool manualCamera;
    private Vector2 manualCentre;
    private float manualSpan = 4.0f;

    // Inspector: a click without a drag on a body selects it, Escape clears
    private Body? selected;
    private Vector2 pressScreen;

    // Placing a body: press sets the position, drag sets the velocity
    private bool placing;
    private Vector2 placeStart, placeNow;

    // Panning with middle button or a right drag, a right click without moving deletes
    private bool rightDown, middleDown, panning;
    private Vector2 rightStartScreen;

    public MainScene()
    {
        bodies = new BodyRenderer();
        labels = new NameLabels(UI);
        hud = new Hud(UI)
        {
            OnPreset = p => pendingPreset = p,
            OnReset = () => pendingReset = true,
            OnTogglePause = () => pendingPause = true,
            OnSpeed = s => pendingSpeed = s
        };
    }

    public override void UpdatePhysics(float dt)
    {
        base.UpdatePhysics(dt);
        while (simActions.TryDequeue(out var action)) action();
        sim.Update(dt);
    }

    public override void Initialize()
    {
        base.Initialize();
        Engine.Graphics.ClearColor = new Vector4(0.02f, 0.02f, 0.05f, 1.0f);

        Shapes.Describe = shapes =>
        {
            var viewport = Engine.WindowManager.ViewportSize;
            var centre = new Vector2(Camera.Position.X, Camera.Position.Y);
            float span = Camera.ViewSize.Y;
            float upp = span / MathF.Max(1.0f, viewport.Y);
            bodies.Describe(shapes, sim, centre, span, viewport);
            effects.Describe(shapes, upp);
            DescribePlacement(shapes, upp);
            DescribeSelection(shapes, upp);
            hud.DescribeSparkline(shapes, centre, span, viewport);
        };
    }

    private void DescribeSelection(Horizon.Rendering.Primitives.ShapeList shapes, float upp)
    {
        var body = selected;
        if (body == null || !sim.Bodies.Contains(body)) return;
        float radius = MathF.Max(0.03f, 0.05f * MathF.Cbrt((float)body.Mass)) * 1.9f + upp * 3.0f;
        var p = new Vector2((float)body.X, (float)body.Y);
        var colour = new Vector4(1.0f, 1.0f, 1.0f, 0.9f);
        const int Segments = 48;
        var prev = p + new Vector2(radius, 0.0f);
        for (int i = 1; i <= Segments; i++)
        {
            float a = i * MathF.Tau / Segments;
            var next = p + new Vector2(MathF.Cos(a), MathF.Sin(a)) * radius;
            shapes.Line(prev, next, upp * 2.0f, colour);
            prev = next;
        }
    }

    private void DescribePlacement(Horizon.Rendering.Primitives.ShapeList shapes, float upp)
    {
        if (!placing) return;
        var c = new Vector4(1.0f, 1.0f, 1.0f, 1.0f);
        shapes.FillCircle(placeStart, 0.05f, new Vector4(c.X, c.Y, c.Z, 0.35f));
        shapes.Line(placeStart, placeNow, upp * 2.0f, new Vector4(c.X, c.Y, c.Z, 0.8f));
        // Arrow head
        var d = placeNow - placeStart;
        if (d.LengthSquared() > 1e-8f)
        {
            var dir = Vector2.Normalize(d);
            var side = new Vector2(-dir.Y, dir.X);
            float h = upp * 10.0f;
            shapes.Line(placeNow, placeNow - dir * h + side * h * 0.5f, upp * 2.0f, new Vector4(c.X, c.Y, c.Z, 0.8f));
            shapes.Line(placeNow, placeNow - dir * h - side * h * 0.5f, upp * 2.0f, new Vector4(c.X, c.Y, c.Z, 0.8f));
        }
    }

    public override void UpdateState(float dt)
    {
        var kb = Engine.Input.Keyboard;
        if (kb.WasPressed(Key.Space)) pendingPause = true;
        if (kb.WasPressed(Key.R)) pendingReset = true;
        if (kb.WasPressed(Key.S)) SaveScenario();
        if (kb.WasPressed(Key.L)) ToggleScenarioList();

        Key[] numberKeys = [Key.Number1, Key.Number2, Key.Number3, Key.Number4, Key.Number5, Key.Number6, Key.Number7, Key.Number8, Key.Number9];
        if (scenarioNames != null)
        {
            // With the scenario list open the number keys pick a scenario instead of a preset
            for (int i = 0; i < numberKeys.Length && i < scenarioNames.Count; i++)
                if (kb.WasPressed(numberKeys[i])) LoadScenario(scenarioNames[i]);
        }
        else
        {
            if (kb.WasPressed(Key.Number1)) pendingPreset = Preset.FigureEight;
            if (kb.WasPressed(Key.Number2)) pendingPreset = Preset.Lagrange;
            if (kb.WasPressed(Key.Number3)) pendingPreset = Preset.Chaotic;
            if (kb.WasPressed(Key.Number4)) pendingPreset = Preset.SunPlanetMoon;
            if (kb.WasPressed(Key.Number5)) pendingPreset = Preset.Pythagorean;
            if (kb.WasPressed(Key.Number6)) pendingPreset = Preset.BinaryPlanet;
            if (kb.WasPressed(Key.Number7)) pendingPreset = Preset.Butterfly;
            if (kb.WasPressed(Key.Number8)) pendingPreset = Preset.Sitnikov;
        }

        if (kb.WasPressed(Key.LeftBracket)) pendingSpeed = Math.Max(Math.Pow(2.0, Hud.MinSpeedLog2), (pendingSpeed ?? sim.TimeScale) * 0.5);
        if (kb.WasPressed(Key.RightBracket)) pendingSpeed = Math.Min(Math.Pow(2.0, Hud.MaxSpeedLog2), (pendingSpeed ?? sim.TimeScale) * 2.0);

        if (kb.WasPressed(Key.T)) simActions.Enqueue(sim.ReverseTime);

        if (kb.WasPressed(Key.I)) simActions.Enqueue(() => sim.Integrator = sim.Integrator == Integrator.VelocityVerlet ? Integrator.Yoshida4 : Integrator.VelocityVerlet);
        if (kb.WasPressed(Key.M)) simActions.Enqueue(() => sim.Collisions = (CollisionMode)(((int)sim.Collisions + 1) % 3));
        if (kb.WasPressed(Key.C)) { simActions.Enqueue(sim.CenterOfMassFrame); bodies.ClearTrails(); }
        if (kb.WasPressed(Key.V)) bodies.ShowVelocity = !bodies.ShowVelocity;
        if (kb.WasPressed(Key.G)) bodies.ShowGrid = !bodies.ShowGrid;
        if (kb.WasPressed(Key.K)) bodies.ShowCentreOfMass = !bodies.ShowCentreOfMass;
        if (kb.WasPressed(Key.N)) labels.Visible = !labels.Visible;
        if (kb.WasPressed(Key.P))
        {
            Directory.CreateDirectory("screenshots");
            Engine.CaptureScreenshot(Path.Combine("screenshots", $"threebody-{DateTime.Now:yyyyMMdd-HHmmss}.png"));
        }
        if (kb.WasPressed(Key.Period) && sim.Paused) simActions.Enqueue(sim.StepOnce);
        if (kb.WasPressed(Key.F)) manualCamera = false;
        if (kb.WasPressed(Key.Escape)) selected = null;

        // Paused simulations don't run the physics tick, so the actions are applied here too
        if (sim.Paused) while (simActions.TryDequeue(out var action)) action();

        if (pendingPause) { sim.Paused = !sim.Paused; pendingPause = false; }
        if (pendingSpeed is { } speed) { sim.TimeScale = speed; pendingSpeed = null; }
        if (pendingPreset is { } preset) { sim.Reset(preset); fit.Snap(); manualCamera = false; pendingPreset = null; pendingReset = false; }
        if (pendingReset) { sim.Reset(); fit.Snap(); manualCamera = false; pendingReset = false; }

        HandleMouse();

        base.UpdateState(dt);

        bodies.Record(sim);
        var viewport = Engine.WindowManager.ViewportSize;
        if (manualCamera)
        {
            float aspect = viewport.X / MathF.Max(1.0f, viewport.Y);
            Camera.ViewSize = new Vector2(manualSpan * aspect, manualSpan);
            Camera.Position = new Vector3(manualCentre, 0.0f);
        }
        else
        {
            fit.Update(sim, Camera, viewport, dt, HudWidthPixels);
        }
        effects.Drain(sim, dt);
        labels.Update(sim, new Vector2(Camera.Position.X, Camera.Position.Y), Camera.ViewSize.Y, viewport);
        hud.Refresh(sim);
        hud.ShowInspector(sim, selected is { } s && sim.Bodies.Contains(s) ? s : null);
    }

    // Scenarios: S saves next to the exe and in the project's Scenarios folder, L lists them and the number keys load one
    private IReadOnlyList<string>? scenarioNames;
    private string scenarioStatus = "";
    private int saveCount;

    private static string ExeScenarioDir => Path.Combine(AppContext.BaseDirectory, "Scenarios");

    /// <summary>The Scenarios folder of the source project, found by walking up from the exe, or null when run elsewhere.</summary>
    private static string? ProjectScenarioDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ThreeBody.csproj"))) return Path.Combine(dir.FullName, "Scenarios");
        return null;
    }

    private void SaveScenario()
    {
        string name = $"saved-{DateTime.Now:yyyyMMdd-HHmmss}-{++saveCount}";
        var data = ScenarioStore.Capture(name, sim);
        ScenarioStore.Save(Path.Combine(ExeScenarioDir, name + ".json"), data);
        if (ProjectScenarioDir() is { } projectDir) ScenarioStore.Save(Path.Combine(projectDir, name + ".json"), data);
        scenarioStatus = $"Saved {name}";
        if (scenarioNames != null) scenarioNames = ScenarioStore.List(ExeScenarioDir);
        hud.ShowScenarios(scenarioNames, scenarioStatus);
    }

    private void ToggleScenarioList()
    {
        scenarioNames = scenarioNames == null ? ScenarioStore.List(ExeScenarioDir) : null;
        if (scenarioNames != null) scenarioStatus = "";
        hud.ShowScenarios(scenarioNames, scenarioStatus);
    }

    private void LoadScenario(string name)
    {
        var data = ScenarioStore.Load(Path.Combine(ExeScenarioDir, name + ".json"));
        if (data == null) { scenarioStatus = $"Could not read {name}"; hud.ShowScenarios(scenarioNames, scenarioStatus); return; }
        simActions.Enqueue(() => sim.LoadScenario(data));
        pendingReset = false;
        fit.Snap();
        manualCamera = false;
        selected = null;
        bodies.ClearTrails();
        scenarioStatus = $"Loaded {name}";
        hud.ShowScenarios(scenarioNames, scenarioStatus);
    }

    private void TakeManualCamera()
    {
        if (manualCamera) return;
        manualCamera = true;
        manualCentre = new Vector2(Camera.Position.X, Camera.Position.Y);
        manualSpan = Camera.ViewSize.Y;
    }

    private void HandleMouse()
    {
        var mouse = Engine.Input.Mouse;
        var pos = mouse.Position;
        bool overUi = UI.IsPointerOverUI;

        // Wheel zoom, keeping the world point under the pointer where it is
        float scroll = mouse.Scroll;
        if (scroll != 0.0f && !overUi)
        {
            TakeManualCamera();
            var before = Camera.ScreenToWorld(pos);
            float factor = MathF.Exp(-scroll * 0.15f);
            float newSpan = Math.Clamp(manualSpan * factor, 0.2f, 2000.0f);
            float applied = newSpan / manualSpan;
            manualCentre = before + (manualCentre - before) * applied;
            manualSpan = newSpan;
        }

        // Left: place a body, drag to set the velocity
        if (mouse.WasPressed(MouseButton.Left) && !overUi)
        {
            placing = true;
            pressScreen = pos;
            placeStart = placeNow = Camera.ScreenToWorld(pos);
        }
        if (placing)
        {
            placeNow = Camera.ScreenToWorld(pos);
            if (mouse.WasReleased(MouseButton.Left) || !mouse.IsDown(MouseButton.Left))
            {
                var start = placeStart;
                var v = (placeNow - placeStart) * VelocityPerWorldUnit;
                placing = false;
                int hit = -1;
                if (Vector2.Distance(pos, pressScreen) <= PanThresholdPixels)
                {
                    float pad = Camera.ViewSize.Y / MathF.Max(1.0f, Engine.WindowManager.ViewportSize.Y) * 6.0f;
                    hit = sim.FindBody(start.X, start.Y, pad);
                }
                if (hit >= 0) selected = sim.Bodies[hit];
                else simActions.Enqueue(() => sim.AddBody(start.X, start.Y, v.X, v.Y));
            }
        }

        // Right: click deletes, drag pans. Middle: drag pans.
        if (mouse.WasPressed(MouseButton.Right) && !overUi) { rightDown = true; rightStartScreen = pos; panning = false; }
        if (mouse.WasPressed(MouseButton.Middle) && !overUi) { middleDown = true; panning = true; }

        if (rightDown && !panning && Vector2.Distance(pos, rightStartScreen) > PanThresholdPixels) panning = true;
        if ((rightDown || middleDown) && panning && (mouse.IsDown(MouseButton.Right) || mouse.IsDown(MouseButton.Middle)))
        {
            TakeManualCamera();
            // Keep the world point that was grabbed under the pointer
            var grab = Camera.ScreenToWorld(pos - mouse.Delta);
            var now = Camera.ScreenToWorld(pos);
            manualCentre += grab - now;
        }

        if (rightDown && !mouse.IsDown(MouseButton.Right))
        {
            if (!panning)
            {
                var w = Camera.ScreenToWorld(pos);
                float pad = manualCamera ? manualSpan / MathF.Max(1.0f, Engine.WindowManager.ViewportSize.Y) * 6.0f : 0.05f;
                simActions.Enqueue(() => sim.RemoveAt(sim.FindBody(w.X, w.Y, pad)));
            }
            rightDown = false; panning = false;
        }
        if (middleDown && !mouse.IsDown(MouseButton.Middle)) { middleDown = false; panning = false; }
    }
}
