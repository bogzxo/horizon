using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

using ThreeBody.Simulation;

namespace ThreeBody.UI;

/// <summary>Body names drawn as small labels next to the bodies, a pool of labels moved every frame.</summary>
public sealed class NameLabels
{
    private readonly UICompositor ui;
    private readonly List<Label> labels = new();
    private readonly UIModule module;

    public bool Visible { get; set; } = true;

    public NameLabels(UICompositor ui)
    {
        this.ui = ui;
        module = ui.CreateModule();
    }

    /// <summary>Place one label per body. Positions are screen pixels from the bottom left, like the sparkline uses.</summary>
    public void Update(NBodySystem sim, Vector2 centre, float span, Vector2 viewport)
    {
        float upp = span / MathF.Max(1.0f, viewport.Y);
        var bodies = sim.Bodies;

        while (labels.Count < bodies.Count)
        {
            var label = new Label("") { Anchor = Origin.TopLeft, TextScale = 0.2f };
            module.AddComponent(label);
            labels.Add(label);
        }

        for (int i = 0; i < labels.Count; i++)
        {
            var label = labels[i];
            if (!Visible || i >= bodies.Count) { label.Visible = false; continue; }

            var b = bodies[i];
            float radius = MathF.Max(0.03f, 0.05f * MathF.Cbrt((float)b.Mass));
            var world = new Vector2((float)b.X + radius * 1.4f, (float)b.Y + radius * 1.4f);
            float px = viewport.X * 0.5f + (world.X - centre.X) / upp;
            float py = viewport.Y * 0.5f + (world.Y - centre.Y) / upp;   // from the bottom
            label.Visible = true;
            label.Text = b.Name;
            label.Color = new Vector4(b.Color.X * 0.5f + 0.5f, b.Color.Y * 0.5f + 0.5f, b.Color.Z * 0.5f + 0.5f, 0.95f);
            label.Position = new Vector2(px, py - viewport.Y);
        }
    }
}
