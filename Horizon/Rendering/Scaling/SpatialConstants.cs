// FsrEasuCon and FsrRcasCon are ported from ffx_fsr1.h version 1.20210629 in GPUOpen-Effects/FidelityFX-FSR at commit
// a21ffb8f6c13233ba336352bdff293894c706575, under the MIT license in shaders/post/fsr1_easu.slang and
// THIRD-PARTY-NOTICES.md. GSR 1's ViewportInfo and settings are from sgsr1_shader_mobile.frag and its README in
// SnapdragonGameStudios/snapdragon-gsr at commit d926f074bcb9d714e179f1ce0fcb9ee2eeb5074e, under the BSD 3-clause
// license in shaders/post/gsr1.slang and THIRD-PARTY-NOTICES.md.

using System.Numerics;

namespace Horizon.Rendering.Scaling;

/// <summary>
/// FsrEasuCon's four uint4s as the floats they hold, for a scene of a size (all of it, no dynamic resolution viewport
/// inside a bigger target) to a window of another. Worked out the way the header does it, multiplies by reciprocals.
/// </summary>
public readonly record struct EasuConstants(Vector4 Con0, Vector4 Con1, Vector4 Con2, Vector4 Con3)
{
    public static EasuConstants For(float inputWidth, float inputHeight, float outputWidth, float outputHeight)
    {
        float rcpOutX = 1.0f / outputWidth, rcpOutY = 1.0f / outputHeight;
        float rcpInX = 1.0f / inputWidth, rcpInY = 1.0f / inputHeight;
        return new EasuConstants(
            new Vector4(inputWidth * rcpOutX, inputHeight * rcpOutY, 0.5f * inputWidth * rcpOutX - 0.5f, 0.5f * inputHeight * rcpOutY - 0.5f),
            new Vector4(rcpInX, rcpInY, 1.0f * rcpInX, -1.0f * rcpInY),
            new Vector4(-1.0f * rcpInX, 2.0f * rcpInY, 1.0f * rcpInX, 2.0f * rcpInY),
            new Vector4(0.0f * rcpInX, 4.0f * rcpInY, 0.0f, 0.0f));
    }
}

/// <summary>
/// How the engine's one sharpness knob (0 to 1, see <see cref="RenderScaleSettings.Sharpness"/>) is put to each upscaler
/// that sharpens. CAS takes it as it is, its own knob. RCAS counts in stops of less sharpening from its most (0), and the
/// knob goes from 2 stops at 0 (a quarter of RCAS's most) to 0 stops at 1. GSR 1's edge sharpness is 1 to 2 by its
/// README (2 its default), the knob goes from 1 at 0 to 2 at 1.
/// </summary>
public static class SpatialSharpness
{
    /// <summary>The stops of RCAS's sharpness for the knob.</summary>
    public static float RcasStops(float sharpness) => 2.0f * (1.0f - Knob(sharpness));

    /// <summary>FsrRcasCon's con.x, 2 to the minus the stops.</summary>
    public static float RcasAmount(float sharpness) => MathF.Pow(2.0f, -RcasStops(sharpness));

    /// <summary>GSR 1's EdgeSharpness for the knob.</summary>
    public static float GsrEdgeSharpness(float sharpness) => 1.0f + Knob(sharpness);

    private static float Knob(float sharpness) => float.IsFinite(sharpness) ? Math.Clamp(sharpness, 0.0f, 1.0f) : 0.0f;
}

/// <summary>GSR 1's ViewportInfo for a scene of a size, a texel (xy) and the size in texels (zw).</summary>
public static class GsrConstants
{
    /// <summary>The restored upstream GLSL path bounds its correction to the linearly sampled RGB at high-contrast edges.</summary>
    public const float MAX_LUMA_CORRECTION = 23.0f / 255.0f;

    /// <summary>What the README suggests the edge threshold is for mobile, 8 / 255, which the shader has.</summary>
    public const float EDGE_THRESHOLD = 8.0f / 255.0f;

    public static Vector4 ViewportInfo(float inputWidth, float inputHeight) => new(1.0f / inputWidth, 1.0f / inputHeight, inputWidth, inputHeight);
}
