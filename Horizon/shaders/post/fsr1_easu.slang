// AMD FidelityFX Super Resolution 1, its first pass, EASU (edge adaptive spatial upsampling), blowing a scene drawn
// at a lower resolution up to the window before RCAS sharpens it (fsr1_rcas.slang). See
// Horizon.Rendering.Scaling.FrameScaler. Ported from ffx_fsr1.h version 1.20210629 and the approximations of ffx_a.h in
// GPUOpen-Effects/FidelityFX-FSR at commit a21ffb8f6c13233ba336352bdff293894c706575
// (https://github.com/GPUOpen-Effects/FidelityFX-FSR/blob/a21ffb8f6c13233ba336352bdff293894c706575/ffx-fsr/ffx_fsr1.h).
//
// It is FsrEasuF, the 32 bit one, as the header has it, the four gather4s a colour channel each, the luma times 2,
// FsrEasuSetF's direction and length, the 12 tap approximate lanczos and the deringing against the nearest 2x2. The
// header asks for input as it would go to an sRGB display, which is what the scene is (sRGB encoded colours in an RGBA8
// UNORM target), so nothing is converted. The gathers go through the scene's own sampler, linear and clamped to its
// edge, the way the header's GLSL example has them. What changed is only the plumbing, a full screen fragment pass, one
// pixel each. Alpha isn't EASU's, it's sampled bilinearly where the pixel lands and RCAS passes it on.
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
    // FsrEasuCon's four uint4s, as the floats they are
    float4 uCon0;
    float4 uCon1;
    float4 uCon2;
    float4 uCon3;
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;
BIND_TEXTURE(0) Sampler2D uSource;

// ffx_a.h's approximations
float PrxLoRcp(float a) { return asfloat(0x7ef07ebbu - asuint(a)); }
float PrxLoRsq(float a) { return asfloat(0x5f347d74u - (asuint(a) >> 1u)); }

float4 EasuR(float2 p) { return uSource.GatherRed(p); }
float4 EasuG(float2 p) { return uSource.GatherGreen(p); }
float4 EasuB(float2 p) { return uSource.GatherBlue(p); }

// FsrEasuTapF
void EasuTap(inout float3 aC, inout float aW, float2 off, float2 dir, float2 len, float lob, float clp, float3 c)
{
    float2 v;
    v.x = (off.x * (dir.x)) + (off.y * dir.y);
    v.y = (off.x * (-dir.y)) + (off.y * dir.x);
    v *= len;
    float d2 = v.x * v.x + v.y * v.y;
    d2 = min(d2, clp);
    float wB = (2.0 / 5.0) * d2 + (-1.0);
    float wA = lob * d2 + (-1.0);
    wB *= wB;
    wA *= wA;
    wB = (25.0 / 16.0) * wB + (-(25.0 / 16.0 - 1.0));
    float w = wB * wA;
    aC += c * w;
    aW += w;
}

// FsrEasuSetF, with the bilinear weight of the one it's asked for
void EasuSet(inout float2 dir, inout float len, float w, float lA, float lB, float lC, float lD, float lE)
{
    float dc = lD - lC;
    float cb = lC - lB;
    float lenX = max(abs(dc), abs(cb));
    lenX = PrxLoRcp(lenX);
    float dirX = lD - lB;
    dir.x += dirX * w;
    lenX = saturate(abs(dirX) * lenX);
    lenX *= lenX;
    len += lenX * w;

    float ec = lE - lC;
    float ca = lC - lA;
    float lenY = max(abs(ec), abs(ca));
    lenY = PrxLoRcp(lenY);
    float dirY = lE - lA;
    dir.y += dirY * w;
    lenY = saturate(abs(dirY) * lenY);
    lenY *= lenY;
    len += lenY * w;
}

// FsrEasuF for the output pixel at ip
float3 Easu(uint2 ip)
{
    float2 pp = float2(ip) * params.uCon0.xy + params.uCon0.zw;
    float2 fp = floor(pp);
    pp -= fp;

    float2 p0 = fp * params.uCon1.xy + params.uCon1.zw;
    float2 p1 = p0 + params.uCon2.xy;
    float2 p2 = p0 + params.uCon2.zw;
    float2 p3 = p0 + params.uCon3.xy;
    float4 bczzR = EasuR(p0), bczzG = EasuG(p0), bczzB = EasuB(p0);
    float4 ijfeR = EasuR(p1), ijfeG = EasuG(p1), ijfeB = EasuB(p1);
    float4 klhgR = EasuR(p2), klhgG = EasuG(p2), klhgB = EasuB(p2);
    float4 zzonR = EasuR(p3), zzonG = EasuG(p3), zzonB = EasuB(p3);

    float4 bczzL = bczzB * 0.5 + (bczzR * 0.5 + bczzG);
    float4 ijfeL = ijfeB * 0.5 + (ijfeR * 0.5 + ijfeG);
    float4 klhgL = klhgB * 0.5 + (klhgR * 0.5 + klhgG);
    float4 zzonL = zzonB * 0.5 + (zzonR * 0.5 + zzonG);
    float bL = bczzL.x, cL = bczzL.y, iL = ijfeL.x, jL = ijfeL.y, fL = ijfeL.z, eL = ijfeL.w;
    float kL = klhgL.x, lL = klhgL.y, hL = klhgL.z, gL = klhgL.w, oL = zzonL.z, nL = zzonL.w;

    float2 dir = float2(0.0, 0.0);
    float len = 0.0;
    EasuSet(dir, len, (1.0 - pp.x) * (1.0 - pp.y), bL, eL, fL, gL, jL);
    EasuSet(dir, len, pp.x * (1.0 - pp.y), cL, fL, gL, hL, kL);
    EasuSet(dir, len, (1.0 - pp.x) * pp.y, fL, iL, jL, kL, nL);
    EasuSet(dir, len, pp.x * pp.y, gL, jL, kL, lL, oL);

    float2 dir2 = dir * dir;
    float dirR = dir2.x + dir2.y;
    bool zro = dirR < (1.0 / 32768.0);
    dirR = PrxLoRsq(dirR);
    dirR = zro ? 1.0 : dirR;
    dir.x = zro ? 1.0 : dir.x;
    dir *= float2(dirR, dirR);
    len = len * 0.5;
    len *= len;
    float stretch = (dir.x * dir.x + dir.y * dir.y) * PrxLoRcp(max(abs(dir.x), abs(dir.y)));
    float2 len2 = float2(1.0 + (stretch - 1.0) * len, 1.0 + (-0.5) * len);
    float lob = 0.5 + ((1.0 / 4.0 - 0.04) - 0.5) * len;
    float clp = PrxLoRcp(lob);

    float3 f = float3(ijfeR.z, ijfeG.z, ijfeB.z), g = float3(klhgR.w, klhgG.w, klhgB.w);
    float3 j = float3(ijfeR.y, ijfeG.y, ijfeB.y), k = float3(klhgR.x, klhgG.x, klhgB.x);
    float3 min4 = min(min(f, min(g, j)), k);
    float3 max4 = max(max(f, max(g, j)), k);

    float3 aC = float3(0.0, 0.0, 0.0);
    float aW = 0.0;
    EasuTap(aC, aW, float2( 0.0, -1.0) - pp, dir, len2, lob, clp, float3(bczzR.x, bczzG.x, bczzB.x));
    EasuTap(aC, aW, float2( 1.0, -1.0) - pp, dir, len2, lob, clp, float3(bczzR.y, bczzG.y, bczzB.y));
    EasuTap(aC, aW, float2(-1.0,  1.0) - pp, dir, len2, lob, clp, float3(ijfeR.x, ijfeG.x, ijfeB.x));
    EasuTap(aC, aW, float2( 0.0,  1.0) - pp, dir, len2, lob, clp, j);
    EasuTap(aC, aW, float2( 0.0,  0.0) - pp, dir, len2, lob, clp, f);
    EasuTap(aC, aW, float2(-1.0,  0.0) - pp, dir, len2, lob, clp, float3(ijfeR.w, ijfeG.w, ijfeB.w));
    EasuTap(aC, aW, float2( 1.0,  1.0) - pp, dir, len2, lob, clp, k);
    EasuTap(aC, aW, float2( 2.0,  1.0) - pp, dir, len2, lob, clp, float3(klhgR.y, klhgG.y, klhgB.y));
    EasuTap(aC, aW, float2( 2.0,  0.0) - pp, dir, len2, lob, clp, float3(klhgR.z, klhgG.z, klhgB.z));
    EasuTap(aC, aW, float2( 1.0,  0.0) - pp, dir, len2, lob, clp, g);
    EasuTap(aC, aW, float2( 1.0,  2.0) - pp, dir, len2, lob, clp, float3(zzonR.z, zzonG.z, zzonB.z));
    EasuTap(aC, aW, float2( 0.0,  2.0) - pp, dir, len2, lob, clp, float3(zzonR.w, zzonG.w, zzonB.w));

    return min(max4, max(min4, aC * (1.0 / aW)));
}

[shader("fragment")]
float4 fsMain(ScreenVaryings input) : SV_Target0
{
    return float4(Easu(uint2(input.position.xy)), uSource.Sample(input.texCoords).a);
}
