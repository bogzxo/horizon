// AMD FidelityFX Super Resolution 1, its second pass, RCAS (robust contrast adaptive sharpening), over what EASU
// (fsr1_easu.slang) made at the window's size. Ported from ffx_fsr1.h version 1.20210629 and ffx_a.h in
// GPUOpen-Effects/FidelityFX-FSR at commit a21ffb8f6c13233ba336352bdff293894c706575
// (https://github.com/GPUOpen-Effects/FidelityFX-FSR/blob/a21ffb8f6c13233ba336352bdff293894c706575/ffx-fsr/ffx_fsr1.h).
//
// It is FsrRcasF, the 32 bit one, with FSR_RCAS_PASSTHROUGH_ALPHA and without FSR_RCAS_DENOISE, the way the header
// suggests (grain, if any, goes after). FsrRcasInputF is empty, the header says EASU's output goes straight in. Loads
// are clamped to the edge of the picture, the header leaves out of range loads to the API, and the noise detection
// (FSR_RCAS_DENOISE only) is left out. A full screen fragment pass
// rather than a compute one, a pixel each.
//
// Copyright (c) 2021 Advanced Micro Devices, Inc. All rights reserved.
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files(the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and / or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions :
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

#include <common/screen.slang>

struct Params
{
    // FsrRcasCon's con.x, the sharpness as a linear amount (2 to the minus the stops)
    float uSharpness;

    // How big the picture is, for keeping the loads inside of it
    float2 uSize;
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;
BIND_TEXTURE(0) Sampler2D uSource;

// This is set at the limit of providing unnatural results for sharpening, FSR_RCAS_LIMIT
static const float RCAS_LIMIT = 0.25 - (1.0 / 16.0);

float PrxMedRcp(float a) { float b = asfloat(0x7ef19fffu - asuint(a)); return b * (-b * a + 2.0); }
float Min3(float x, float y, float z) { return min(x, min(y, z)); }
float Max3(float x, float y, float z) { return max(x, max(y, z)); }

float4 RcasLoad(int2 p)
{
    int2 clamped = clamp(p, int2(0, 0), int2(params.uSize) - int2(1, 1));
    return uSource.Load(int3(clamped, 0));
}

[shader("fragment")]
float4 fsMain(ScreenVaryings input) : SV_Target0
{
    int2 sp = int2(input.position.xy);
    float3 b = RcasLoad(sp + int2( 0, -1)).rgb;
    float3 d = RcasLoad(sp + int2(-1,  0)).rgb;
    float4 ee = RcasLoad(sp);
    float3 e = ee.rgb;
    float pixA = ee.a;
    float3 f = RcasLoad(sp + int2( 1,  0)).rgb;
    float3 h = RcasLoad(sp + int2( 0,  1)).rgb;

    float3 mn4 = float3(min(Min3(b.r, d.r, f.r), h.r), min(Min3(b.g, d.g, f.g), h.g), min(Min3(b.b, d.b, f.b), h.b));
    float3 mx4 = float3(max(Max3(b.r, d.r, f.r), h.r), max(Max3(b.g, d.g, f.g), h.g), max(Max3(b.b, d.b, f.b), h.b));

    float2 peakC = float2(1.0, -1.0 * 4.0);
    // The limiters, high precision reciprocals as the header says. A ring of black (or of white) would divide by
    // zero there and make a NaN that the header leaves to how the GPU takes the max of one, so the divisors are kept a
    // hair off zero, which changes nothing anywhere else
    float3 hitMin = min(mn4, e) * (1.0 / max(4.0 * mx4, 1e-6));
    float3 hitMax = (peakC.x - max(mx4, e)) * (1.0 / min(4.0 * mn4 + peakC.y, -1e-6));
    float3 lobeRGB = max(-hitMin, hitMax);
    float lobe = max(-RCAS_LIMIT, min(Max3(lobeRGB.r, lobeRGB.g, lobeRGB.b), 0.0)) * params.uSharpness;

    float rcpL = PrxMedRcp(4.0 * lobe + 1.0);
    float3 pix = (lobe * b + lobe * d + lobe * h + lobe * f + e) * rcpL;
    return float4(pix, pixA);
}
