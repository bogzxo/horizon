using System.Runtime.CompilerServices;

namespace AutoVoxel.Generation;

/// <summary>
/// Simplex noise, two and three dimensional, Gustavson's take on Perlin's improvement of his own idea, seeded. Smooth
/// hills of numbers from about -1 to 1, the same for the same seed and point on every machine, which is the whole of
/// how a world of four billion voxels fits in a seed. Read only once made, so every thread shares one.
/// </summary>
public sealed class Noise
{
    private static readonly sbyte[] Grad3 =
    [
        1, 1, 0, -1, 1, 0, 1, -1, 0, -1, -1, 0,
        1, 0, 1, -1, 0, 1, 1, 0, -1, -1, 0, -1,
        0, 1, 1, 0, -1, 1, 0, 1, -1, 0, -1, -1
    ];

    private const float F2 = 0.36602540378f; // (sqrt(3) - 1) / 2
    private const float G2 = 0.21132486540f; // (3 - sqrt(3)) / 6
    private const float F3 = 1.0f / 3.0f;
    private const float G3 = 1.0f / 6.0f;

    private readonly byte[] perm = new byte[512];
    private readonly byte[] permMod12 = new byte[512];

    public Noise(int seed)
    {
        var p = new byte[256];
        for (int i = 0; i < 256; i++) p[i] = (byte)i;

        var random = new Random(seed);
        for (int i = 255; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (p[i], p[j]) = (p[j], p[i]);
        }

        for (int i = 0; i < 512; i++)
        {
            perm[i] = p[i & 255];
            permMod12[i] = (byte)(perm[i] % 12);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FastFloor(float x) => x >= 0 ? (int)x : (int)x - 1;

    public float Sample(float x, float y)
    {
        float s = (x + y) * F2;
        int i = FastFloor(x + s), j = FastFloor(y + s);
        float t = (i + j) * G2;
        float x0 = x - (i - t), y0 = y - (j - t);

        int i1 = x0 > y0 ? 1 : 0, j1 = 1 - i1;
        float x1 = x0 - i1 + G2, y1 = y0 - j1 + G2;
        float x2 = x0 - 1.0f + 2.0f * G2, y2 = y0 - 1.0f + 2.0f * G2;

        int ii = i & 255, jj = j & 255;
        float n = 0.0f;

        float t0 = 0.5f - x0 * x0 - y0 * y0;
        if (t0 > 0)
        {
            int g = permMod12[ii + perm[jj]] * 3;
            t0 *= t0;
            n += t0 * t0 * (Grad3[g] * x0 + Grad3[g + 1] * y0);
        }

        float t1 = 0.5f - x1 * x1 - y1 * y1;
        if (t1 > 0)
        {
            int g = permMod12[ii + i1 + perm[jj + j1]] * 3;
            t1 *= t1;
            n += t1 * t1 * (Grad3[g] * x1 + Grad3[g + 1] * y1);
        }

        float t2 = 0.5f - x2 * x2 - y2 * y2;
        if (t2 > 0)
        {
            int g = permMod12[ii + 1 + perm[jj + 1]] * 3;
            t2 *= t2;
            n += t2 * t2 * (Grad3[g] * x2 + Grad3[g + 1] * y2);
        }

        return 70.0f * n;
    }

    public float Sample(float x, float y, float z)
    {
        float s = (x + y + z) * F3;
        int i = FastFloor(x + s), j = FastFloor(y + s), k = FastFloor(z + s);
        float t = (i + j + k) * G3;
        float x0 = x - (i - t), y0 = y - (j - t), z0 = z - (k - t);

        int i1, j1, k1, i2, j2, k2;
        if (x0 >= y0)
        {
            if (y0 >= z0) { i1 = 1; j1 = 0; k1 = 0; i2 = 1; j2 = 1; k2 = 0; }
            else if (x0 >= z0) { i1 = 1; j1 = 0; k1 = 0; i2 = 1; j2 = 0; k2 = 1; }
            else { i1 = 0; j1 = 0; k1 = 1; i2 = 1; j2 = 0; k2 = 1; }
        }
        else
        {
            if (y0 < z0) { i1 = 0; j1 = 0; k1 = 1; i2 = 0; j2 = 1; k2 = 1; }
            else if (x0 < z0) { i1 = 0; j1 = 1; k1 = 0; i2 = 0; j2 = 1; k2 = 1; }
            else { i1 = 0; j1 = 1; k1 = 0; i2 = 1; j2 = 1; k2 = 0; }
        }

        float x1 = x0 - i1 + G3, y1 = y0 - j1 + G3, z1 = z0 - k1 + G3;
        float x2 = x0 - i2 + 2.0f * G3, y2 = y0 - j2 + 2.0f * G3, z2 = z0 - k2 + 2.0f * G3;
        float x3 = x0 - 1.0f + 3.0f * G3, y3 = y0 - 1.0f + 3.0f * G3, z3 = z0 - 1.0f + 3.0f * G3;

        int ii = i & 255, jj = j & 255, kk = k & 255;
        float n = 0.0f;

        n += Corner(0.6f - x0 * x0 - y0 * y0 - z0 * z0, permMod12[ii + perm[jj + perm[kk]]], x0, y0, z0);
        n += Corner(0.6f - x1 * x1 - y1 * y1 - z1 * z1, permMod12[ii + i1 + perm[jj + j1 + perm[kk + k1]]], x1, y1, z1);
        n += Corner(0.6f - x2 * x2 - y2 * y2 - z2 * z2, permMod12[ii + i2 + perm[jj + j2 + perm[kk + k2]]], x2, y2, z2);
        n += Corner(0.6f - x3 * x3 - y3 * y3 - z3 * z3, permMod12[ii + 1 + perm[jj + 1 + perm[kk + 1]]], x3, y3, z3);

        return 32.0f * n;

        static float Corner(float t, int gi, float x, float y, float z)
        {
            if (t <= 0) return 0.0f;
            int g = gi * 3;
            t *= t;
            return t * t * (Grad3[g] * x + Grad3[g + 1] * y + Grad3[g + 2] * z);
        }
    }

    /// <summary>A few octaves on top of each other, every one twice as fine and half as strong. Hills with rocks on.</summary>
    public float Fractal(float x, float y, int octaves, float lacunarity = 2.0f, float gain = 0.5f)
    {
        float sum = 0.0f, amplitude = 1.0f, total = 0.0f;
        for (int i = 0; i < octaves; i++)
        {
            sum += Sample(x, y) * amplitude;
            total += amplitude;
            amplitude *= gain;
            x = x * lacunarity + 17.31f;
            y = y * lacunarity - 9.17f;
        }

        return sum / total;
    }

    /// <summary>
    /// Ridges, the absolute of the noise turned upside down and sharpened, every octave weighed by the one before so
    /// the crests get the detail and the valleys stay smooth. Mountain ranges, 0 to 1.
    /// </summary>
    public float Ridged(float x, float y, int octaves)
    {
        float sum = 0.0f, amplitude = 0.5f, weight = 1.0f, total = 0.0f;
        for (int i = 0; i < octaves; i++)
        {
            float n = 1.0f - MathF.Abs(Sample(x, y));
            n *= n;
            n *= weight;
            weight = Math.Clamp(n * 2.0f, 0.0f, 1.0f);
            sum += n * amplitude;
            total += amplitude;
            amplitude *= 0.5f;
            x = x * 2.03f + 31.7f;
            y = y * 2.03f - 11.3f;
        }

        return sum / total;
    }
}
