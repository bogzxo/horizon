using System.Numerics;

using Horizon.Graphics;

namespace Horizon.Rendering.PostProcessing;

/// <summary>
/// Helper class that blurs a picture into a target of half its size, for everything in here that needs a blur (the effect, the transition).
/// The picture is shrunk first and then blurred in rounds of two passes, one sideways and one up and down.
/// Half the size is a quarter of the work, and nobody can tell on something that is blurred anyway.
/// <para>
/// Nobody can tell unless the blur is barely there, that is. A half sized picture is soft however little it was blurred.
/// So whoever shows the result lays it over the sharp picture by <see cref="Share"/>, and the blur comes on without a pop.
/// </para>
/// </summary>
internal sealed class GaussianBlur : IDisposable
{
    /// <summary>The radius from which the blurred picture is shown by itself. Below it, it is blended with the sharp one.</summary>
    public const float FULL_RADIUS = 2.0f;

    private const string UNIFORM_STEP = "uStep";
    private const string UNIFORM_TEXEL = "uTexel";

    // From this radius on (in pixels of the picture) the blur is worked out at a quarter of the size instead of half of it,
    // then blown up to half: a sixteenth of the pixels and one round where half the size would take two. Nobody can tell
    // on a blur this big, but the first few pixels of a blur that is barely there would turn blocky
    private const float QUARTER_FROM = 12.0f;

    // The furthest read of the shader is this many steps from the middle, which is what a radius gets divided by to get the step
    private const float TAP_REACH = 3.25f;

    // One round only reaches so far before the gaps between its reads start to show, past that it goes round again
    private const float RADIUS_PER_ROUND = 8.0f;
    private const int MAX_ROUNDS = 3;

    private PostTechnique? copy, blur, downsample;
    private PostTarget? scratch, quarter, quarterScratch;

    /// <summary>
    /// How much of the blurred picture to show over the sharp one for a radius, from 0 for none of it to 1 for nothing else.
    /// </summary>
    public static float Share(float radius) => Math.Clamp(radius / FULL_RADIUS, 0.0f, 1.0f);

    /// <summary>
    /// Blurs a picture. GL thread, with nothing blended.
    /// </summary>
    /// <param name="sourceSize">The size of <paramref name="source"/> in pixels.</param>
    /// <param name="radius">How far the picture is smeared, in pixels of the picture.</param>
    /// <param name="into">Where the blurred picture goes. It is made if there is none, and made anew if it is the wrong size.</param>
    /// <returns>What is in <paramref name="into"/>.</returns>
    public PostTarget Blur(Texture source, Vector2 sourceSize, float radius, ref PostTarget? into)
    {
        copy ??= new PostTechnique("copy");
        blur ??= new PostTechnique("blur");

        if (radius >= QUARTER_FROM)
            return BlurAtQuarter(source, sourceSize, radius, ref into);

        PostTarget result = Fit(ref into, sourceSize), other = Fit(ref scratch, sourceSize);

        // Shrunk first. Every pixel of the small picture is four of the big one, so the reads of the blur skip nothing
        copy.Bind();
        source.Bind(0);
        result.Bind();
        ScreenTriangle.Draw();

        // A big blur is done as a few smaller ones on top of each other, which comes to the same thing without the gaps
        int rounds = Math.Clamp((int)MathF.Ceiling(radius / RADIUS_PER_ROUND), 1, MAX_ROUNDS);

        // In pixels of the small picture, which are twice the size
        float step = MathF.Max(0.0f, radius) * 0.5f / MathF.Sqrt(rounds) / TAP_REACH;
        Vector2 sideways = new(step / result.Size.X, 0.0f), upwards = new(0.0f, step / result.Size.Y);

        blur.Bind();

        for (int round = 0; round < rounds; round++)
        {
            result.Texture.Bind(0);
            blur.SetUniform(UNIFORM_STEP, in sideways);
            other.Bind();
            ScreenTriangle.Draw();

            other.Texture.Bind(0);
            blur.SetUniform(UNIFORM_STEP, in upwards);
            result.Bind();
            ScreenTriangle.Draw();
        }

        blur.Unbind();
        return result;
    }

    /// <summary>
    /// Helper method to blur a picture at a quarter of its size, for a blur that is big enough for that not to show:
    /// averaged down to a quarter, blurred there in rounds the way it is at half, and blown up into the half sized result.
    /// </summary>
    private PostTarget BlurAtQuarter(Texture source, Vector2 sourceSize, float radius, ref PostTarget? into)
    {
        downsample ??= new PostTechnique("downsample");

        PostTarget result = Fit(ref into, sourceSize);
        PostTarget small = Fit(ref quarter, sourceSize / 2.0f), other = Fit(ref quarterScratch, sourceSize / 2.0f);

        downsample.Bind();
        source.Bind(0);
        Vector2 texel = Vector2.One / sourceSize;
        downsample.SetUniform(UNIFORM_TEXEL, in texel);
        small.Bind();
        ScreenTriangle.Draw();

        // A pixel here is four of the picture, so a round reaches twice as far as it does at half the size
        int rounds = Math.Clamp((int)MathF.Ceiling(radius / (RADIUS_PER_ROUND * 2.0f)), 1, MAX_ROUNDS);
        float step = radius * 0.25f / MathF.Sqrt(rounds) / TAP_REACH;
        Vector2 sideways = new(step / small.Size.X, 0.0f), upwards = new(0.0f, step / small.Size.Y);

        blur!.Bind();

        for (int round = 0; round < rounds; round++)
        {
            small.Texture.Bind(0);
            blur.SetUniform(UNIFORM_STEP, in sideways);
            other.Bind();
            ScreenTriangle.Draw();

            other.Texture.Bind(0);
            blur.SetUniform(UNIFORM_STEP, in upwards);
            small.Bind();
            ScreenTriangle.Draw();
        }

        // Blown up to half the size, which is what whoever asked reads it at. Smooth already, the GPU blends the rest
        copy!.Bind();
        small.Texture.Bind(0);
        result.Bind();
        ScreenTriangle.Draw();

        copy.Unbind();
        return result;
    }

    /// <summary>
    /// Helper method to make sure a target is the size a picture gets blurred into (half of it), which it is made anew for if it isn't.
    /// </summary>
    public static PostTarget Fit(ref PostTarget? target, Vector2 pictureSize)
    {
        uint width = (uint)MathF.Max(1.0f, pictureSize.X / 2.0f), height = (uint)MathF.Max(1.0f, pictureSize.Y / 2.0f);

        if (target is null || !target.Fits(width, height))
        {
            target?.Dispose();
            target = new PostTarget(width, height);
        }

        return target;
    }

    public void Dispose()
    {
        scratch?.Dispose();
        quarter?.Dispose();
        quarterScratch?.Dispose();
        scratch = quarter = quarterScratch = null;
    }
}
