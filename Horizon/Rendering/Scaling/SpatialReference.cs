// CPU twins of shaders/post/fsr1_easu.slang, fsr1_rcas.slang and gsr1.slang, for the GPU check to hold them to. EASU
// and RCAS are from ffx_fsr1.h 1.20210629 (GPUOpen-Effects/FidelityFX-FSR a21ffb8f6c13233ba336352bdff293894c706575, MIT),
// GSR 1 from the restored sgsr1_shader_mobile.frag (SnapdragonGameStudios/snapdragon-gsr d926f074bcb9d714e179f1ce0fcb9ee2eeb5074e,
// BSD 3-clause). The notices are in the shaders and in THIRD-PARTY-NOTICES.md.

using System.Numerics;

namespace Horizon.Rendering.Scaling;

/// <summary>
/// FSR 1 and GSR 1 worked out on the CPU line for line the way the shaders do, slow and only for checking. Pictures are
/// RGBA, 0 to 1, rows top first. A gather or a linear sample is worked out the way Vulkan defines it for a sampler
/// that is linear and clamped to the edge. A GPU does the linear weights with fewer bits than a float (8 of them is
/// common), so a check of a sample through a sampler allows a few 255ths.
/// </summary>
public static class SpatialReference
{
    /// <summary>EASU then RCAS from a picture of one size to another, what FSR 1 makes.</summary>
    public static Vector4[] Fsr1(ReadOnlySpan<Vector4> source, int width, int height, int outputWidth, int outputHeight, float sharpness)
    {
        Vector4[] easu = Easu(source, width, height, outputWidth, outputHeight);
        return Rcas(easu, outputWidth, outputHeight, SpatialSharpness.RcasAmount(sharpness));
    }

    /// <summary>FsrEasuF over every output pixel, with alpha sampled the way the shader samples it.</summary>
    public static Vector4[] Easu(ReadOnlySpan<Vector4> source, int width, int height, int outputWidth, int outputHeight)
    {
        EasuConstants c = EasuConstants.For(width, height, outputWidth, outputHeight);
        var output = new Vector4[outputWidth * outputHeight];
        for (int y = 0; y < outputHeight; y++)
        {
            for (int x = 0; x < outputWidth; x++)
            {
                Vector3 rgb = EasuPixel(source, width, height, (uint)x, (uint)y, c);
                float alpha = Sample(source, width, height, (x + 0.5f) / outputWidth, (y + 0.5f) / outputHeight).W;
                output[y * outputWidth + x] = new Vector4(rgb, alpha);
            }
        }

        return output;
    }

    /// <summary>FsrRcasF over every pixel of a picture, alpha passed through, with con.x the linear sharpness.</summary>
    public static Vector4[] Rcas(ReadOnlySpan<Vector4> source, int width, int height, float amount)
    {
        const float LIMIT = 0.25f - (1.0f / 16.0f);
        var output = new Vector4[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                Vector4 b = Load(source, width, height, x, y - 1), d = Load(source, width, height, x - 1, y);
                Vector4 e = Load(source, width, height, x, y);
                Vector4 f = Load(source, width, height, x + 1, y), h = Load(source, width, height, x, y + 1);

                Vector3 mn4 = Vector3.Min(Vector3.Min(Vector3.Min(Rgb(b), Rgb(d)), Rgb(f)), Rgb(h));
                Vector3 mx4 = Vector3.Max(Vector3.Max(Vector3.Max(Rgb(b), Rgb(d)), Rgb(f)), Rgb(h));
                Vector3 hitMin = Vector3.Min(mn4, Rgb(e)) * Reciprocal(Vector3.Max(4.0f * mx4, new Vector3(1e-6f)));
                Vector3 hitMax = (Vector3.One - Vector3.Max(mx4, Rgb(e))) * Reciprocal(Vector3.Min(4.0f * mn4 - new Vector3(4.0f), new Vector3(-1e-6f)));
                Vector3 lobeRGB = Vector3.Max(-hitMin, hitMax);
                float lobe = MathF.Max(-LIMIT, MathF.Min(MathF.Max(lobeRGB.X, MathF.Max(lobeRGB.Y, lobeRGB.Z)), 0.0f)) * amount;
                float rcpL = CasReference.PrxMedRcp(4.0f * lobe + 1.0f);
                Vector3 pix = (lobe * Rgb(b) + lobe * Rgb(d) + lobe * Rgb(h) + lobe * Rgb(f) + Rgb(e)) * rcpL;
                output[y * width + x] = new Vector4(pix, e.W);
            }
        }

        return output;
    }

    /// <summary>GSR 1 (mode 1, RGBA) over every output pixel, alpha sampled the way the shader samples it.</summary>
    public static Vector4[] Gsr1(ReadOnlySpan<Vector4> source, int width, int height, int outputWidth, int outputHeight, float sharpness)
    {
        Vector4 con1 = GsrConstants.ViewportInfo(width, height);
        float edgeSharpness = SpatialSharpness.GsrEdgeSharpness(sharpness);
        var output = new Vector4[outputWidth * outputHeight];
        for (int y = 0; y < outputHeight; y++)
        {
            for (int x = 0; x < outputWidth; x++)
                output[y * outputWidth + x] = GsrPixel(source, width, height, new Vector2((x + 0.5f) / outputWidth, (y + 0.5f) / outputHeight), con1, edgeSharpness);
        }

        return output;
    }

    private static Vector4 GsrPixel(ReadOnlySpan<Vector4> source, int width, int height, Vector2 uv, Vector4 con1, float edgeSharpness)
    {
        Vector4 sampled = Sample(source, width, height, uv.X, uv.Y);
        Vector4 pix = new(sampled.X, sampled.Y, sampled.Z, 0.0f);

        Vector2 imgCoord = uv * new Vector2(con1.Z, con1.W) + new Vector2(-0.5f, 0.5f);
        Vector2 imgCoordPixel = new(MathF.Floor(imgCoord.X), MathF.Floor(imgCoord.Y));
        Vector2 coord = imgCoordPixel * new Vector2(con1.X, con1.Y);
        Vector2 pl = imgCoord - imgCoordPixel;
        Vector4 left = Gather(source, width, height, coord, 1);

        float edgeVote = MathF.Abs(left.Z - left.Y) + MathF.Abs(pix.Y - left.Y) + MathF.Abs(pix.Y - left.Z);
        if (edgeVote > GsrConstants.EDGE_THRESHOLD)
        {
            coord.X += con1.X;
            Vector4 right = Gather(source, width, height, coord + new Vector2(con1.X, 0.0f), 1);
            Vector4 up = Gather(source, width, height, coord + new Vector2(0.0f, -con1.Y), 1);
            Vector4 down = Gather(source, width, height, coord + new Vector2(0.0f, con1.Y), 1);
            Vector4 upDown = new(up.W, up.Z, down.Y, down.X);

            float mean = (left.Y + left.Z + right.X + right.W) * 0.25f;
            Vector4 m = new(mean);
            left -= m;
            right -= m;
            upDown -= m;
            pix.W = pix.Y - mean;

            float sum = Abs(left) + Abs(right) + Abs(upDown);
            float std = 2.181818f / MathF.Max(sum, 1e-6f);

            Vector2 aWY = WeightY(pl.X, pl.Y + 1.0f, upDown.X, std);
            aWY += WeightY(pl.X - 1.0f, pl.Y + 1.0f, upDown.Y, std);
            aWY += WeightY(pl.X - 1.0f, pl.Y - 2.0f, upDown.Z, std);
            aWY += WeightY(pl.X, pl.Y - 2.0f, upDown.W, std);
            aWY += WeightY(pl.X + 1.0f, pl.Y - 1.0f, left.X, std);
            aWY += WeightY(pl.X, pl.Y - 1.0f, left.Y, std);
            aWY += WeightY(pl.X, pl.Y, left.Z, std);
            aWY += WeightY(pl.X + 1.0f, pl.Y, left.W, std);
            aWY += WeightY(pl.X - 1.0f, pl.Y - 1.0f, right.X, std);
            aWY += WeightY(pl.X - 2.0f, pl.Y - 1.0f, right.Y, std);
            aWY += WeightY(pl.X - 2.0f, pl.Y, right.Z, std);
            aWY += WeightY(pl.X - 1.0f, pl.Y, right.W, std);

            float finalY = aWY.Y / aWY.X;
            float max4 = MathF.Max(MathF.Max(left.Y, left.Z), MathF.Max(right.X, right.W));
            float min4 = MathF.Min(MathF.Min(left.Y, left.Z), MathF.Min(right.X, right.W));
            finalY = Math.Clamp(edgeSharpness * finalY, min4, max4);
            float deltaY = Math.Clamp(finalY - pix.W, -GsrConstants.MAX_LUMA_CORRECTION, GsrConstants.MAX_LUMA_CORRECTION);

            pix.X = Math.Clamp(pix.X + deltaY, 0.0f, 1.0f);
            pix.Y = Math.Clamp(pix.Y + deltaY, 0.0f, 1.0f);
            pix.Z = Math.Clamp(pix.Z + deltaY, 0.0f, 1.0f);
        }

        return new Vector4(pix.X, pix.Y, pix.Z, sampled.W);
    }

    private static float FastLanczos2(float x)
    {
        float wA = x - 4.0f;
        float wB = x * wA - wA;
        wA *= wA;
        return wB * wA;
    }

    private static Vector2 WeightY(float dx, float dy, float c, float std)
    {
        float x = ((dx * dx) + (dy * dy)) * 0.55f + Math.Clamp(MathF.Abs(c) * std, 0.0f, 1.0f);
        float w = FastLanczos2(x);
        return new Vector2(w, w * c);
    }

    private static Vector3 EasuPixel(ReadOnlySpan<Vector4> source, int width, int height, uint ix, uint iy, in EasuConstants c)
    {
        Vector2 pp = new Vector2(ix, iy) * new Vector2(c.Con0.X, c.Con0.Y) + new Vector2(c.Con0.Z, c.Con0.W);
        Vector2 fp = new(MathF.Floor(pp.X), MathF.Floor(pp.Y));
        pp -= fp;

        Vector2 p0 = fp * new Vector2(c.Con1.X, c.Con1.Y) + new Vector2(c.Con1.Z, c.Con1.W);
        Vector2 p1 = p0 + new Vector2(c.Con2.X, c.Con2.Y);
        Vector2 p2 = p0 + new Vector2(c.Con2.Z, c.Con2.W);
        Vector2 p3 = p0 + new Vector2(c.Con3.X, c.Con3.Y);

        Vector4 bczzR = Gather(source, width, height, p0, 0), bczzG = Gather(source, width, height, p0, 1), bczzB = Gather(source, width, height, p0, 2);
        Vector4 ijfeR = Gather(source, width, height, p1, 0), ijfeG = Gather(source, width, height, p1, 1), ijfeB = Gather(source, width, height, p1, 2);
        Vector4 klhgR = Gather(source, width, height, p2, 0), klhgG = Gather(source, width, height, p2, 1), klhgB = Gather(source, width, height, p2, 2);
        Vector4 zzonR = Gather(source, width, height, p3, 0), zzonG = Gather(source, width, height, p3, 1), zzonB = Gather(source, width, height, p3, 2);

        Vector4 bczzL = bczzB * 0.5f + (bczzR * 0.5f + bczzG);
        Vector4 ijfeL = ijfeB * 0.5f + (ijfeR * 0.5f + ijfeG);
        Vector4 klhgL = klhgB * 0.5f + (klhgR * 0.5f + klhgG);
        Vector4 zzonL = zzonB * 0.5f + (zzonR * 0.5f + zzonG);
        float bL = bczzL.X, cL = bczzL.Y, iL = ijfeL.X, jL = ijfeL.Y, fL = ijfeL.Z, eL = ijfeL.W;
        float kL = klhgL.X, lL = klhgL.Y, hL = klhgL.Z, gL = klhgL.W, oL = zzonL.Z, nL = zzonL.W;

        Vector2 dir = Vector2.Zero;
        float len = 0.0f;
        EasuSet(ref dir, ref len, (1.0f - pp.X) * (1.0f - pp.Y), bL, eL, fL, gL, jL);
        EasuSet(ref dir, ref len, pp.X * (1.0f - pp.Y), cL, fL, gL, hL, kL);
        EasuSet(ref dir, ref len, (1.0f - pp.X) * pp.Y, fL, iL, jL, kL, nL);
        EasuSet(ref dir, ref len, pp.X * pp.Y, gL, jL, kL, lL, oL);

        Vector2 dir2 = dir * dir;
        float dirR = dir2.X + dir2.Y;
        bool zro = dirR < (1.0f / 32768.0f);
        dirR = BitConverter.UInt32BitsToSingle(0x5f347d74u - (BitConverter.SingleToUInt32Bits(dirR) >> 1));
        dirR = zro ? 1.0f : dirR;
        dir.X = zro ? 1.0f : dir.X;
        dir *= dirR;
        len *= 0.5f;
        len *= len;
        float stretch = (dir.X * dir.X + dir.Y * dir.Y) * CasReference.PrxLoRcp(MathF.Max(MathF.Abs(dir.X), MathF.Abs(dir.Y)));
        Vector2 len2 = new(1.0f + (stretch - 1.0f) * len, 1.0f + (-0.5f) * len);
        float lob = 0.5f + ((1.0f / 4.0f - 0.04f) - 0.5f) * len;
        float clp = CasReference.PrxLoRcp(lob);

        Vector3 f = new(ijfeR.Z, ijfeG.Z, ijfeB.Z), g = new(klhgR.W, klhgG.W, klhgB.W);
        Vector3 j = new(ijfeR.Y, ijfeG.Y, ijfeB.Y), k = new(klhgR.X, klhgG.X, klhgB.X);
        Vector3 min4 = Vector3.Min(Vector3.Min(f, Vector3.Min(g, j)), k);
        Vector3 max4 = Vector3.Max(Vector3.Max(f, Vector3.Max(g, j)), k);

        Vector3 aC = Vector3.Zero;
        float aW = 0.0f;
        EasuTap(ref aC, ref aW, new Vector2(0.0f, -1.0f) - pp, dir, len2, lob, clp, new Vector3(bczzR.X, bczzG.X, bczzB.X));
        EasuTap(ref aC, ref aW, new Vector2(1.0f, -1.0f) - pp, dir, len2, lob, clp, new Vector3(bczzR.Y, bczzG.Y, bczzB.Y));
        EasuTap(ref aC, ref aW, new Vector2(-1.0f, 1.0f) - pp, dir, len2, lob, clp, new Vector3(ijfeR.X, ijfeG.X, ijfeB.X));
        EasuTap(ref aC, ref aW, new Vector2(0.0f, 1.0f) - pp, dir, len2, lob, clp, j);
        EasuTap(ref aC, ref aW, new Vector2(0.0f, 0.0f) - pp, dir, len2, lob, clp, f);
        EasuTap(ref aC, ref aW, new Vector2(-1.0f, 0.0f) - pp, dir, len2, lob, clp, new Vector3(ijfeR.W, ijfeG.W, ijfeB.W));
        EasuTap(ref aC, ref aW, new Vector2(1.0f, 1.0f) - pp, dir, len2, lob, clp, k);
        EasuTap(ref aC, ref aW, new Vector2(2.0f, 1.0f) - pp, dir, len2, lob, clp, new Vector3(klhgR.Y, klhgG.Y, klhgB.Y));
        EasuTap(ref aC, ref aW, new Vector2(2.0f, 0.0f) - pp, dir, len2, lob, clp, new Vector3(klhgR.Z, klhgG.Z, klhgB.Z));
        EasuTap(ref aC, ref aW, new Vector2(1.0f, 0.0f) - pp, dir, len2, lob, clp, g);
        EasuTap(ref aC, ref aW, new Vector2(1.0f, 2.0f) - pp, dir, len2, lob, clp, new Vector3(zzonR.Z, zzonG.Z, zzonB.Z));
        EasuTap(ref aC, ref aW, new Vector2(0.0f, 2.0f) - pp, dir, len2, lob, clp, new Vector3(zzonR.W, zzonG.W, zzonB.W));

        return Vector3.Min(max4, Vector3.Max(min4, aC * (1.0f / aW)));
    }

    private static void EasuTap(ref Vector3 aC, ref float aW, Vector2 off, Vector2 dir, Vector2 len, float lob, float clp, Vector3 c)
    {
        Vector2 v = new((off.X * dir.X) + (off.Y * dir.Y), (off.X * -dir.Y) + (off.Y * dir.X));
        v *= len;
        float d2 = MathF.Min(v.X * v.X + v.Y * v.Y, clp);
        float wB = (2.0f / 5.0f) * d2 - 1.0f;
        float wA = lob * d2 - 1.0f;
        wB *= wB;
        wA *= wA;
        wB = (25.0f / 16.0f) * wB - (25.0f / 16.0f - 1.0f);
        float w = wB * wA;
        aC += c * w;
        aW += w;
    }

    private static void EasuSet(ref Vector2 dir, ref float len, float w, float lA, float lB, float lC, float lD, float lE)
    {
        float lenX = CasReference.PrxLoRcp(MathF.Max(MathF.Abs(lD - lC), MathF.Abs(lC - lB)));
        float dirX = lD - lB;
        dir.X += dirX * w;
        lenX = Math.Clamp(MathF.Abs(dirX) * lenX, 0.0f, 1.0f);
        len += lenX * lenX * w;

        float lenY = CasReference.PrxLoRcp(MathF.Max(MathF.Abs(lE - lC), MathF.Abs(lC - lA)));
        float dirY = lE - lA;
        dir.Y += dirY * w;
        lenY = Math.Clamp(MathF.Abs(dirY) * lenY, 0.0f, 1.0f);
        len += lenY * lenY * w;
    }

    /// <summary>
    /// A gather4 of one channel at a point, Vulkan's way. The four texels round the point, clamped to the edge, in the
    /// order x (left, below), y (right, below), z (right, above), w (left, above), where below is the larger row.
    /// </summary>
    public static Vector4 Gather(ReadOnlySpan<Vector4> source, int width, int height, Vector2 p, int channel)
    {
        int x0 = (int)MathF.Floor(p.X * width - 0.5f), y0 = (int)MathF.Floor(p.Y * height - 0.5f);
        return new Vector4(
            Channel(Load(source, width, height, x0, y0 + 1), channel),
            Channel(Load(source, width, height, x0 + 1, y0 + 1), channel),
            Channel(Load(source, width, height, x0 + 1, y0), channel),
            Channel(Load(source, width, height, x0, y0), channel));
    }

    /// <summary>A linear sample at a point, clamped to the edge, with float weights.</summary>
    public static Vector4 Sample(ReadOnlySpan<Vector4> source, int width, int height, float u, float v)
    {
        float x = u * width - 0.5f, y = v * height - 0.5f;
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        Vector4 top = Vector4.Lerp(Load(source, width, height, x0, y0), Load(source, width, height, x0 + 1, y0), fx);
        Vector4 bottom = Vector4.Lerp(Load(source, width, height, x0, y0 + 1), Load(source, width, height, x0 + 1, y0 + 1), fx);
        return Vector4.Lerp(top, bottom, fy);
    }

    private static Vector4 Load(ReadOnlySpan<Vector4> source, int width, int height, int x, int y) =>
        source[Math.Clamp(y, 0, height - 1) * width + Math.Clamp(x, 0, width - 1)];

    private static float Channel(Vector4 c, int channel) => channel switch { 0 => c.X, 1 => c.Y, 2 => c.Z, _ => c.W };

    private static Vector3 Rgb(Vector4 c) => new(c.X, c.Y, c.Z);

    private static Vector3 Reciprocal(Vector3 v) => new(1.0f / v.X, 1.0f / v.Y, 1.0f / v.Z);

    private static float Abs(Vector4 v) => MathF.Abs(v.X) + MathF.Abs(v.Y) + MathF.Abs(v.Z) + MathF.Abs(v.W);
}
