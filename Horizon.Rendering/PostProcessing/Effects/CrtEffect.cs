using System.Numerics;

namespace Horizon.Rendering.PostProcessing;

/// <summary>
/// Shows the picture the way a picture tube would: soft dots along scan lines and glass that bulges, without a
/// shadow mask. The lines don't cost the picture its brightness: what a line leaves dark is made up for by driving
/// it harder and, for what is too bright for that, by a wider beam that fills the gaps (so dark colours have crisp
/// lines and bright ones bloom). See shaders/post/crt.frag for where the look comes from and what it costs.
/// It wants to be the last effect: it draws at the size of whatever the renderer is shown on, the finer that is
/// against the picture the more of the tube there is to see.
/// <para>
/// The tube has a resolution of its own, a dot for every pixel of the art rather than of the screen: for pixel art
/// that is drawn at twice its size, <see cref="PixelSize"/> is 2. Scan lines only show for what they are once each
/// is three or four pixels of the screen thick, on anything finer they blend into an even softness (and a bigger
/// <see cref="PixelSize"/> than the art has trades detail for lines).
/// </para>
/// </summary>
public sealed class CrtEffect : PostEffect
{
    private const string UNIFORM_SPREAD = "uSpread";
    private const string UNIFORM_RESOLUTION = "uResolution";
    private const string UNIFORM_HARD_SCAN = "uHardScan";
    private const string UNIFORM_HARD_BLOOM = "uHardBloom";
    private const string UNIFORM_HARD_PIX = "uHardPix";
    private const string UNIFORM_SCAN_MEAN = "uScanMean";
    private const string UNIFORM_BLOOM_NORM = "uBloomNorm";
    private const string UNIFORM_MAX_BLOOM = "uMaxBloom";
    private const string UNIFORM_WARP = "uWarp";

    // The softest a scan line can be: any softer and it gives more light over a line than the picture has, with
    // nothing left to make up for
    private const float SOFTEST_SCAN = -5.0f;

    private PostTechnique shrink = null!, tube = null!;
    private PostTarget? small;

    /// <summary>How many pixels of the picture make one dot of the tube, each way. Not used while <see cref="Resolution"/> is set.</summary>
    public float PixelSize { get; set; } = 1.0f;

    /// <summary>How many dots a scan line has and how many lines there are, for a tube of a fixed size whatever the picture is. Null to go by <see cref="PixelSize"/>.</summary>
    public Vector2? Resolution { get; set; }

    /// <summary>How quickly a scan line falls off into the dark between it and the next: -8 is soft, -16 is hard.</summary>
    public float ScanlineHardness { get; set; } = -12.0f;

    /// <summary>How quickly the wide beam falls off that fills the gaps between the lines of what is bright, from -2 to -4.</summary>
    public float BloomHardness { get; set; } = -3.0f;

    /// <summary>
    /// How much of the light the wide beam may carry. At 1 everything is as bright as it was without the tube,
    /// less keeps more of the lines on what is white and dims it for that.
    /// </summary>
    public float MaxBloom { get; set; } = 1.0f;

    /// <summary>How quickly a dot falls off into the one next to it: -2 is soft, -4 is hard.</summary>
    public float PixelHardness { get; set; } = -3.0f;

    /// <summary>How much the glass bulges, sideways and upwards. Zero is flat, an eighth is a goldfish bowl.</summary>
    public Vector2 Warp { get; set; } = new(1.0f / 32.0f, 1.0f / 24.0f);

    protected override void Initialize()
    {
        shrink = new PostTechnique("crt_shrink");
        tube = new PostTechnique("crt");
    }

    protected override void Render(PostContext context)
    {
        Vector2 resolution = Resolution ?? context.SourceSize / MathF.Max(1.0f, PixelSize);
        uint width = (uint)MathF.Max(1.0f, MathF.Round(resolution.X)), height = (uint)MathF.Max(1.0f, MathF.Round(resolution.Y));
        resolution = new Vector2(width, height);

        if (small is null || !small.Fits(width, height))
        {
            small?.Dispose();
            small = new PostTarget(width, height, PostTarget.LinearLight);
        }

        // The picture at the size of the tube. Four reads spread over what a dot stands for, each blended between
        // the pixels around it: at twice the size that is exactly the four pixels of a dot, at the same size they
        // all land on the one pixel there is.
        Vector2 ratio = context.SourceSize / resolution;
        Vector2 spread = new Vector2(SpreadOf(ratio.X), SpreadOf(ratio.Y)) / context.SourceSize;

        shrink.Bind();
        context.Source.Bind(0);
        shrink.SetUniform(PostTechnique.UNIFORM_SOURCE, 0);
        shrink.SetUniform(UNIFORM_SPREAD, in spread);
        context.Draw(small);

        tube.Bind();
        small.Texture.Bind(0);
        tube.SetUniform(PostTechnique.UNIFORM_SOURCE, 0);
        tube.SetUniform(UNIFORM_RESOLUTION, in resolution);

        float hardScan = MathF.Min(ScanlineHardness, SOFTEST_SCAN), hardBloom = MathF.Min(BloomHardness, -0.01f);
        tube.SetUniform(UNIFORM_HARD_SCAN, hardScan);
        tube.SetUniform(UNIFORM_HARD_BLOOM, hardBloom);
        tube.SetUniform(UNIFORM_HARD_PIX, MathF.Min(PixelHardness, -0.01f));

        // What the shader would otherwise work out for every pixel
        tube.SetUniform(UNIFORM_SCAN_MEAN, BeamMean(hardScan));
        tube.SetUniform(UNIFORM_BLOOM_NORM, 1.0f / BeamMean(hardBloom));
        tube.SetUniform(UNIFORM_MAX_BLOOM, Math.Clamp(MaxBloom, 0.0f, 1.0f));

        Vector2 warp = Warp;
        tube.SetUniform(UNIFORM_WARP, in warp);
        context.Draw();

        tube.Unbind();
    }

    // How much light a beam that is 1 at the middle of its line and falls off as 2^(hardness * distance^2) gives
    // over the height of a line, on average
    private static float BeamMean(float hardness) => MathF.Sqrt(MathF.PI / (-hardness * MathF.Log(2.0f)));

    // How far from the middle of a dot the reads of the shrinking pass are, in pixels of the picture
    private static float SpreadOf(float ratio) => ratio <= 2.0f ? MathF.Max(0.0f, ratio - 1.0f) * 0.5f : ratio * 0.25f;

    public override void Dispose()
    {
        small?.Dispose();
        small = null;

        base.Dispose();
    }
}
