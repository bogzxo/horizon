using System.Numerics;

using Horizon.Graphics;
using Horizon.Graphics.Vulkan;
using Horizon.Rendering;
using Horizon.Rendering.Lighting;
using Horizon.Rendering.PostProcessing;

using Silk.NET.Vulkan;

namespace Horizon.Tests.Rendering;

/// <summary>
/// Headless unit tests for Pass 6-A lighting, post-processing, and Vulkan sync/plumbing helpers:
/// radiance cascade active dispatch sizing & ping-pong indexing, R11G11B10F format mapping,
/// dual-source blend factor mapping, narrowed BarrierTargets stage/access masks, and LightCells AO parameters.
/// </summary>
public class Pass6GraphicsAndLightingTests
{
    [Theory]
    [InlineData(241, 136, 2.0f, 482u, 272u)]
    [InlineData(121, 69, 4.0f, 484u, 276u)]
    [InlineData(61, 35, 8.0f, 488u, 280u)]
    [InlineData(16, 10, 32.0f, 512u, 320u)]
    [InlineData(9, 6, 64.0f, 576u, 384u)]
    public void Active_cascade_size_matches_exact_probe_and_direction_grid(float probesX, float probesY, float directionsSqrt, uint expectedW, uint expectedH)
    {
        var (w, h) = PathTracedLighting2D.ActiveCascadeSize(new Vector2(probesX, probesY), directionsSqrt);
        Assert.Equal((expectedW, expectedH), (w, h));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Ping_pong_cascade_indices_never_alias_read_and_write_and_alternate_top_down(int cascadeCount)
    {
        int? previousWrite = null;
        for (int i = cascadeCount - 1; i >= 0; i--)
        {
            var (readIdx, writeIdx) = PathTracedLighting2D.PingPongIndices(i, cascadeCount);
            Assert.InRange(readIdx, 0, 1);
            Assert.InRange(writeIdx, 0, 1);
            Assert.NotEqual(readIdx, writeIdx);

            if (previousWrite is { } upperWritten)
                Assert.Equal(upperWritten, readIdx);

            previousWrite = writeIdx;
        }
    }

    [Fact]
    public void R11G11B10F_maps_to_vulkan_packed_ufloat_and_4_bytes_per_texel()
    {
        Assert.Equal(Format.B10G11R11UfloatPack32, GraphicsDevice.MapPixelFormat(PixelFormat.R11G11B10F, Format.D24UnormS8Uint));
        Assert.Equal(4, TextureDefinition.R11G11B10Float.BytesPerTexel);
        Assert.Equal(PixelFormat.R11G11B10F, PostTarget.HdrColor.Format);
        Assert.True(PostTarget.HdrColor.Smooth);
    }

    [Theory]
    [InlineData(Horizon.Graphics.BlendFactor.Zero, Silk.NET.Vulkan.BlendFactor.Zero)]
    [InlineData(Horizon.Graphics.BlendFactor.One, Silk.NET.Vulkan.BlendFactor.One)]
    [InlineData(Horizon.Graphics.BlendFactor.SrcAlpha, Silk.NET.Vulkan.BlendFactor.SrcAlpha)]
    [InlineData(Horizon.Graphics.BlendFactor.OneMinusSrcAlpha, Silk.NET.Vulkan.BlendFactor.OneMinusSrcAlpha)]
    [InlineData(Horizon.Graphics.BlendFactor.Src1Color, Silk.NET.Vulkan.BlendFactor.Src1Color)]
    [InlineData(Horizon.Graphics.BlendFactor.OneMinusSrc1Color, Silk.NET.Vulkan.BlendFactor.OneMinusSrc1Color)]
    public void Pipeline_cache_maps_blend_factors_including_dual_source(Horizon.Graphics.BlendFactor engineFactor, Silk.NET.Vulkan.BlendFactor expectedVk)
    {
        Assert.Equal(expectedVk, Horizon.Graphics.Vulkan.PipelineCache.FactorOf(engineFactor));
    }

    [Fact]
    public void Barrier_masks_narrow_compute_shader_images_and_storage_without_all_commands()
    {
        var (srcStage, srcAccess, dstStage, dstAccess) = GraphicsDevice.BarrierMasks(BarrierTargets.ShaderImages);
        Assert.Equal(PipelineStageFlags2.ComputeShaderBit, srcStage);
        Assert.Equal(AccessFlags2.ShaderWriteBit | AccessFlags2.ShaderStorageWriteBit, srcAccess);
        Assert.Equal(PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.VertexShaderBit, dstStage);
        Assert.Equal(AccessFlags2.ShaderReadBit | AccessFlags2.ShaderSampledReadBit | AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit, dstAccess);
        Assert.Equal(PipelineStageFlags2.None, srcStage & PipelineStageFlags2.AllCommandsBit);
        Assert.Equal(PipelineStageFlags2.None, dstStage & PipelineStageFlags2.AllCommandsBit);

        var storage = GraphicsDevice.BarrierMasks(BarrierTargets.ShaderStorage);
        Assert.Equal((srcStage, srcAccess, dstStage, dstAccess), storage);
    }

    [Fact]
    public void Barrier_masks_include_draw_indirect_and_vertex_input_when_vertex_attributes_requested()
    {
        var (srcStage, srcAccess, dstStage, dstAccess) = GraphicsDevice.BarrierMasks(BarrierTargets.ShaderStorage | BarrierTargets.VertexAttributes);
        Assert.Equal(PipelineStageFlags2.ComputeShaderBit, srcStage);
        Assert.Equal(AccessFlags2.ShaderWriteBit | AccessFlags2.ShaderStorageWriteBit, srcAccess);
        Assert.True((dstStage & PipelineStageFlags2.DrawIndirectBit) != 0);
        Assert.True((dstStage & PipelineStageFlags2.VertexInputBit) != 0);
        Assert.True((dstStage & PipelineStageFlags2.VertexShaderBit) != 0);
        Assert.True((dstAccess & AccessFlags2.IndirectCommandReadBit) != 0);
        Assert.True((dstAccess & AccessFlags2.VertexAttributeReadBit) != 0);
        Assert.True((dstAccess & AccessFlags2.IndexReadBit) != 0);
        Assert.Equal(PipelineStageFlags2.None, dstStage & PipelineStageFlags2.AllCommandsBit);
    }

    [Fact]
    public void Light_cells_ao_params_clamp_and_gate_on_enabled_and_strength()
    {
        var disabled = LightCells.ClampAoParams(new AmbientOcclusion2D.State(Enabled: false, Radius: 16f, Strength: 0.85f, Samples: 8, BakedStrength: 1f, DirectStrength: 0.4f, Show: false));
        Assert.False(disabled.Enabled);

        var zeroStrength = LightCells.ClampAoParams(new AmbientOcclusion2D.State(Enabled: true, Radius: 16f, Strength: 0.0f, Samples: 8, BakedStrength: 1f, DirectStrength: 0.4f, Show: false));
        Assert.False(zeroStrength.Enabled);

        var clamped = LightCells.ClampAoParams(new AmbientOcclusion2D.State(Enabled: true, Radius: 0.1f, Strength: 2.5f, Samples: 64, BakedStrength: 1f, DirectStrength: 0.4f, Show: false));
        Assert.True(clamped.Enabled);
        Assert.Equal(32, clamped.Samples);
        Assert.Equal(1.0f, clamped.Radius);
        Assert.Equal(1.0f, clamped.Strength);

        var lowSamples = LightCells.ClampAoParams(new AmbientOcclusion2D.State(Enabled: true, Radius: 24f, Strength: 0.5f, Samples: 0, BakedStrength: 1f, DirectStrength: 0.4f, Show: false));
        Assert.True(lowSamples.Enabled);
        Assert.Equal(1, lowSamples.Samples);
        Assert.Equal(24.0f, lowSamples.Radius);
        Assert.Equal(0.5f, lowSamples.Strength);
    }

    [Theory]
    [InlineData(1.0f, false, 3, false, true)]
    [InlineData(1.0f, false, 0, true, true)]
    [InlineData(1.0f, false, 0, false, false)]
    [InlineData(0.0f, false, 3, true, false)]
    [InlineData(1.0f, true, 3, true, false)]
    public void Deferred_renderer_builds_light_cells_for_pixel_art_lights_or_ao(float pixelSize, bool cellsOff, int lights, bool aoEnabled, bool expected)
    {
        Assert.Equal(expected, DeferredRenderer2D.ShouldBuildLightCells(pixelSize, cellsOff, lights, aoEnabled));
    }
}
