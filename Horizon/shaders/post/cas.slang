// AMD FidelityFX CAS (contrast adaptive sharpening) with its upsampling, blowing a scene drawn at a lower resolution
// up to the window, see Horizon.Rendering.Scaling.FrameScaler. Ported from ffx_cas.h version 1.20190610 of
// GPUOpen-Effects/FidelityFX-CAS at commit 9fabcc9a2c45f958aff55ddfda337e74ef894b7f
// (https://github.com/GPUOpen-Effects/FidelityFX-CAS/blob/9fabcc9a2c45f958aff55ddfda337e74ef894b7f/ffx-cas/ffx_cas.h).
//
// What it is is CasFilter's scaling path as the header has it by default, 32 bit, without CAS_BETTER_DIAGONALS,
// CAS_GO_SLOWER or CAS_SLOW, so the green channel's weights do all three channels and the reciprocals and the square
// root are the header's own bit trick approximations (APrxLoRcpF1, APrxMedRcpF1, APrxLoSqrtF1 from ffx_a.h).
// What changed is only the plumbing. It runs as a full screen fragment pass rather than a compute shader, because a
// swapchain picture can't be counted on to take storage writes, and one pixel is shaded per invocation, which the
// filter is written per pixel for anyway. CasLoad clamps to the edge of the scene, the header leaves loads outside of
// it to whatever the API does. CasInput squares the colours and the result is square rooted, the header's own gamma
// 2.0 way for 8 bit UNORM input, which is what the scene is (sRGB encoded colours in an RGBA8 UNORM target). Alpha
// isn't something CAS does, it's sampled bilinearly where the pixel lands.
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

#include <common/screen.slang>

struct Params
{
    // const0 of CasSetup, scene pixels a window pixel (xy) and where the first one lands (zw)
    float4 uScaleOffset;

    // const1.x, the negative lobe
    float uPeak;

    // How big the scene is, for keeping the loads inside of it
    float2 uSceneSize;
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;
BIND_TEXTURE(0) Sampler2D uSource;

// ffx_a.h's approximations, the same bits on every GPU
float PrxLoRcp(float a) { return asfloat(0x7ef07ebbu - asuint(a)); }
float PrxMedRcp(float a) { float b = asfloat(0x7ef19fffu - asuint(a)); return b * (-b * a + 2.0); }
float PrxLoSqrt(float a) { return asfloat((asuint(a) >> 1u) + 0x1fbc4639u); }
float Min3(float x, float y, float z) { return min(x, min(y, z)); }
float Max3(float x, float y, float z) { return max(x, max(y, z)); }

// CasLoad and CasInput together, gamma 2.0 into linear
float3 CasLoad(int2 p)
{
    int2 clamped = clamp(p, int2(0, 0), int2(params.uSceneSize) - int2(1, 1));
    float3 c = uSource.Load(int3(clamped, 0)).rgb;
    return c * c;
}

// CasFilter's scaling path for the pixel at ip, in linear
float3 CasFilter(uint2 ip)
{
    float2 pp = float2(ip) * params.uScaleOffset.xy + params.uScaleOffset.zw;
    float2 fp = floor(pp);
    pp -= fp;
    int2 sp = int2(fp);

    //  a b c d
    //  e f g h
    //  i j k l
    //  m n o p
    float3 a = CasLoad(sp + int2(-1, -1));
    float3 b = CasLoad(sp + int2( 0, -1));
    float3 e = CasLoad(sp + int2(-1,  0));
    float3 f = CasLoad(sp);
    float3 c = CasLoad(sp + int2( 1, -1));
    float3 d = CasLoad(sp + int2( 2, -1));
    float3 g = CasLoad(sp + int2( 1,  0));
    float3 h = CasLoad(sp + int2( 2,  0));
    float3 i = CasLoad(sp + int2(-1,  1));
    float3 j = CasLoad(sp + int2( 0,  1));
    float3 m = CasLoad(sp + int2(-1,  2));
    float3 n = CasLoad(sp + int2( 0,  2));
    float3 k = CasLoad(sp + int2( 1,  1));
    float3 l = CasLoad(sp + int2( 2,  1));
    float3 o = CasLoad(sp + int2( 1,  2));
    float3 p = CasLoad(sp + int2( 2,  2));

    // Soft min and max of the four crosses round f, g, j and k
    float3 mnf = float3(Min3(Min3(b.r, e.r, f.r), g.r, j.r), Min3(Min3(b.g, e.g, f.g), g.g, j.g), Min3(Min3(b.b, e.b, f.b), g.b, j.b));
    float3 mxf = float3(Max3(Max3(b.r, e.r, f.r), g.r, j.r), Max3(Max3(b.g, e.g, f.g), g.g, j.g), Max3(Max3(b.b, e.b, f.b), g.b, j.b));
    float3 mng = float3(Min3(Min3(c.r, f.r, g.r), h.r, k.r), Min3(Min3(c.g, f.g, g.g), h.g, k.g), Min3(Min3(c.b, f.b, g.b), h.b, k.b));
    float3 mxg = float3(Max3(Max3(c.r, f.r, g.r), h.r, k.r), Max3(Max3(c.g, f.g, g.g), h.g, k.g), Max3(Max3(c.b, f.b, g.b), h.b, k.b));
    float3 mnj = float3(Min3(Min3(f.r, i.r, j.r), k.r, n.r), Min3(Min3(f.g, i.g, j.g), k.g, n.g), Min3(Min3(f.b, i.b, j.b), k.b, n.b));
    float3 mxj = float3(Max3(Max3(f.r, i.r, j.r), k.r, n.r), Max3(Max3(f.g, i.g, j.g), k.g, n.g), Max3(Max3(f.b, i.b, j.b), k.b, n.b));
    float3 mnk = float3(Min3(Min3(g.r, j.r, k.r), l.r, o.r), Min3(Min3(g.g, j.g, k.g), l.g, o.g), Min3(Min3(g.b, j.b, k.b), l.b, o.b));
    float3 mxk = float3(Max3(Max3(g.r, j.r, k.r), l.r, o.r), Max3(Max3(g.g, j.g, k.g), l.g, o.g), Max3(Max3(g.b, j.b, k.b), l.b, o.b));

    // Smooth minimum distance to the signal limit over the smooth max, shaped by a square root. Only green's is used
    // past this point, the header leaves red's and blue's to dead code removal
    float ampfG = PrxLoSqrt(saturate(min(mnf.g, 1.0 - mxf.g) * PrxLoRcp(mxf.g)));
    float ampgG = PrxLoSqrt(saturate(min(mng.g, 1.0 - mxg.g) * PrxLoRcp(mxg.g)));
    float ampjG = PrxLoSqrt(saturate(min(mnj.g, 1.0 - mxj.g) * PrxLoRcp(mxj.g)));
    float ampkG = PrxLoSqrt(saturate(min(mnk.g, 1.0 - mxk.g) * PrxLoRcp(mxk.g)));

    float peak = params.uPeak;
    float wfG = ampfG * peak;
    float wgG = ampgG * peak;
    float wjG = ampjG * peak;
    float wkG = ampkG * peak;

    // Bilinear weights of the four, thinned by their contrast to hide the bilinear look
    float s = (1.0 - pp.x) * (1.0 - pp.y);
    float t = pp.x * (1.0 - pp.y);
    float u = (1.0 - pp.x) * pp.y;
    float v = pp.x * pp.y;
    float thinB = 1.0 / 32.0;
    s *= PrxLoRcp(thinB + (mxf.g - mnf.g));
    t *= PrxLoRcp(thinB + (mxg.g - mng.g));
    u *= PrxLoRcp(thinB + (mxj.g - mnj.g));
    v *= PrxLoRcp(thinB + (mxk.g - mnk.g));

    float qbeG = wfG * s;
    float qchG = wgG * t;
    float qfG = wgG * t + wjG * u + s;
    float qgG = wfG * s + wkG * v + t;
    float qjG = wfG * s + wkG * v + u;
    float qkG = wgG * t + wjG * u + v;
    float qinG = wjG * u;
    float qloG = wkG * v;

    float rcpWG = PrxMedRcp(2.0 * qbeG + 2.0 * qchG + 2.0 * qinG + 2.0 * qloG + qfG + qgG + qjG + qkG);
    float3 pix = (b * qbeG + e * qbeG + c * qchG + h * qchG + i * qinG + n * qinG + l * qloG + o * qloG + f * qfG + g * qgG + j * qjG + k * qkG) * rcpWG;
    return saturate(pix);
}

[shader("fragment")]
float4 fsMain(ScreenVaryings input) : SV_Target0
{
    float3 light = CasFilter(uint2(input.position.xy));

    // Back out of linear the way the header says for gamma 2.0
    return float4(sqrt(light), uSource.Sample(input.texCoords).a);
}
