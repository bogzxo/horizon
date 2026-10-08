using System.Numerics;

using Horizon.Core.Tweening;
using Horizon.Rendering.PostProcessing;

namespace Horizon.Rendering.Transitions;

/// <summary>
/// The old scene rots away. Blotches turn up all over it and spread until there is nothing left, then the new scene comes out from under them the same way.
/// The blotches are Perlin noise with a threshold that moves through it, see shaders/post/rot.frag. A stain creeps ahead of each one and its edge has a crust.
/// <code>
/// engine.SetScene(new FightScene(), new RotTransition { EdgeColor = new Vector3(0.8f, 0.2f, 0.1f) });
/// </code>
/// It never rots the same way twice.
/// </summary>
public sealed class RotTransition : ScreenTransition
{
    private const string UNIFORM_COVER = "uCover";
    private const string UNIFORM_COLOR = "uColor";
    private const string UNIFORM_EDGE_COLOR = "uEdgeColor";
    private const string UNIFORM_CELLS = "uCells";
    private const string UNIFORM_BLOCKS = "uBlocks";
    private const string UNIFORM_SEED = "uSeed";

    // Seeds are picked from zero up to this, which is plenty of different rot
    private const float SEED_RANGE = 64.0f;

    private PostTechnique technique = null!;
    private float seed = NewSeed();
    private bool wasArriving;

    /// <summary>What is left once everything has rotted, which is also what the new scene comes out of.</summary>
    public Vector3 Color { get; set; } = new(0.03f, 0.035f, 0.03f);

    /// <summary>The colour of the crust along the edge of the rot, and (thinner) of the stain ahead of it.</summary>
    public Vector3 EdgeColor { get; set; } = new(0.55f, 0.66f, 0.18f);

    /// <summary>How big a blotch is, as a share of the height of the screen. Smaller for a finer rot with more of them.</summary>
    public float BlotchSize { get; set; } = 0.2f;

    /// <summary>
    /// How many pixels of the screen rot as one, each way. For pixel art that is drawn at twice its size that is 2 (or more for chunkier rot),
    /// and at 1 the rot is as fine as the screen.
    /// </summary>
    public float PixelSize { get; set; } = 4.0f;

    public RotTransition()
    {
        // The noise does the easing, the threshold just walks through it
        OutTime = 0.45f;
        InTime = 0.5f;
        OutEasing = Easing.Linear;
        InEasing = Easing.Linear;
    }

    protected override void Initialize()
    {
        technique = new PostTechnique("rot");
    }

    protected override void Draw(float cover, bool arriving, float dt)
    {
        // The new scene comes out of rot of its own. Nobody sees it change, everything is covered right then
        if (arriving && !wasArriving)
            seed = NewSeed();
        wasArriving = arriving;

        Vector2 screen = ScreenSize;
        Vector2 blocks = screen / MathF.Max(1.0f, PixelSize);
        Vector2 cells = screen / MathF.Max(1.0f, BlotchSize * screen.Y);
        Vector3 color = Color, edgeColor = EdgeColor;

        technique.Bind();
        technique.SetUniform(UNIFORM_COVER, Math.Clamp(cover, 0.0f, 1.0f));
        technique.SetUniform(UNIFORM_COLOR, in color);
        technique.SetUniform(UNIFORM_EDGE_COLOR, in edgeColor);
        technique.SetUniform(UNIFORM_CELLS, in cells);
        technique.SetUniform(UNIFORM_BLOCKS, in blocks);
        technique.SetUniform(UNIFORM_SEED, seed);
        DrawOverScreen();

        technique.Unbind();
    }

    public override void Finish()
    {
        // Somewhere else in the noise next time
        seed = NewSeed();
        wasArriving = false;

        base.Finish();
    }

    private static float NewSeed() => Random.Shared.NextSingle() * SEED_RANGE;
}
