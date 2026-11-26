using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Horizon.Graphics;
using Horizon.Graphics.Vulkan;

namespace Horizon.Tests.Rendering;

/// <summary>
/// Headless unit tests for Pass 2 GraphicsDevice and Vulkan helper invariants: stream buffer alignment for
/// non-power-of-2 element sizes, bindless capacity clamping, texel upload format fallbacks, scissor / ReadPixels
/// bounds clamping, and SPIR-V separate-sampler detection.
/// </summary>
public class Pass2GraphicsDeviceTests
{
    [StructLayout(LayoutKind.Sequential, Size = 24)]
    private struct Particle24
    {
        public float X, Y, Vx, Vy, Life, Size;
    }

    [Theory]
    [InlineData(1, 24, 16u)]
    [InlineData(10, 24, 64u)]
    [InlineData(10, 24, 256u)]
    [InlineData(100, 24, 256u)]
    [InlineData(1, 12, 32u)]
    [InlineData(17, 36, 256u)]
    [InlineData(5, 48, 64u)]
    [InlineData(100, 64, 64u)]
    public void Stream_buffer_region_layout_aligns_every_region_for_non_power_of_two_elements(int wanted, int elementSize, uint alignment)
    {
        var (capacity, regionBytes) = StreamBuffer<Particle24>.ComputeRegionLayout(wanted, elementSize, alignment);

        Assert.True(capacity >= wanted, $"Capacity {capacity} must be at least wanted {wanted}.");
        Assert.Equal((nuint)capacity * (nuint)elementSize, regionBytes);
        Assert.Equal((nuint)0, regionBytes % alignment);

        for (int region = 0; region < StreamBuffer<Particle24>.REGIONS; region++)
        {
            ulong byteOffset = (ulong)region * (ulong)capacity * (ulong)elementSize;
            Assert.Equal(0UL, byteOffset % alignment);
        }
    }

    [Theory]
    [InlineData(0u, 1u)]
    [InlineData(16u, 16u)]
    [InlineData(64u, 64u)]
    [InlineData(128u, 128u)]
    [InlineData(255u, 255u)]
    [InlineData(256u, 256u)]
    [InlineData(1024u, 1024u)]
    [InlineData(4096u, 4096u)]
    [InlineData(65536u, 4096u)]
    public void Bindless_capacity_never_exceeds_hardware_limit_below_256_or_max_capacity(uint allowed, uint expected)
    {
        Assert.Equal(expected, BindlessTextures.ComputeCapacity(allowed));
    }

    [Theory]
    [InlineData(PixelFormat.R8, TexelFormat.R8, 1)]
    [InlineData(PixelFormat.R16F, TexelFormat.R16F, 2)]
    [InlineData(PixelFormat.Rgba8, TexelFormat.Rgba8, 4)]
    [InlineData(PixelFormat.Rgba8Srgb, TexelFormat.Rgba8, 4)]
    [InlineData(PixelFormat.Rgba16F, TexelFormat.Rgba16F, 8)]
    [InlineData(PixelFormat.Rgba32F, TexelFormat.Rgba32F, 16)]
    public void Create_texture_fallback_maps_pixel_formats_to_matching_texel_format_and_stride(PixelFormat pixelFormat, TexelFormat expectedFormat, int expectedBytes)
    {
        TexelFormat mapped = GraphicsDevice.TexelFormatOf(pixelFormat);
        Assert.Equal(expectedFormat, mapped);
        Assert.Equal(expectedBytes, GraphicsDevice.BytesPerTexel(mapped));
    }

    [Fact]
    public void Default_scissor_clamps_negative_and_oversized_viewport_to_target_bounds()
    {
        var (x, y, w, h) = GraphicsDevice.ClampScissor(
            customScissor: false,
            scissorX: 0, scissorY: 0, scissorWidth: 0, scissorHeight: 0,
            viewportX: -20, viewportY: -10, viewportWidth: 200, viewportHeight: 150,
            targetWidth: 100, targetHeight: 80);

        Assert.Equal((0, 0, 100u, 80u), (x, y, w, h));
    }

    [Fact]
    public void Custom_scissor_clamps_against_both_viewport_and_target_bounds_without_overflow()
    {
        var (x, y, w, h) = GraphicsDevice.ClampScissor(
            customScissor: true,
            scissorX: -50, scissorY: 20, scissorWidth: uint.MaxValue, scissorHeight: 500,
            viewportX: 10, viewportY: 10, viewportWidth: 100, viewportHeight: 100,
            targetWidth: 80, targetHeight: 60);

        Assert.Equal((10, 20, 70u, 40u), (x, y, w, h));

        var outside = GraphicsDevice.ClampScissor(
            customScissor: true,
            scissorX: 200, scissorY: 200, scissorWidth: 50, scissorHeight: 50,
            viewportX: 0, viewportY: 0, viewportWidth: 100, viewportHeight: 100,
            targetWidth: 100, targetHeight: 100);

        Assert.Equal((100, 100, 0u, 0u), outside);
    }

    [Fact]
    public void Read_pixels_clamps_bottom_up_requests_safely_into_image_bounds()
    {
        // In-bounds: bottom-up y=20, h=50 in 600px image -> top=530
        Assert.Equal((10, 530, 100u, 50u), GraphicsDevice.ClampReadPixels(10, 20, 100, 50, 800, 600));

        // Extending past right and top edge
        Assert.Equal((80, 0, 20u, 20u), GraphicsDevice.ClampReadPixels(80, 80, 50, 50, 100, 100));

        // Negative x and y
        Assert.Equal((0, 80, 20u, 20u), GraphicsDevice.ClampReadPixels(-10, -10, 30, 30, 100, 100));

        // Completely outside
        var outside = GraphicsDevice.ClampReadPixels(200, 0, 50, 50, 100, 100);
        Assert.Equal(0u, outside.Width);
    }

    [Fact]
    public void Spirv_scanner_detects_only_set_1_separate_sampler_bindings()
    {
        // Set 1, Binding 0 (CombinedImageSampler) -> false
        Assert.False(Shader.UsesSeparateSamplersInSpirv(MakeMinimalSpirv(targetId: 7, descriptorSet: 1, binding: 0)));

        // Set 1, Binding 12 (separate Sampler) -> true
        Assert.True(Shader.UsesSeparateSamplersInSpirv(MakeMinimalSpirv(targetId: 7, descriptorSet: 1, binding: 12)));

        // Set 0, Binding 12 (not Set 1) -> false
        Assert.False(Shader.UsesSeparateSamplersInSpirv(MakeMinimalSpirv(targetId: 7, descriptorSet: 0, binding: 12)));
    }

    private static byte[] MakeMinimalSpirv(uint targetId, uint descriptorSet, uint binding)
    {
        // 5-word SPIR-V header + two 4-word OpDecorate instructions + 1-word OpFunction stop sentinel
        uint[] words =
        [
            0x07230203u, 0x00010600u, 0u, 16u, 0u,
            (4u << 16) | 71u, targetId, 34u, descriptorSet,
            (4u << 16) | 71u, targetId, 33u, binding,
            (1u << 16) | 54u
        ];

        byte[] bytes = new byte[words.Length * 4];
        for (int i = 0; i < words.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4, 4), words[i]);
        return bytes;
    }
}
