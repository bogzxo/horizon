// Snapdragon Game Super Resolution 1, upscaling and sharpening in one pass, blowing a scene drawn at a lower
// resolution up to the window. See Horizon.Rendering.Scaling.FrameScaler. Ported from
// sgsr/v1/include/glsl/sgsr1_shader_mobile.frag of SnapdragonGameStudios/snapdragon-gsr at commit
// d926f074bcb9d714e179f1ce0fcb9ee2eeb5074e (GSR 1 version 1.0.0,
// https://github.com/SnapdragonGameStudios/snapdragon-gsr/tree/d926f074bcb9d714e179f1ce0fcb9ee2eeb5074e/sgsr/v1).
//
// It is the restored original GLSL mobile path, operation mode 1 (RGBA, green standing in for luma), without
// UseEdgeDirection, edge threshold 8/255 (what the README suggests for mobile), the 12 tap fastLanczos2 weights over
// four gathers, the clamp to the four nearest and the saturate. The gathers and the centre sample go through the
// scene's sampler, linear and clamped to its edge. ViewportInfo is
// (1/width, 1/height, width, height) of the scene. Colours go in as they would go to the screen, nothing converted,
// the way the shader takes them.
// What changed. The math is 32 bit where the upstream shader uses mediump, a GPU with no 16 bit floats has the same answer and one
// with them loses nothing that shows. The edge sharpness is a uniform from the settings (1 to 2, the README's range)
// rather than the define, and the sum the deviation is worked out of is kept a hair off zero, where the header would
// divide by it. The header writes alpha 1 (it assumes alpha isn't used), here it's the scene's alpha
// sampled where the pixel lands, the way the other upscalers pass it on. A full screen fragment pass, a pixel each.
// Keep the restored GLSL's 0.55 spatial weight, 2.181818/sum normalization and +/-23/255 luma correction cap together.
// The older HLSL file at the same upstream revision differs in all three and can create large brightness halos.
//
// Copyright (c) 2025, Qualcomm Innovation Center, Inc. All rights reserved.
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are met:
//
// 1. Redistributions of source code must retain the above copyright notice,
//    this list of conditions and the following disclaimer.
//
// 2. Redistributions in binary form must reproduce the above copyright notice,
//    this list of conditions and the following disclaimer in the documentation
//    and/or other materials provided with the distribution.
//
// 3. Neither the name of the copyright holder nor the names of its contributors
//    may be used to endorse or promote products derived from this software
//    without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
// AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
// IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
// ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE
// LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
// CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
// SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
// INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
// CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
// ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
// POSSIBILITY OF SUCH DAMAGE.
//
// SPDX-License-Identifier: BSD-3-Clause

#include <common/screen.slang>

struct Params
{
    // ViewportInfo, a texel of the scene (xy) and its size in texels (zw)
    float4 uViewportInfo;

    // EdgeSharpness, 1 to 2
    float uEdgeSharpness;
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;
BIND_TEXTURE(0) Sampler2D uSource;

static const float EDGE_THRESHOLD = 8.0 / 255.0;

float fastLanczos2(float x)
{
    float wA = x - 4.0;
    float wB = x * wA - wA;
    wA *= wA;
    return wB * wA;
}

float2 weightY(float dx, float dy, float c, float std)
{
    float x = ((dx * dx) + (dy * dy)) * 0.55 + clamp(abs(c) * std, 0.0, 1.0);
    float w = fastLanczos2(x);
    return float2(w, w * c);
}

// Mode 1, the green channel's four texels round a point
float4 SgsrGreen(float2 p) { return uSource.GatherGreen(p); }

float4 Sgsr(float2 uv)
{
    float4 con1 = params.uViewportInfo;
    float edgeSharpness = params.uEdgeSharpness;

    float4 sampled = uSource.SampleLevel(uv, 0.0);
    float4 pix = float4(sampled.xyz, 0.0);

    float2 imgCoord = ((uv.xy * con1.zw) + float2(-0.5, 0.5));
    float2 imgCoordPixel = floor(imgCoord);
    float2 coord = (imgCoordPixel * con1.xy);
    float2 pl = (imgCoord + (-imgCoordPixel));
    float4 left = SgsrGreen(coord);

    float edgeVote = abs(left.z - left.y) + abs(pix.y - left.y) + abs(pix.y - left.z);
    if (edgeVote > EDGE_THRESHOLD)
    {
        coord.x += con1.x;

        float4 right = SgsrGreen(coord + float2(con1.x, 0.0));
        float4 upDown;
        upDown.xy = SgsrGreen(coord + float2(0.0, -con1.y)).wz;
        upDown.zw = SgsrGreen(coord + float2(0.0, con1.y)).yx;

        float mean = (left.y + left.z + right.x + right.w) * 0.25;
        left = left - float4(mean, mean, mean, mean);
        right = right - float4(mean, mean, mean, mean);
        upDown = upDown - float4(mean, mean, mean, mean);
        pix.w = pix.y - mean;

        float sum = (((((abs(left.x) + abs(left.y)) + abs(left.z)) + abs(left.w)) + (((abs(right.x) + abs(right.y)) + abs(right.z)) + abs(right.w))) + (((abs(upDown.x) + abs(upDown.y)) + abs(upDown.z)) + abs(upDown.w)));
        float std = 2.181818 / max(sum, 1e-6);

        float2 aWY = weightY(pl.x, pl.y + 1.0, upDown.x, std);
        aWY += weightY(pl.x - 1.0, pl.y + 1.0, upDown.y, std);
        aWY += weightY(pl.x - 1.0, pl.y - 2.0, upDown.z, std);
        aWY += weightY(pl.x, pl.y - 2.0, upDown.w, std);
        aWY += weightY(pl.x + 1.0, pl.y - 1.0, left.x, std);
        aWY += weightY(pl.x, pl.y - 1.0, left.y, std);
        aWY += weightY(pl.x, pl.y, left.z, std);
        aWY += weightY(pl.x + 1.0, pl.y, left.w, std);
        aWY += weightY(pl.x - 1.0, pl.y - 1.0, right.x, std);
        aWY += weightY(pl.x - 2.0, pl.y - 1.0, right.y, std);
        aWY += weightY(pl.x - 2.0, pl.y, right.z, std);
        aWY += weightY(pl.x - 1.0, pl.y, right.w, std);

        float finalY = aWY.y / aWY.x;

        float max4 = max(max(left.y, left.z), max(right.x, right.w));
        float min4 = min(min(left.y, left.z), min(right.x, right.w));
        finalY = clamp(edgeSharpness * finalY, min4, max4);

        float deltaY = finalY - pix.w;
        deltaY = clamp(deltaY, -23.0 / 255.0, 23.0 / 255.0);

        pix.x = saturate((pix.x + deltaY));
        pix.y = saturate((pix.y + deltaY));
        pix.z = saturate((pix.z + deltaY));
    }

    return float4(pix.xyz, sampled.a);
}

[shader("fragment")]
float4 fsMain(ScreenVaryings input) : SV_Target0
{
    return Sgsr(input.texCoords);
}
