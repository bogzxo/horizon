using System.Numerics;

using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// A few sparks thrown off a point of the UI, the power mode people install in their editors on purpose, and a burst for
/// when something went right (a save, a build). They're little round embers with a glow round them that fly out, fall
/// and fade, white hot at first and the colour they were thrown in as they cool, a fixed pool of them, so throwing them
/// makes no garbage. They're drawn anywhere on screen whatever the bounds, nothing hits them and the
/// pointer goes through, so put it last in a module of its own that isn't interactive, over everything.
/// <code>
/// sparks.Emit(editor.CaretPoint, 4, skin.AccentColor);
/// sparks.Burst(point, 40, skin.AccentColor);
/// </code>
/// </summary>
public class Sparks : UIComponent
{
    private const int MOST = 384;

    private struct Spark
    {
        public Vector2 Position, Velocity;
        public float Age, Life, Size;
        public Vector4 Color;

        // What it's called from one tick to the next. Keyed by where it was in the pool, every spark after one that
        // went out was taken for its neighbour for a tick and blended half way to it, a streak across the burst
        public int Id;
    }

    private readonly Spark[] sparks = new Spark[MOST];
    private readonly Random random = new();
    private int count, nextId;

    /// <summary>How big the glow round a spark is, times the spark, 0 for none. Two more quads a spark.</summary>
    public float Glow { get; set; } = 3.0f;

    /// <summary>How hard they fall, in UI pixels a second every second.</summary>
    public float Gravity { get; set; } = 900.0f;

    /// <summary>How many are flying right now.</summary>
    public int Count => count;

    protected override bool HitTestVisible => false;

    protected override Vector2 Measure(UISkin skin) => Vector2.Zero;

    /// <summary>A few sparks off a point, up and out, gone in about <paramref name="life"/> seconds.</summary>
    public void Emit(Vector2 at, int many, Vector4 color, float speed = 160.0f, float life = 0.45f, float size = 3.0f)
    {
        for (int i = 0; i < many; i++)
        {
            // Mostly upwards, a fan of a hundred and forty degrees or so, Y goes up in the UI
            float angle = MathF.PI * 0.5f + (random.NextSingle() - 0.5f) * 2.4f;
            float fast = speed * (0.5f + random.NextSingle());
            Add(at, new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * fast, color, life * (0.6f + random.NextSingle() * 0.8f), size * (0.6f + random.NextSingle() * 0.8f));
        }
    }

    /// <summary>A ring of sparks out of a point every which way, for something done.</summary>
    public void Burst(Vector2 at, int many, Vector4 color, float speed = 320.0f, float life = 0.7f, float size = 4.0f)
    {
        for (int i = 0; i < many; i++)
        {
            float angle = random.NextSingle() * MathF.Tau;
            float fast = speed * (0.35f + random.NextSingle() * 0.65f);
            Add(at, new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * fast, color, life * (0.6f + random.NextSingle() * 0.8f), size * (0.6f + random.NextSingle() * 0.8f));
        }
    }

    /// <summary>Every spark gone at once.</summary>
    public void Clear() => count = 0;

    private void Add(Vector2 at, Vector2 velocity, Vector4 color, float life, float size)
    {
        // A full pool drops the oldest, the first, rather than the new one, which is what somebody just did
        if (count == MOST)
        {
            Array.Copy(sparks, 1, sparks, 0, MOST - 1);
            count--;
        }

        nextId = (nextId + 1) & 0xFFFFF;
        sparks[count++] = new Spark { Position = at, Velocity = velocity, Life = MathF.Max(0.05f, life), Size = size, Color = color, Id = nextId };
    }

    protected override void Update(float dt)
    {
        int kept = 0;
        for (int i = 0; i < count; i++)
        {
            ref Spark spark = ref sparks[i];
            spark.Age += dt;
            if (spark.Age >= spark.Life)
                continue;

            spark.Velocity.Y -= Gravity * dt;
            spark.Velocity *= MathF.Max(0.0f, 1.0f - 2.5f * dt);
            spark.Position += spark.Velocity * dt;
            sparks[kept++] = spark;
        }

        count = kept;
    }

    protected override void Paint(UIDrawList list)
    {
        if (count == 0)
            return;

        // A part a spark by its name, so a frame between two ticks blends every spark with itself
        for (int i = 0; i < count; i++)
        {
            ref Spark spark = ref sparks[i];
            list.BeginPart(spark.Id);

            float left = 1.0f - spark.Age / spark.Life;
            float radius = spark.Size * (0.4f + 0.6f * left) * 0.5f;
            Vector2 middle = spark.Position;

            // White hot as it's thrown, its own colour as it cools
            Vector4 core = Vector4.Lerp(spark.Color, Vector4.One, 0.65f * left * left) with { W = spark.Color.W * left };

            if (Glow > 0.0f)
            {
                float halo = radius * Glow;
                list.RoundRect(new UIRect(middle - new Vector2(halo), middle + new Vector2(halo)), halo, spark.Color with { W = spark.Color.W * left * 0.16f });
            }

            list.RoundRect(new UIRect(middle - new Vector2(radius), middle + new Vector2(radius)), radius, core);
        }
        list.EndParts();
    }
}
