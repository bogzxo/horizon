using System.Numerics;

using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// Light on the UI, soft pools of it that follow something about (the caret, the pointer) and rings that spread out of a
/// point and fade (a click). A light is asked for every tick it should shine with <see cref="Shine"/>, by a key of the
/// program's, and eases after where it's asked to be, fading in the first time and out once nobody asks any more, so
/// the program never has to say when it's done with one. A fixed few of each, nothing made as they come and go, and with
/// none lit and no rings going nothing is painted at all. Like <see cref="Sparks"/> it's drawn anywhere on screen whatever
/// its bounds and the pointer goes through it, so it goes in a module of its own that isn't interactive.
/// <code>
/// lights.Shine(1, editor.CaretPoint, 90, skin.AccentColor with { W = 0.18f });
/// lights.Ripple(compositor.Pointer.Position, skin.AccentColor);
/// </code>
/// </summary>
public class Lights : UIComponent
{
    private const int MOST_LIGHTS = 16, MOST_RIPPLES = 24;

    // Keys of the parts, the lights and the rings apart, so a frame between two ticks blends each with itself
    private const int RIPPLE_PART = 1 << 20;

    private struct Light
    {
        public int Key;
        public Vector2 At, Target;
        public float Radius, Level;
        public Vector4 Color;
        public bool Asked;
    }

    private struct Ring
    {
        public Vector2 At;
        public float Age, Life, Reach, Thickness;
        public Vector4 Color;
        public int Id;
    }

    private readonly Light[] lights = new Light[MOST_LIGHTS];
    private readonly Ring[] rings = new Ring[MOST_RIPPLES];
    private int lightCount, ringCount, nextRing;

    /// <summary>How quickly a light gets to where it's asked to be, as the rate of an exponential, 0 for at once.</summary>
    public float Follow { get; set; } = 20.0f;

    /// <summary>How quickly a light fades in and out, as the rate of an exponential.</summary>
    public float Fade { get; set; } = 10.0f;

    /// <summary>Whether anything is lit or spreading, nothing is painted when not.</summary>
    public bool Lit => lightCount > 0 || ringCount > 0;

    protected override bool HitTestVisible => false;

    protected override Vector2 Measure(UISkin skin) => Vector2.Zero;

    /// <summary>
    /// A light of the program's, by its key, shining at a point this tick, as far round as the radius and as strong as the
    /// colour's alpha. Ask again next tick to keep it lit, it fades out when nobody does.
    /// </summary>
    public void Shine(int key, Vector2 at, float radius, Vector4 color)
    {
        for (int i = 0; i < lightCount; i++)
        {
            ref Light light = ref lights[i];
            if (light.Key != key)
                continue;

            light.Target = at;
            light.Radius = radius;
            light.Color = color;
            light.Asked = true;
            return;
        }

        if (lightCount == MOST_LIGHTS)
            return;

        lights[lightCount++] = new Light { Key = key, At = at, Target = at, Radius = radius, Color = color, Asked = true };
    }

    /// <summary>A ring out of a point that spreads as far as <paramref name="reach"/> and fades over <paramref name="life"/> seconds.</summary>
    public void Ripple(Vector2 at, Vector4 color, float reach = 48.0f, float life = 0.45f, float thickness = 2.0f)
    {
        // A full pool drops the oldest, the first, rather than the new one
        if (ringCount == MOST_RIPPLES)
        {
            Array.Copy(rings, 1, rings, 0, MOST_RIPPLES - 1);
            ringCount--;
        }

        nextRing = (nextRing + 1) & 0xFFFF;
        rings[ringCount++] = new Ring { At = at, Life = MathF.Max(0.05f, life), Reach = reach, Thickness = thickness, Color = color, Id = nextRing };
    }

    /// <summary>Every light out and every ring gone at once.</summary>
    public void Clear()
    {
        lightCount = 0;
        ringCount = 0;
    }

    protected override void Update(float dt)
    {
        float follow = Follow > 0.0f ? 1.0f - MathF.Exp(-Follow * dt) : 1.0f;
        float fade = 1.0f - MathF.Exp(-Fade * dt);

        int kept = 0;
        for (int i = 0; i < lightCount; i++)
        {
            ref Light light = ref lights[i];
            light.At += (light.Target - light.At) * follow;
            light.Level += ((light.Asked ? 1.0f : 0.0f) - light.Level) * fade;
            if (light.Asked && light.Level > 0.995f)
                light.Level = 1.0f;
            if (Vector2.DistanceSquared(light.At, light.Target) < 0.01f)
                light.At = light.Target;

            // Out once it's faded all the way and nobody asked for it this tick
            bool keep = light.Asked || light.Level > 0.01f;
            light.Asked = false;
            if (keep)
                lights[kept++] = light;
        }

        lightCount = kept;

        int alive = 0;
        for (int i = 0; i < ringCount; i++)
        {
            ref Ring ring = ref rings[i];
            ring.Age += dt;
            if (ring.Age < ring.Life)
                rings[alive++] = ring;
        }

        ringCount = alive;
    }

    protected override void Paint(UIDrawList list)
    {
        if (!Lit)
            return;

        // A pool of light, a disc of it with an edge that fades all the way out, the middle a third of it
        for (int i = 0; i < lightCount; i++)
        {
            ref Light light = ref lights[i];
            list.BeginPart(light.Key & (RIPPLE_PART - 1));
            float core = light.Radius * 0.3f;
            var disc = new UIRect(light.At - new Vector2(core), light.At + new Vector2(core));
            list.Shadow(disc, core, light.Radius - core, light.Color with { W = light.Color.W * light.Level });
        }

        // A ring that eases out as it goes, quick at first, and thins and fades on the way
        for (int i = 0; i < ringCount; i++)
        {
            ref Ring ring = ref rings[i];
            list.BeginPart(RIPPLE_PART | ring.Id);
            float t = ring.Age / ring.Life;
            float eased = 1.0f - (1.0f - t) * (1.0f - t) * (1.0f - t);
            float radius = MathF.Max(1.0f, ring.Reach * eased);
            float thickness = MathF.Max(0.75f, ring.Thickness * (1.0f - t * 0.6f));
            var round = new UIRect(ring.At - new Vector2(radius), ring.At + new Vector2(radius));
            list.RoundOutline(round, radius, thickness, ring.Color with { W = ring.Color.W * (1.0f - t) * (1.0f - t) });
        }

        list.EndParts();
    }
}
