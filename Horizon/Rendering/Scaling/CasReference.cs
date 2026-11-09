// The same port of AMD FidelityFX CAS as shaders/post/cas.slang, on the CPU. From ffx_cas.h version 1.20190610,
// GPUOpen-Effects/FidelityFX-CAS at commit 9fabcc9a2c45f958aff55ddfda337e74ef894b7f, under the MIT license below.
//
// Copyright (c) 2017-2019 Advanced Micro Devices, Inc. All rights reserved.
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation
// files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy,
// modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following conditions:
// The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
// Software.
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
// WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.  IN NO EVENT SHALL THE AUTHORS OR
// COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE,
// ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

using System.Numerics;

namespace Horizon.Rendering.Scaling;

/// <summary>
/// CAS's scaling filter worked out on the CPU, line for line what the shader does, for the tests and the GPU check
/// to hold the shader to. Slow and only for that. Colours go in and come out as 0 to 1 sRGB encoded values, the way
/// the scene's target has them, through the same gamma 2.0 the shader uses.
/// </summary>
public static class CasReference
{
    /// <summary>Upscales a picture (RGB, 0 to 1, rows top first) to another size, the way the shader would.</summary>
    public static Vector3[] Upscale(ReadOnlySpan<Vector3> source, int width, int height, int outputWidth, int outputHeight, float sharpness)
    {
        CasConstants constants = CasConstants.For(sharpness, width, height, outputWidth, outputHeight);
        var output = new Vector3[outputWidth * outputHeight];
        for (int y = 0; y < outputHeight; y++)
        {
            for (int x = 0; x < outputWidth; x++)
            {
                Vector3 light = Filter(source, width, height, (uint)x, (uint)y, constants);
                output[y * outputWidth + x] = new Vector3(MathF.Sqrt(light.X), MathF.Sqrt(light.Y), MathF.Sqrt(light.Z));
            }
        }

        return output;
    }

    // ffx_a.h's approximations, bit for bit
    public static float PrxLoRcp(float a) => BitConverter.UInt32BitsToSingle(0x7ef07ebbu - BitConverter.SingleToUInt32Bits(a));

    public static float PrxMedRcp(float a)
    {
        float b = BitConverter.UInt32BitsToSingle(0x7ef19fffu - BitConverter.SingleToUInt32Bits(a));
        return b * (-b * a + 2.0f);
    }

    public static float PrxLoSqrt(float a) => BitConverter.UInt32BitsToSingle((BitConverter.SingleToUInt32Bits(a) >> 1) + 0x1fbc4639u);

    private static float Sat(float a) => Math.Clamp(a, 0.0f, 1.0f);

    private static float Min3(float x, float y, float z) => MathF.Min(x, MathF.Min(y, z));

    private static float Max3(float x, float y, float z) => MathF.Max(x, MathF.Max(y, z));

    // CasLoad and CasInput, clamped to the edge and squared
    private static Vector3 Load(ReadOnlySpan<Vector3> source, int width, int height, int x, int y)
    {
        Vector3 c = source[Math.Clamp(y, 0, height - 1) * width + Math.Clamp(x, 0, width - 1)];
        return c * c;
    }

    /// <summary>CasFilter's scaling path for one output pixel, in linear.</summary>
    public static Vector3 Filter(ReadOnlySpan<Vector3> source, int width, int height, uint ix, uint iy, in CasConstants constants)
    {
        float ppx = ix * constants.Scale.X + constants.Offset.X;
        float ppy = iy * constants.Scale.Y + constants.Offset.Y;
        float fpx = MathF.Floor(ppx), fpy = MathF.Floor(ppy);
        ppx -= fpx;
        ppy -= fpy;
        int sx = (int)fpx, sy = (int)fpy;

        Vector3 b = Load(source, width, height, sx, sy - 1);
        Vector3 e = Load(source, width, height, sx - 1, sy);
        Vector3 f = Load(source, width, height, sx, sy);
        Vector3 c = Load(source, width, height, sx + 1, sy - 1);
        Vector3 g = Load(source, width, height, sx + 1, sy);
        Vector3 h = Load(source, width, height, sx + 2, sy);
        Vector3 i = Load(source, width, height, sx - 1, sy + 1);
        Vector3 j = Load(source, width, height, sx, sy + 1);
        Vector3 n = Load(source, width, height, sx, sy + 2);
        Vector3 k = Load(source, width, height, sx + 1, sy + 1);
        Vector3 l = Load(source, width, height, sx + 2, sy + 1);
        Vector3 o = Load(source, width, height, sx + 1, sy + 2);

        // The corners (a, d, m and p) are loaded by the header and never used without CAS_BETTER_DIAGONALS, so they are
        // left out here. Green alone decides the weights, as the header does by default
        float mnfG = Min3(Min3(b.Y, e.Y, f.Y), g.Y, j.Y), mxfG = Max3(Max3(b.Y, e.Y, f.Y), g.Y, j.Y);
        float mngG = Min3(Min3(c.Y, f.Y, g.Y), h.Y, k.Y), mxgG = Max3(Max3(c.Y, f.Y, g.Y), h.Y, k.Y);
        float mnjG = Min3(Min3(f.Y, i.Y, j.Y), k.Y, n.Y), mxjG = Max3(Max3(f.Y, i.Y, j.Y), k.Y, n.Y);
        float mnkG = Min3(Min3(g.Y, j.Y, k.Y), l.Y, o.Y), mxkG = Max3(Max3(g.Y, j.Y, k.Y), l.Y, o.Y);

        float ampfG = PrxLoSqrt(Sat(MathF.Min(mnfG, 1.0f - mxfG) * PrxLoRcp(mxfG)));
        float ampgG = PrxLoSqrt(Sat(MathF.Min(mngG, 1.0f - mxgG) * PrxLoRcp(mxgG)));
        float ampjG = PrxLoSqrt(Sat(MathF.Min(mnjG, 1.0f - mxjG) * PrxLoRcp(mxjG)));
        float ampkG = PrxLoSqrt(Sat(MathF.Min(mnkG, 1.0f - mxkG) * PrxLoRcp(mxkG)));

        float peak = constants.Peak;
        float wfG = ampfG * peak, wgG = ampgG * peak, wjG = ampjG * peak, wkG = ampkG * peak;

        float s = (1.0f - ppx) * (1.0f - ppy);
        float t = ppx * (1.0f - ppy);
        float u = (1.0f - ppx) * ppy;
        float v = ppx * ppy;
        const float thinB = 1.0f / 32.0f;
        s *= PrxLoRcp(thinB + (mxfG - mnfG));
        t *= PrxLoRcp(thinB + (mxgG - mngG));
        u *= PrxLoRcp(thinB + (mxjG - mnjG));
        v *= PrxLoRcp(thinB + (mxkG - mnkG));

        float qbeG = wfG * s;
        float qchG = wgG * t;
        float qfG = wgG * t + wjG * u + s;
        float qgG = wfG * s + wkG * v + t;
        float qjG = wfG * s + wkG * v + u;
        float qkG = wgG * t + wjG * u + v;
        float qinG = wjG * u;
        float qloG = wkG * v;

        float rcpWG = PrxMedRcp(2.0f * qbeG + 2.0f * qchG + 2.0f * qinG + 2.0f * qloG + qfG + qgG + qjG + qkG);
        Vector3 pix = (b * qbeG + e * qbeG + c * qchG + h * qchG + i * qinG + n * qinG + l * qloG + o * qloG + f * qfG + g * qgG + j * qjG + k * qkG) * rcpWG;
        return Vector3.Clamp(pix, Vector3.Zero, Vector3.One);
    }
}
