using System.Numerics;

using Horizon.Rendering.Scaling;

namespace Horizon.Tests;

public class CasReferenceTests
{
    [Theory]
    [InlineData(0.0f)]
    [InlineData(1.0f)]
    [InlineData(0.5f)]
    [InlineData(0.2f)]
    [InlineData(0.85f)]
    public void A_flat_colour_comes_out_the_colour_it_went_in_at_every_ratio_and_sharpness(float value)
    {
        var colour = new Vector3(value, value * 0.5f, 1.0f - value);
        foreach ((int w, int h, int ow, int oh) in new[] { (8, 6, 16, 12), (12, 9, 16, 12), (15, 11, 16, 12) })
        {
            foreach (float sharpness in new[] { 0.0f, 0.5f, 1.0f })
            {
                Vector3[] output = CasReference.Upscale(Filled(w, h, colour), w, h, ow, oh, sharpness);
                foreach (Vector3 pixel in output)
                {
                    // Within half an 8 bit step, which is what the approximate reciprocals leave
                    Assert.InRange(pixel.X, colour.X - 0.5f / 255.0f, colour.X + 0.5f / 255.0f);
                    Assert.InRange(pixel.Y, colour.Y - 0.5f / 255.0f, colour.Y + 0.5f / 255.0f);
                    Assert.InRange(pixel.Z, colour.Z - 0.5f / 255.0f, colour.Z + 0.5f / 255.0f);
                }
            }
        }
    }

    [Fact]
    public void Edges_of_the_picture_read_their_own_pixels_and_never_anything_outside()
    {
        // A picture with a white border and black inside, whatever is past the edge is the edge again
        const int w = 6, h = 6;
        var source = Filled(w, h, Vector3.Zero);
        for (int i = 0; i < w; i++)
        {
            source[i] = source[(h - 1) * w + i] = Vector3.One;
            source[i * w] = source[i * w + w - 1] = Vector3.One;
        }

        Vector3[] output = CasReference.Upscale(source, w, h, 12, 12, 0.5f);

        // The corners are the corner's white, not darkened by anything black beyond the edge
        Assert.True(output[0].X > 0.99f);
        Assert.True(output[11].X > 0.99f);
        Assert.True(output[11 * 12].X > 0.99f);
        Assert.True(output[11 * 12 + 11].X > 0.99f);
        Assert.All(output, pixel => Assert.True(float.IsFinite(pixel.X) && pixel.X is >= 0.0f and <= 1.0f));
    }

    [Fact]
    public void Black_and_white_never_make_anything_that_isnt_a_number()
    {
        var source = new Vector3[16];
        for (int i = 0; i < source.Length; i++)
            source[i] = i % 3 == 0 ? Vector3.One : Vector3.Zero;

        foreach (Vector3 pixel in CasReference.Upscale(source, 4, 4, 8, 8, 1.0f))
            Assert.True(float.IsFinite(pixel.X) && float.IsFinite(pixel.Y) && float.IsFinite(pixel.Z));
    }

    [Fact]
    public void An_edge_comes_out_steeper_than_bilinear_and_more_so_with_more_sharpness()
    {
        // A soft step from dark grey to light grey across a row, upscaled twice
        const int w = 16, h = 4;
        var source = new Vector3[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float v = x < 7 ? 0.2f : x == 7 ? 0.4f : x == 8 ? 0.6f : 0.8f;
                source[y * w + x] = new Vector3(v);
            }
        }

        float Steepness(float sharpness)
        {
            Vector3[] output = CasReference.Upscale(source, w, h, w * 2, h * 2, sharpness);
            int row = 3 * w * 2;
            float most = 0.0f;
            for (int x = 1; x < w * 2; x++)
                most = MathF.Max(most, output[row + x].Y - output[row + x - 1].Y);
            return most;
        }

        // Bilinear between texel middles moves by a step of the source over two output pixels at most
        float bilinear = 0.2f / 2.0f;
        float soft = Steepness(0.0f), sharp = Steepness(1.0f);

        Assert.True(soft > bilinear, $"CAS at sharpness 0 went up {soft} a pixel, bilinear {bilinear}");
        Assert.True(sharp > soft, $"CAS at sharpness 1 went up {sharp} a pixel, at 0 {soft}");
    }

    [Fact]
    public void The_approximations_are_the_headers_bit_tricks_and_close_to_the_real_thing()
    {
        foreach (float a in new[] { 0.01f, 0.1f, 0.5f, 1.0f, 2.0f, 31.0f })
        {
            Assert.InRange(CasReference.PrxMedRcp(a) * a, 0.995f, 1.005f);
            Assert.InRange(CasReference.PrxLoRcp(a) * a, 0.9f, 1.1f);
            Assert.InRange(CasReference.PrxLoSqrt(a) / MathF.Sqrt(a), 0.95f, 1.05f);
        }

        // The constants out of ffx_a.h, so a change to them shows
        Assert.Equal(BitConverter.UInt32BitsToSingle(0x7ef07ebbu - BitConverter.SingleToUInt32Bits(1.0f)), CasReference.PrxLoRcp(1.0f));
        Assert.Equal(BitConverter.UInt32BitsToSingle((BitConverter.SingleToUInt32Bits(4.0f) >> 1) + 0x1fbc4639u), CasReference.PrxLoSqrt(4.0f));
    }

    [Fact]
    public void The_same_picture_comes_out_the_same_every_time()
    {
        var random = new Random(7);
        var source = new Vector3[10 * 7];
        for (int i = 0; i < source.Length; i++)
            source[i] = new Vector3(random.NextSingle(), random.NextSingle(), random.NextSingle());

        Assert.Equal(CasReference.Upscale(source, 10, 7, 17, 12, 0.3f), CasReference.Upscale(source, 10, 7, 17, 12, 0.3f));
    }

    private static Vector3[] Filled(int width, int height, Vector3 colour)
    {
        var pixels = new Vector3[width * height];
        Array.Fill(pixels, colour);
        return pixels;
    }
}
