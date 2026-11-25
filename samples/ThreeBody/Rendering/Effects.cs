using System.Numerics;

using Horizon.Rendering.Primitives;

using ThreeBody.Simulation;

namespace ThreeBody.Rendering;

/// <summary>Short flashes and expanding rings where bodies collide.</summary>
public sealed class Effects
{
    private const float Lifetime = 0.9f;        // seconds an effect lives
    private const float FlashPart = 0.25f;      // share of the lifetime the flash is visible
    private const float RepeatWindow = 0.15f;   // seconds in which a nearby repeat contact is ignored
    private const float RepeatReach = 3.0f;     // in sizes of the new contact
    private const int MaxActive = 64;
    private const int RingSegments = 48;

    private struct Effect
    {
        public float Age;
        public Vector2 Pos;
        public float Size;
        public bool Merged;
    }

    private readonly List<Effect> _active = new();

    /// <summary>Takes new collisions from the simulation, ages the active effects by dt (real seconds) and drops the finished ones.</summary>
    public void Drain(NBodySystem sim, float dt)
    {
        // Age first, so an effect added below starts at age zero
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var e = _active[i];
            e.Age += dt;
            if (e.Age >= Lifetime) _active.RemoveAt(i);
            else _active[i] = e;
        }

        // The queue is always emptied, even when the list is full
        while (sim.CollisionEvents.TryDequeue(out var ev))
        {
            if (_active.Count >= MaxActive) continue;

            var pos = new Vector2((float)ev.X, (float)ev.Y);
            float size = (float)ev.Size;
            if (IsRepeat(pos, size)) continue;

            _active.Add(new Effect { Age = 0.0f, Pos = pos, Size = size, Merged = ev.Merged });
        }
    }

    /// <summary>Writes the flash and ring of every active effect. upp is world units per pixel.</summary>
    public void Describe(ShapeList shapes, float upp)
    {
        foreach (var e in _active)
        {
            float t = Math.Clamp(e.Age / Lifetime, 0.0f, 1.0f);

            // Merges are white-gold, bounces cyan
            var rgb = e.Merged ? new Vector3(1.0f, 0.92f, 0.65f) : new Vector3(0.35f, 0.9f, 1.0f);

            // Bright flash, gone after the first quarter
            if (t < FlashPart)
            {
                float f = 1.0f - t / FlashPart;
                shapes.FillCircle(e.Pos, e.Size * (1.0f + 1.5f * t / FlashPart), new Vector4(1.0f, 1.0f, 1.0f, f * f));
                shapes.FillCircle(e.Pos, e.Size * 2.5f, new Vector4(rgb, f * 0.35f));
            }

            // Ring growing outward and fading
            float radius = e.Size * (0.5f + 5.5f * t);
            var colour = new Vector4(rgb, 1.0f - t);
            float width = upp * 2.5f;
            var prev = e.Pos + new Vector2(radius, 0.0f);
            for (int k = 1; k <= RingSegments; k++)
            {
                float a = k * (MathF.Tau / RingSegments);
                var next = e.Pos + new Vector2(MathF.Cos(a), MathF.Sin(a)) * radius;
                shapes.Line(prev, next, width, colour);
                prev = next;
            }
        }
    }

    // True when a fresh effect already sits close to this spot
    private bool IsRepeat(Vector2 pos, float size)
    {
        float reach = size * RepeatReach;
        foreach (var e in _active)
        {
            if (e.Age < RepeatWindow && Vector2.DistanceSquared(e.Pos, pos) < reach * reach) return true;
        }
        return false;
    }
}
