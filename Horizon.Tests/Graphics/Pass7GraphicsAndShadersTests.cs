using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Horizon.Graphics;
using Horizon.Graphics.Vulkan;
using Horizon.Rendering.Lighting;
using Horizon.Rendering.PostProcessing;
using Horizon.Rendering.Tiling;

namespace Horizon.Tests.Graphics;

public class Pass7GraphicsAndShadersTests
{
    [Fact]
    public void DualSourceBlendModeAndFactorsMatchVulkan()
    {
        BlendMode mode = BlendMode.DualSource;
        Assert.Equal(BlendFactor.One, mode.SourceColor);
        Assert.Equal(BlendFactor.OneMinusSrc1Color, mode.DestinationColor);
        Assert.Equal(BlendFactor.One, mode.SourceAlpha);
        Assert.Equal(BlendFactor.OneMinusSrc1Alpha, mode.DestinationAlpha);

        Assert.Equal(Silk.NET.Vulkan.BlendFactor.Src1Color, PipelineCache.FactorOf(BlendFactor.Src1Color));
        Assert.Equal(Silk.NET.Vulkan.BlendFactor.OneMinusSrc1Color, PipelineCache.FactorOf(BlendFactor.OneMinusSrc1Color));
        Assert.Equal(Silk.NET.Vulkan.BlendFactor.Src1Alpha, PipelineCache.FactorOf(BlendFactor.Src1Alpha));
        Assert.Equal(Silk.NET.Vulkan.BlendFactor.OneMinusSrc1Alpha, PipelineCache.FactorOf(BlendFactor.OneMinusSrc1Alpha));
        Assert.Equal(Silk.NET.Vulkan.BlendFactor.OneMinusDstAlpha, PipelineCache.FactorOf(BlendFactor.OneMinusDstAlpha));
    }

    [Fact]
    public void ShaderPackZeroCopyRoundtripPreservesSeekableAndNonSeekableStreams()
    {
        byte[] spirv = MakeFakeSpirv(42);
        var entry = new ShaderPackEntry(
            new string('A', 64),
            "shaders/test/sample.slang",
            spirv,
            [new ShaderPackStage(ShaderStage.Compute, "csMain", 8, 8, 1)],
            [new UniformBlockLayout.Member("uScale", "float", 0, 4, 0, 0)]);

        byte[] packed = ShaderPackWriter.Write("fp", "spirv_1_6", "test.manifest", [4096u], [entry]);

        ShaderPack fromSeekable = ShaderPack.Read(new MemoryStream(packed), "seekable.hzshaders");
        Assert.True(fromSeekable.TryGet(entry.Key, out ShaderPackEntry readSeekable));
        Assert.Equal(string.Empty, entry.DifferenceFrom(readSeekable));
        Assert.True(readSeekable.SpirvSpan.SequenceEqual(spirv));
        Assert.Equal(spirv, readSeekable.Spirv);
        Assert.True(readSeekable.ToModule().Stages[0].SpirvMemory.Span.SequenceEqual(spirv));

        using var nonSeekable = new NonSeekableReadStream(packed);
        ShaderPack fromStream = ShaderPack.Read(nonSeekable, "stream.hzshaders");
        Assert.True(fromStream.TryGet(entry.Key, out ShaderPackEntry readStream));
        Assert.Equal(string.Empty, entry.DifferenceFrom(readStream));
        Assert.Equal(spirv, readStream.Spirv);
    }

    [Fact]
    public void CompactTileMapGpuTileIs48BytesWithExpectedOffsets()
    {
        Assert.Equal(48, Marshal.SizeOf<TileMapGpu.Tile>());
        Assert.Equal(0, (int)Marshal.OffsetOf<TileMapGpu.Tile>(nameof(TileMapGpu.Tile.Position)));
        Assert.Equal(8, (int)Marshal.OffsetOf<TileMapGpu.Tile>(nameof(TileMapGpu.Tile.Size)));
        Assert.Equal(16, (int)Marshal.OffsetOf<TileMapGpu.Tile>(nameof(TileMapGpu.Tile.UvRect)));
        Assert.Equal(32, (int)Marshal.OffsetOf<TileMapGpu.Tile>(nameof(TileMapGpu.Tile.Rotation)));
        Assert.Equal(36, (int)Marshal.OffsetOf<TileMapGpu.Tile>(nameof(TileMapGpu.Tile.LayerAndFlip)));
        Assert.Equal(40, (int)Marshal.OffsetOf<TileMapGpu.Tile>(nameof(TileMapGpu.Tile.AlbedoNormal)));
        Assert.Equal(44, (int)Marshal.OffsetOf<TileMapGpu.Tile>(nameof(TileMapGpu.Tile.SpecularOcclusion)));
    }

    [Fact]
    public void UniformBlockStructsMatchStd140SizesAndOffsets()
    {
        // SpriteShadows
        Assert.Equal(24, Marshal.SizeOf<SpriteShadows.SeedParams>());
        Assert.Equal(0, (int)Marshal.OffsetOf<SpriteShadows.SeedParams>(nameof(SpriteShadows.SeedParams.Size)));
        Assert.Equal(8, (int)Marshal.OffsetOf<SpriteShadows.SeedParams>(nameof(SpriteShadows.SeedParams.Cells)));
        Assert.Equal(16, (int)Marshal.OffsetOf<SpriteShadows.SeedParams>(nameof(SpriteShadows.SeedParams.Scale)));
        Assert.Equal(20, (int)Marshal.OffsetOf<SpriteShadows.SeedParams>(nameof(SpriteShadows.SeedParams.SpritesBlock)));

        Assert.Equal(16, Marshal.SizeOf<SpriteShadows.FloodParams>());
        Assert.Equal(0, (int)Marshal.OffsetOf<SpriteShadows.FloodParams>(nameof(SpriteShadows.FloodParams.Cells)));
        Assert.Equal(8, (int)Marshal.OffsetOf<SpriteShadows.FloodParams>(nameof(SpriteShadows.FloodParams.Step)));
        Assert.Equal(12, (int)Marshal.OffsetOf<SpriteShadows.FloodParams>(nameof(SpriteShadows.FloodParams.Scale)));

        Assert.Equal(32, Marshal.SizeOf<SpriteShadows.FieldParams>());
        Assert.Equal(0, (int)Marshal.OffsetOf<SpriteShadows.FieldParams>(nameof(SpriteShadows.FieldParams.Size)));
        Assert.Equal(8, (int)Marshal.OffsetOf<SpriteShadows.FieldParams>(nameof(SpriteShadows.FieldParams.Cells)));
        Assert.Equal(16, (int)Marshal.OffsetOf<SpriteShadows.FieldParams>(nameof(SpriteShadows.FieldParams.Scale)));
        Assert.Equal(20, (int)Marshal.OffsetOf<SpriteShadows.FieldParams>(nameof(SpriteShadows.FieldParams.Far)));
        Assert.Equal(24, (int)Marshal.OffsetOf<SpriteShadows.FieldParams>(nameof(SpriteShadows.FieldParams.PixelWorld)));
        Assert.Equal(28, (int)Marshal.OffsetOf<SpriteShadows.FieldParams>(nameof(SpriteShadows.FieldParams.SpritesBlock)));

        // BloomEffect
        Assert.Equal(16, Marshal.SizeOf<BloomEffect.BloomDownParams>());
        Assert.Equal(0, (int)Marshal.OffsetOf<BloomEffect.BloomDownParams>(nameof(BloomEffect.BloomDownParams.Texel)));
        Assert.Equal(8, (int)Marshal.OffsetOf<BloomEffect.BloomDownParams>(nameof(BloomEffect.BloomDownParams.Threshold)));
        Assert.Equal(12, (int)Marshal.OffsetOf<BloomEffect.BloomDownParams>(nameof(BloomEffect.BloomDownParams.First)));

        Assert.Equal(16, Marshal.SizeOf<BloomEffect.BloomUpParams>());
        Assert.Equal(0, (int)Marshal.OffsetOf<BloomEffect.BloomUpParams>(nameof(BloomEffect.BloomUpParams.Texel)));
        Assert.Equal(8, (int)Marshal.OffsetOf<BloomEffect.BloomUpParams>(nameof(BloomEffect.BloomUpParams.Radius)));

        Assert.Equal(16, Marshal.SizeOf<BloomEffect.BloomMixParams>());
        Assert.Equal(0, (int)Marshal.OffsetOf<BloomEffect.BloomMixParams>(nameof(BloomEffect.BloomMixParams.Intensity)));

        // PathTracedLighting2D (gi_resolve.slang)
        Assert.Equal(80, Marshal.SizeOf<PathTracedLighting2D.ResolveParams>());
        Assert.Equal(0, (int)Marshal.OffsetOf<PathTracedLighting2D.ResolveParams>(nameof(PathTracedLighting2D.ResolveParams.Size)));
        Assert.Equal(8, (int)Marshal.OffsetOf<PathTracedLighting2D.ResolveParams>(nameof(PathTracedLighting2D.ResolveParams.TraceSize)));
        Assert.Equal(16, (int)Marshal.OffsetOf<PathTracedLighting2D.ResolveParams>(nameof(PathTracedLighting2D.ResolveParams.CascadeSize)));
        Assert.Equal(24, (int)Marshal.OffsetOf<PathTracedLighting2D.ResolveParams>(nameof(PathTracedLighting2D.ResolveParams.ProbeCount)));
        Assert.Equal(32, (int)Marshal.OffsetOf<PathTracedLighting2D.ResolveParams>(nameof(PathTracedLighting2D.ResolveParams.ProbeSpacing)));
        Assert.Equal(40, (int)Marshal.OffsetOf<PathTracedLighting2D.ResolveParams>(nameof(PathTracedLighting2D.ResolveParams.ProbeOffset)));
        Assert.Equal(48, (int)Marshal.OffsetOf<PathTracedLighting2D.ResolveParams>(nameof(PathTracedLighting2D.ResolveParams.Directions)));
        Assert.Equal(52, (int)Marshal.OffsetOf<PathTracedLighting2D.ResolveParams>(nameof(PathTracedLighting2D.ResolveParams.DirectionsSqrt)));
        Assert.Equal(56, (int)Marshal.OffsetOf<PathTracedLighting2D.ResolveParams>(nameof(PathTracedLighting2D.ResolveParams.Shift)));
        Assert.Equal(64, (int)Marshal.OffsetOf<PathTracedLighting2D.ResolveParams>(nameof(PathTracedLighting2D.ResolveParams.Settle)));
        Assert.Equal(68, (int)Marshal.OffsetOf<PathTracedLighting2D.ResolveParams>(nameof(PathTracedLighting2D.ResolveParams.FrameTime)));
        Assert.Equal(72, (int)Marshal.OffsetOf<PathTracedLighting2D.ResolveParams>(nameof(PathTracedLighting2D.ResolveParams.Sharpen)));
        Assert.Equal(76, (int)Marshal.OffsetOf<PathTracedLighting2D.ResolveParams>(nameof(PathTracedLighting2D.ResolveParams.Responsiveness)));
    }

    private static byte[] MakeFakeSpirv(uint mark)
    {
        byte[] bytes = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0x07230203u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 0x00010600u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4), mark);
        return bytes;
    }

    private sealed class NonSeekableReadStream(byte[] data) : Stream
    {
        private int position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int take = Math.Min(Math.Min(count, 17), data.Length - position);
            if (take <= 0) return 0;
            Array.Copy(data, position, buffer, offset, take);
            position += take;
            return take;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
