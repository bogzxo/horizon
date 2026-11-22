using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

using ThreeBody.Simulation;

using Button = Horizon.UI.Components.Button;

namespace ThreeBody.UI;

/// <summary>Preset buttons, pause, speed slider and the energy readout. Callbacks only set intents, the scene applies them.</summary>
public sealed class Hud
{
    public Action<Preset>? OnPreset;
    public Action? OnTogglePause;
    public Action? OnReset;
    public Action<double>? OnSpeed;

    private readonly Button pauseButton;
    private readonly Label readout;
    private readonly Label speedLabel;
    private readonly Slider speedSlider;
    public const float MinSpeedLog2 = -2.0f, MaxSpeedLog2 = 7.0f;   // 0.25x .. 128x
    private readonly StackPanel inspector;
    private readonly Label swatch;
    private readonly Label inspectorText;
    private readonly Label scenarioText;

    // The panel is two columns and the gap between them inside its padding, 298 wide in all
    private const float PADDING = 12.0f;
    private const float GAP = 6.0f;
    private const float COLUMN = 134.0f;

    private static StackPanel Row() => new() { Direction = UIDirection.Horizontal, Spacing = GAP };

    private static Button Cell(string label, Action pressed) => new(label) { Size = new Vector2(COLUMN, 0), OnPressed = pressed };

    public Hud(UICompositor ui)
    {
        var module = ui.CreateModule();

        var panel = module.AddComponent(new StackPanel
        {
            Anchor = Origin.TopLeft,
            Position = new Vector2(16, -16),
            Color = new Vector4(0.05f, 0.07f, 0.12f, 0.72f),
            Radius = 12.0f,
            Padding = new UIEdges(PADDING),
            Spacing = GAP,
            Stretch = true
        });

        panel.Add(new Label("Three Body") { TextScale = 0.4f });

        // The presets two to a row, every button one column wide so the rows line up as a grid
        (string Label, Preset Preset)[] presets =
        [
            ("1 Eight", Preset.FigureEight),
            ("2 Lagrange", Preset.Lagrange),
            ("3 Chaos", Preset.Chaotic),
            ("4 Sun/Moon", Preset.SunPlanetMoon),
            ("5 Pythag", Preset.Pythagorean),
            ("6 Binary", Preset.BinaryPlanet),
            ("7 Butterfly", Preset.Butterfly),
            ("8 Sitnikov", Preset.Sitnikov)
        ];

        StackPanel? row = null;
        for (int i = 0; i < presets.Length; i++)
        {
            if (i % 2 == 0)
                row = panel.Add(Row());

            Preset preset = presets[i].Preset;
            row!.Add(Cell(presets[i].Label, () => OnPreset?.Invoke(preset)));
        }

        var actions = panel.Add(Row());
        pauseButton = actions.Add(Cell("Pause", () => OnTogglePause?.Invoke()));
        actions.Add(Cell("Reset (R)", () => OnReset?.Invoke()));

        speedLabel = panel.Add(new Label("Speed 1.00x") { TextScale = 0.25f });
        // Log scale: the slider holds log2 of the speed, 0.25x (-2) to 128x (7)
        speedSlider = panel.Add(new Slider
        {
            Min = MinSpeedLog2,
            Max = MaxSpeedLog2,
            Step = 0.25f,
            Value = 0.0f,
            Size = new Vector2(COLUMN * 2 + GAP, 20),
            OnChanged = v => OnSpeed?.Invoke(Math.Pow(2.0, v))
        });

        // Inspector panel, top right, empty and invisible until a body is selected
        inspector = module.AddComponent(new StackPanel
        {
            Anchor = Origin.TopRight,
            Position = new Vector2(-16, -16),
            Color = new Vector4(0, 0, 0, 0),
            Radius = 12.0f,
            Padding = new UIEdges(PADDING),
            Spacing = GAP,
            Stretch = true
        });
        swatch = inspector.Add(new Label("") { TextScale = 0.3f });
        inspectorText = inspector.Add(new Label("") { TextScale = 0.25f });

        readout = module.AddComponent(new Label
        {
            Anchor = Origin.BottomLeft,
            Position = new Vector2(16, 16),
            TextScale = 0.25f
        });

        scenarioText = module.AddComponent(new Label
        {
            Anchor = Origin.TopRight,
            Position = new Vector2(-16, -160),
            TextScale = 0.25f
        });

        module.AddComponent(new Label(
            "S: save scenario   L: scenario list\n" +
            "Left drag: throw a new body\n" +
            "Right click: delete a body\n" +
            "Wheel: zoom   Right/middle drag: pan\n" +
            "F: fit all   Space: pause   .: step\n" +
            "[ ]: halve / double speed (0.25x to 128x)\n" +
            "I: integrator   M: collisions   C: centre\n" +
            "V: velocity   G: grid   K: centre mark\n" +
            "T: reverse time (Verlet retraces its path)")
        {
            Anchor = Origin.BottomRight,
            Position = new Vector2(-16, 16),
            TextScale = 0.22f
        });
    }

    // Energy drift history for the sparkline, one sample per refresh, reset with the simulation
    private const int HistoryLength = 300;
    private const float SparkWidthPixels = 300.0f, SparkHeightPixels = 44.0f, SparkMarginPixels = 350.0f, SparkAboveReadoutPixels = 16.0f;   // right of the left panel
    private readonly Queue<float> history = new();
    private int lastResetCount = -1;

    /// <summary>Draws the energy drift history as a small line graph above the readout (world units from the camera, so it stays fixed on screen).</summary>
    public void DescribeSparkline(Horizon.Rendering.Primitives.ShapeList shapes, Vector2 centre, float span, Vector2 viewport)
    {
        if (history.Count < 2) return;
        float upp = span / MathF.Max(1.0f, viewport.Y);
        Vector2 ToWorld(float px, float py) => centre + new Vector2(px - viewport.X * 0.5f, py - viewport.Y * 0.5f) * upp;

        float min = float.MaxValue, max = float.MinValue;
        foreach (var v in history) { min = MathF.Min(min, v); max = MathF.Max(max, v); }
        float range = MathF.Max(max - min, 1e-9f);

        float left = SparkMarginPixels, bottom = SparkAboveReadoutPixels;
        shapes.Line(ToWorld(left, bottom), ToWorld(left + SparkWidthPixels, bottom), upp, new Vector4(1, 1, 1, 0.25f));
        Vector2? prev = null;
        int i = 0;
        foreach (var v in history)
        {
            float x = left + SparkWidthPixels * i++ / (HistoryLength - 1);
            float y = bottom + SparkHeightPixels * (v - min) / range;
            var p = ToWorld(x, y);
            if (prev is { } q) shapes.Line(q, p, upp * 1.5f, new Vector4(0.45f, 0.9f, 1.0f, 0.9f));
            prev = p;
        }
    }

    /// <summary>Mass, speed, distance to the centre of mass and colour swatch of the selected body, nothing when null.</summary>
    public void ShowInspector(NBodySystem sim, Body? body)
    {
        if (body == null)
        {
            inspector.Color = new Vector4(0, 0, 0, 0);
            swatch.Text = "";
            inspectorText.Text = "";
            return;
        }

        double m = 0, cx = 0, cy = 0;
        foreach (var b in sim.Bodies) { m += b.Mass; cx += b.Mass * b.X; cy += b.Mass * b.Y; }
        if (m > 0) { cx /= m; cy /= m; }
        double dist = Math.Sqrt((body.X - cx) * (body.X - cx) + (body.Y - cy) * (body.Y - cy));
        double speed = Math.Sqrt(body.VX * body.VX + body.VY * body.VY);

        inspector.Color = new Vector4(0.05f, 0.07f, 0.12f, 0.72f);
        swatch.Color = new Vector4(body.Color.X, body.Color.Y, body.Color.Z, 1.0f);
        swatch.Text = $"████ {body.Name}";
        inspectorText.Text = $"Mass {body.Mass:0.000}\nSpeed {speed:0.000}\nDist to centre of mass {dist:0.000}\nEsc: deselect";
    }

    /// <summary>The scenario list, numbered, below the left panel; null hides it. status is a one line message under the list.</summary>
    public void ShowScenarios(IReadOnlyList<string>? names, string status)
    {
        if (names == null)
        {
            scenarioText.Text = status;
            return;
        }

        var text = new System.Text.StringBuilder("Scenarios (press the number to load, L closes):\n");
        if (names.Count == 0) text.Append("  none saved yet, press S\n");
        for (int i = 0; i < names.Count && i < 9; i++) text.Append($"  {i + 1}  {names[i]}\n");
        text.Append(status);
        scenarioText.Text = text.ToString();
    }

    public void Refresh(NBodySystem sim)
    {
        if (sim.ResetCount != lastResetCount) { history.Clear(); lastResetCount = sim.ResetCount; }
        if (!sim.Paused)
        {
            history.Enqueue((float)sim.EnergyDrift);
            while (history.Count > HistoryLength) history.Dequeue();
        }
        pauseButton.Label = sim.Paused ? "Resume" : "Pause";
        speedLabel.Text = $"Speed {sim.TimeScale:0.##}x  ([ ] halve/double)";
        float log2 = Math.Clamp(MathF.Log2((float)sim.TimeScale), MinSpeedLog2, MaxSpeedLog2);
        if (MathF.Abs(speedSlider.Value - log2) > 1e-3f) speedSlider.Value = log2;
        string limited = sim.SpeedLimited ? "   SPEED LIMITED BY CPU" : "";
        string steps = $"Steps/tick {sim.LastSteps}   dt {sim.LastStepDt:0.00000}{limited}\n";
        string rebase = sim.Rebaselines > 0 ? $"   (baseline reset x{sim.Rebaselines})" : "";
        readout.Text = steps + $"{sim.CurrentPreset}   t = {sim.Time:0.00}   bodies {sim.Bodies.Count}\n" +
                       $"Energy {sim.Energy:0.00000}   drift {sim.EnergyDrift * 100.0:+0.0000;-0.0000}%{rebase}\n" +
                       $"Integrator: {(sim.Integrator == Integrator.Yoshida4 ? "Yoshida 4th order" : "Velocity Verlet")}   Collisions: {sim.Collisions}";
    }
}
