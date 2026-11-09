// Ported from AMD FidelityFX CAS, ffx_cas.h version 1.20190610, GPUOpen-Effects/FidelityFX-CAS at commit
// 9fabcc9a2c45f958aff55ddfda337e74ef894b7f. The notice the license asks for is in shaders/post/cas.slang and in
// THIRD-PARTY-NOTICES.md at the root of the repository, and is repeated here.
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
/// What CAS's CasSetup works out on the CPU for the shader, the float half of its two uint4s. The packed 16 bit half
/// isn't used, the engine runs the 32 bit filter. Worked out the way the header does it, the same multiplies by the same
/// reciprocals, so the shader and the reference in the tests get the very same bits.
/// </summary>
/// <param name="Scale">Scene pixels to a window pixel each way (const0.xy).</param>
/// <param name="Offset">Where the first window pixel's middle lands in the scene, less half a pixel (const0.zw).</param>
/// <param name="Peak">The negative lobe, -1/8 at no sharpness to -1/5 at full (const1.x).</param>
public readonly record struct CasConstants(Vector2 Scale, Vector2 Offset, float Peak)
{
    /// <summary>CAS's CAS_AREA_LIMIT, the most it upsamples, in area.</summary>
    public const float AREA_LIMIT = 4.0f;

    /// <summary>CasSupportScaling, whether CAS can take an input of a size to an output of another.</summary>
    public static bool SupportsScaling(float outputWidth, float outputHeight, float inputWidth, float inputHeight) =>
        outputWidth * outputHeight * (1.0f / (inputWidth * inputHeight)) <= AREA_LIMIT;

    /// <summary>CasSetup for a sharpness (0 to 1, clamped like ASatF1) and the two sizes.</summary>
    public static CasConstants For(float sharpness, float inputWidth, float inputHeight, float outputWidth, float outputHeight)
    {
        float rcpX = 1.0f / outputWidth, rcpY = 1.0f / outputHeight;
        var scale = new Vector2(inputWidth * rcpX, inputHeight * rcpY);
        var offset = new Vector2(0.5f * inputWidth * rcpX - 0.5f, 0.5f * inputHeight * rcpY - 0.5f);

        // ALerpF1(8.0, 5.0, ASatF1(sharpness)) is 8 + (5 - 8) * t
        float t = float.IsNaN(sharpness) ? 0.0f : Math.Clamp(sharpness, 0.0f, 1.0f);
        float peak = -(1.0f / (8.0f + (5.0f - 8.0f) * t));
        return new CasConstants(scale, offset, peak);
    }
}
