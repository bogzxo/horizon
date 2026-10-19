using System.Numerics;

namespace Horizon.Rendering.PostProcessing;

/// <summary>
/// A glow round whatever is bright. What is over the <see cref="Threshold"/> is taken out of the picture and
/// shrunk by halves a few times over (<see cref="Levels"/>), each level a careful blur of the one before so that
/// nothing shimmers, then blown back up level by level with every size added in, which is a glow that is wide
/// and soft and still tight round the thing itself, and laid over the picture by <see cref="Intensity"/>. A lamp,
/// a window at night, a screen, the sun on water, anything the lighting drives past white gets a halo the way it
/// does on a camera.
/// <code>
/// world.PostProcessing.Add(new BloomEffect { Threshold = 0.7f, Intensity = 0.35f });
/// </code>
/// It costs one pass a level each way at a half, a quarter, an eighth of the picture and so on, which comes to
/// about a third of a full size pass altogether. It goes before an <see cref="ExposureEffect"/>, so the glow is
/// rolled off with the rest, and before a tube. Nothing is allocated a frame, the levels are made once and made
/// anew when the picture changes size.
/// </summary>
public sealed class BloomEffect : PostEffect
{
    private const string UNIFORM_TEXEL = "uTexel";
    private const string UNIFORM_THRESHOLD = "uThreshold";
    private const string UNIFORM_FIRST = "uFirst";
    private const string UNIFORM_RADIUS = "uRadius";
    private const string UNIFORM_INTENSITY = "uIntensity";

    /// <summary>The most levels there can be, a picture halved this often is a few pixels across.</summary>
    public const int MOST_LEVELS = 8;

    private PostTechnique down = null!, up = null!, mix = null!;
    private readonly PostTarget?[] downs = new PostTarget?[MOST_LEVELS];
    private readonly PostTarget?[] ups = new PostTarget?[MOST_LEVELS];

    /// <summary>What is darker than this (by its brightest channel, 0 to 1) doesn't glow. Softly, there is no hard edge at it.</summary>
    public float Threshold { get; set; } = 0.7f;

    /// <summary>How much of the glow is laid over the picture, 0 for none.</summary>
    public float Intensity { get; set; } = 0.35f;

    /// <summary>How many times the picture is halved, which is how wide the glow is. Five reaches a good way, eight is the most.</summary>
    public int Levels { get; set; } = 5;

    /// <summary>How far the taps reach on the way back up, in pixels of each level. 1 is the usual, more is softer and may show gaps.</summary>
    public float Radius { get; set; } = 1.0f;

    protected override void Initialize()
    {
        down = new PostTechnique("bloom_down");
        up = new PostTechnique("bloom_up");
        mix = new PostTechnique("bloom_mix");
    }

    protected override void Render(PostContext context)
    {
        int levels = Math.Clamp(Levels, 1, MOST_LEVELS);

        // The way down, every level half the one before, the first keeping only what is bright
        Vector2 sourceSize = context.SourceSize;
        Graphics.Texture source = context.Source;
        uint width = (uint)sourceSize.X, height = (uint)sourceSize.Y;

        down.Bind();
        for (int level = 0; level < levels; level++)
        {
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
            PostTarget target = Fit(downs, level, width, height);

            Vector2 texel = Vector2.One / sourceSize;
            down.SetUniform(UNIFORM_TEXEL, in texel);
            down.SetUniform(UNIFORM_THRESHOLD, Math.Clamp(Threshold, 0.0f, 1.0f));
            down.SetUniform(UNIFORM_FIRST, level == 0);
            source.Bind(0);
            context.Draw(target);

            source = target.Texture;
            sourceSize = target.Size;
        }

        // And back up, each level blown up onto the one of its size from the way down
        PostTarget low = downs[levels - 1]!;
        if (levels > 1)
        {
            up.Bind();
            for (int level = levels - 2; level >= 0; level--)
            {
                PostTarget same = downs[level]!;
                PostTarget target = Fit(ups, level, (uint)same.Size.X, (uint)same.Size.Y);

                Vector2 texel = Vector2.One / low.Size;
                up.SetUniform(UNIFORM_TEXEL, in texel);
                up.SetUniform(UNIFORM_RADIUS, MathF.Max(Radius, 0.0f));
                low.Texture.Bind(0);
                same.Texture.Bind(1);
                context.Draw(target);

                low = target;
            }
        }

        // Over the picture
        mix.Bind();
        mix.SetUniform(UNIFORM_INTENSITY, MathF.Max(Intensity, 0.0f));
        context.Source.Bind(0);
        low.Texture.Bind(1);
        context.Draw();
        mix.Unbind();
    }

    /// <summary>Helper method for a level's target at a size, made when there is none or it is another size.</summary>
    private static PostTarget Fit(PostTarget?[] targets, int level, uint width, uint height)
    {
        if (targets[level] is { } existing && existing.Fits(width, height)) return existing;

        targets[level]?.Dispose();
        return targets[level] = new PostTarget(width, height, PostTarget.Precise);
    }

    public override void Dispose()
    {
        for (int i = 0; i < MOST_LEVELS; i++)
        {
            downs[i]?.Dispose();
            ups[i]?.Dispose();
            downs[i] = ups[i] = null;
        }

        base.Dispose();
    }
}
