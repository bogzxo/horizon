using System.Numerics;

using Horizon.Rendering.Primitives;

using ThreeBody.Simulation;

namespace ThreeBody.Rendering;

/// <summary>Records trails and writes the backdrop, trails, glowing bodies and overlays down as shapes. All on the simulation thread.</summary>
public sealed class BodyRenderer
{
    private const int TrailLength = 600;
    private const int StarCount = 140;

    /// <summary>World length of a velocity arrow per unit of speed, the throw preview uses the same so a drag matches the arrow it makes.</summary>
    public const float VelocityArrowScale = 0.4f;

    // One trail per body object, so adding, deleting or merging bodies leaves the others' trails alone
    private readonly Dictionary<Body, Trail> trails = new();
    private readonly List<Body> stale = new();
    private readonly Vector4[] stars;   // x, y in 0..1, z depth (parallax), w size/brightness
    private int frame;

    /// <summary>Velocity vectors on bodies.</summary>
    public bool ShowVelocity { get; set; } = true;

    /// <summary>Marker at the centre of mass.</summary>
    public bool ShowCentreOfMass { get; set; } = true;

    /// <summary>Faint grid behind the stars.</summary>
    public bool ShowGrid { get; set; } = true;

    public BodyRenderer()
    {
        var rng = new Random(7);
        stars = new Vector4[StarCount];
        for (int i = 0; i < StarCount; i++)
            stars[i] = new Vector4(rng.NextSingle(), rng.NextSingle(), 0.05f + 0.35f * rng.NextSingle(), rng.NextSingle());
    }

    public void Record(NBodySystem sim)
    {
        // Bodies that are gone (deleted, merged away, preset reset) lose their trail, new ones get a fresh one
        stale.Clear();
        foreach (var key in trails.Keys)
            if (!Contains(sim, key)) stale.Add(key);
        foreach (var key in stale) trails.Remove(key);
        foreach (var b in sim.Bodies)
            if (!trails.ContainsKey(b)) trails[b] = new Trail(TrailLength);

        // One point every other tick keeps the buffer covering a decent stretch of orbit
        if ((frame++ & 1) != 0 || sim.Paused) return;

        foreach (var b in sim.Bodies)
            trails[b].Add(new Vector2((float)b.X, (float)b.Y));
    }

    /// <summary>Forget all trails, after something moved every body at once.</summary>
    public void ClearTrails() => trails.Clear();

    private static bool Contains(NBodySystem sim, Body body)
    {
        for (int i = 0; i < sim.Bodies.Count; i++)
            if (ReferenceEquals(sim.Bodies[i], body)) return true;
        return false;
    }

    /// <param name="centre">World point at the middle of the view.</param>
    /// <param name="span">World units across the shorter screen side.</param>
    public void Describe(ShapeList shapes, NBodySystem sim, Vector2 centre, float span, Vector2 window)
    {
        float upp = span / MathF.Max(1.0f, window.Y);
        float aspect = window.X / MathF.Max(1.0f, window.Y);

        DrawBackdrop(shapes, centre, span, aspect, upp);
        DrawTrails(shapes, sim, upp);
        DrawBodies(shapes, sim, upp);

        if (ShowCentreOfMass) DrawCentreOfMass(shapes, sim, upp);
    }

    private void DrawBackdrop(ShapeList shapes, Vector2 cam, float span, float aspect, float upp)
    {
        float w = span * aspect * 1.1f, h = span * 1.1f;

        if (ShowGrid)
        {
            // Grid lines scale with zoom by snapping spacing to a power of two, and drift at half camera speed
            float spacing = MathF.Pow(2.0f, MathF.Round(MathF.Log2(span / 8.0f)));
            const float parallax = 0.5f;
            var shift = cam * (1.0f - parallax);   // the grid layer lags behind the camera
            var line = new Vector4(0.35f, 0.45f, 0.7f, 0.07f);
            int kx0 = (int)MathF.Floor((cam.X - w * 0.5f - shift.X) / spacing);
            int kx1 = (int)MathF.Ceiling((cam.X + w * 0.5f - shift.X) / spacing);
            for (int k = kx0; k <= kx1; k++)
            {
                float sx = k * spacing + shift.X;
                shapes.Line(new Vector2(sx, cam.Y - h * 0.5f), new Vector2(sx, cam.Y + h * 0.5f), upp, line);
            }
            int ky0 = (int)MathF.Floor((cam.Y - h * 0.5f - shift.Y) / spacing);
            int ky1 = (int)MathF.Ceiling((cam.Y + h * 0.5f - shift.Y) / spacing);
            for (int k = ky0; k <= ky1; k++)
            {
                float sy = k * spacing + shift.Y;
                shapes.Line(new Vector2(cam.X - w * 0.5f, sy), new Vector2(cam.X + w * 0.5f, sy), upp, line);
            }
        }

        foreach (var s in stars)
        {
            // Nearer (larger depth) stars slide more as the camera moves, wrapped around the view
            float u = s.X - cam.X * s.Z / span;
            float v = s.Y - cam.Y * s.Z / span;
            u -= MathF.Floor(u);
            v -= MathF.Floor(v);
            var p = new Vector2(cam.X + (u - 0.5f) * w, cam.Y + (v - 0.5f) * h);
            float size = upp * (0.8f + 1.6f * s.W);
            float a = 0.25f + 0.55f * s.W;
            shapes.FillCircle(p, size, new Vector4(0.8f, 0.85f, 1.0f, a));
        }
    }

    private void DrawTrails(ShapeList shapes, NBodySystem sim, float upp)
    {
        float width = upp * 3.0f;

        foreach (var body in sim.Bodies)
        {
            var c = body.Color;
            if (!trails.TryGetValue(body, out var trail)) continue;
            for (int i = 1; i < trail.Count; i++)
            {
                float age = (float)i / trail.Count;   // 0 old .. 1 new
                float fade = age * age;
                // Soft halo under a thinner bright core, both thinning and fading with age
                shapes.Line(trail[i - 1], trail[i], width * (0.2f + 0.8f * age) * 2.2f, new Vector4(c.X, c.Y, c.Z, fade * 0.18f));
                shapes.Line(trail[i - 1], trail[i], width * (0.2f + 0.8f * age),
                    new Vector4(Mix(c.X, 1, age * 0.35f), Mix(c.Y, 1, age * 0.35f), Mix(c.Z, 1, age * 0.35f), fade * 0.9f));
            }
        }
    }

    private void DrawBodies(ShapeList shapes, NBodySystem sim, float upp)
    {
        foreach (var body in sim.Bodies)
        {
            // Sized by mass, cube root keeps heavy bodies from swallowing the screen
            float radius = MathF.Max(0.03f, 0.05f * MathF.Cbrt((float)body.Mass));
            var p = new Vector2((float)body.X, (float)body.Y);
            var c = body.Color;

            // Glow: concentric discs with falling alpha
            const int Layers = 7;
            for (int i = Layers; i >= 1; i--)
            {
                float t = (float)i / Layers;
                shapes.FillCircle(p, radius * (1.0f + 3.2f * t * t), new Vector4(c.X, c.Y, c.Z, 0.05f + 0.03f * (1.0f - t)));
            }

            shapes.FillCircle(p, radius, new Vector4(c.X, c.Y, c.Z, 1.0f));
            shapes.FillCircle(p, radius * 0.55f, new Vector4(Mix(c.X, 1, 0.7f), Mix(c.Y, 1, 0.7f), Mix(c.Z, 1, 0.7f), 1.0f));

            if (ShowVelocity)
            {
                var v = new Vector2((float)body.VX, (float)body.VY);
                var tip = p + v * VelocityArrowScale;
                if (v.LengthSquared() > 1e-8f)
                {
                    var colour = new Vector4(c.X, c.Y, c.Z, 0.85f);
                    shapes.Line(p, tip, upp * 2.0f, colour);
                    var dir = Vector2.Normalize(v);
                    var side = new Vector2(-dir.Y, dir.X);
                    float head = upp * 9.0f;
                    shapes.Line(tip, tip - dir * head + side * head * 0.5f, upp * 2.0f, colour);
                    shapes.Line(tip, tip - dir * head - side * head * 0.5f, upp * 2.0f, colour);
                }
            }
        }
    }

    private static void DrawCentreOfMass(ShapeList shapes, NBodySystem sim, float upp)
    {
        double m = 0, x = 0, y = 0;
        foreach (var b in sim.Bodies) { m += b.Mass; x += b.Mass * b.X; y += b.Mass * b.Y; }
        if (m <= 0) return;

        var p = new Vector2((float)(x / m), (float)(y / m));
        var col = new Vector4(1.0f, 1.0f, 1.0f, 0.7f);
        float arm = upp * 9.0f;
        shapes.Circle(p, arm * 0.6f, upp * 1.5f, col);
        shapes.Line(p - new Vector2(arm, 0), p + new Vector2(arm, 0), upp * 1.5f, col);
        shapes.Line(p - new Vector2(0, arm), p + new Vector2(0, arm), upp * 1.5f, col);
    }

    private static float Mix(float a, float b, float t) => a + (b - a) * t;
}
