using System.Numerics;

namespace Horizon.Rendering.PostProcessing;

/// <summary>
/// Smears what moves along the way it is moving, by as much as it moves while the shutter is open: what film does
/// by itself. For pixel art this is more than a look. Art that is kept on its pixels can't glide, it stands still
/// for a few frames and then jumps one, and a camera that follows somebody does that with the whole screen. A smear
/// as long as the jump fills the steps in, and the eye sees it move.
/// <para>
/// It goes by how fast everything in the picture is moving across it and how near it is (<see cref="PostContext.Motion"/>),
/// which a <see cref="DeferredRenderer2D"/> keeps track of for a world (sprites know how fast they are going, the
/// layers of a tile map how fast they scroll) and a <see cref="PostLayer"/> for what is laid over it, a UI. On a
/// plain renderer this does nothing.
/// Those speeds are worked out over time rather than from one frame to the next (see <see cref="Core.MotionEstimator"/>),
/// so the blur is as long when the game draws a thousand frames a second as when it draws thirty.
/// See shaders/post/motion_blur.frag for how the smearing itself is done.
/// </para>
/// <para>
/// This is the exact one and the fussy one. It is only as good as the speeds it is told, it smears no further than
/// <see cref="MaxLength"/> and it has one shutter for everything in the picture. <see cref="MotionBlurEffect"/> is the
/// one to reach for first, it needs none of that.
/// </para>
/// </summary>
public sealed class VelocityBlurEffect : PostEffect
{
    // How many pixels the squares the picture is cut into are wide, nothing is smeared further than that to either
    // side. Must match TILE in shaders/post/motion_tiles.frag.
    private const int TILE_SIZE = 16;

    private const string UNIFORM_MOTION = "uMotion";
    private const string UNIFORM_TILES = "uTiles";
    private const string UNIFORM_SPREAD = "uSpread";
    private const string UNIFORM_SIZE = "uSize";
    private const string UNIFORM_REACH = "uReach";
    private const string UNIFORM_MAX_REACH = "uMaxReach";

    private PostTechnique fastest = null!, spread = null!, blur = null!;
    private PostTarget? tiles, spreadTiles;

    /// <summary>
    /// How long the shutter is open, in seconds: everything is smeared over as far as it moves in that time. A
    /// sixtieth of a second is what a screen shows a frame for, which is about what it takes to hide the stepping
    /// of pixel art. Longer is dreamier.
    /// </summary>
    public float Shutter { get; set; } = 1.0f / 60.0f;

    /// <summary>The furthest anything is smeared, in pixels from one end to the other. No more than 32.</summary>
    public float MaxLength { get; set; } = 32;

    protected internal override bool NeedsMotion => true;

    protected override void Initialize()
    {
        fastest = new PostTechnique("motion_tiles");
        spread = new PostTechnique("motion_spread");
        blur = new PostTechnique("motion_blur");
    }

    protected override void Render(PostContext context)
    {
        // Without knowing what moves there is nothing to smear
        if (context.Motion is not { } motion || Shutter <= 0.0f)
        {
            context.Copy();
            return;
        }

        uint width = (uint)MathF.Ceiling(context.SourceSize.X / TILE_SIZE), height = (uint)MathF.Ceiling(context.SourceSize.Y / TILE_SIZE);
        if (tiles is null || !tiles.Fits(width, height))
        {
            tiles?.Dispose();
            spreadTiles?.Dispose();

            tiles = new PostTarget(width, height, PostTarget.Exact);
            spreadTiles = new PostTarget(width, height, PostTarget.Exact);
        }

        // The fastest thing in every square of the picture
        fastest.Bind();
        motion.Bind(0);
        context.Draw(tiles);

        // ...and in the squares around it
        spread.Bind();
        tiles.Texture.Bind(0);
        context.Draw(spreadTiles!);

        // Motion is written in halves of the screen a second (two of them either way, see encodeMotion in the
        // shaders that write it), which comes to this many pixels of smear to either side
        Vector2 size = context.SourceSize;
        Vector2 reach = size * (Shutter * 0.5f);

        blur.Bind();
        context.Source.Bind(0);
        motion.Bind(1);
        spreadTiles!.Texture.Bind(2);
        blur.SetUniform(UNIFORM_SIZE, in size);
        blur.SetUniform(UNIFORM_REACH, in reach);
        blur.SetUniform(UNIFORM_MAX_REACH, Math.Clamp(MaxLength * 0.5f, 0.5f, TILE_SIZE));
        context.Draw();

        blur.Unbind();
    }

    public override void Dispose()
    {
        tiles?.Dispose();
        spreadTiles?.Dispose();
        tiles = spreadTiles = null;

        base.Dispose();
    }
}
