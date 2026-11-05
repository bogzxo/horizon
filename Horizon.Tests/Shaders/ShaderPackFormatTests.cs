using Horizon.Graphics;
using Horizon.Tests.Content;

using static Horizon.Tests.Shaders.ShaderTestFiles;

namespace Horizon.Tests.Shaders;

/// <summary>
/// The bytes of a shader pack. What goes in comes out, the same entries make the same bytes, and anything that isn't
/// a whole pack of this format is a clear error and never a half read pack or a read that doesn't end.
/// </summary>
public class ShaderPackFormatTests
{
    private static readonly string KeyA = new('A', 64), KeyB = new('B', 64), KeyC = new('C', 64);

    private static ShaderPackEntry[] Entries() =>
    [
        FakeEntry(KeyB, "shaders/b.slang", mark: 2),
        new ShaderPackEntry(KeyA, "shaders/a.slang", FakeSpirv(1),
            [new ShaderPackStage(ShaderStage.Vertex, "vsMain", 0, 0, 0), new ShaderPackStage(ShaderStage.Fragment, "fsMain", 0, 0, 0)], []),
        FakeEntry(KeyC, "inline", mark: 3, x: 64, y: 1, z: 1)
    ];

    [Fact]
    public void EveryStageWorkGroupAndParamComesBack()
    {
        var pack = ShaderPack.Read(new MemoryStream(Pack(Entries(), [4096, 1024])), "test");

        Assert.Equal(ShaderCompiler.CompilerFingerprint, pack.Fingerprint);
        Assert.Equal(ShaderCompiler.TARGET_PROFILE, pack.TargetProfile);
        Assert.Equal("test.manifest", pack.Manifest);
        Assert.Equal(new uint[] { 1024, 4096 }, pack.BindlessCapacities);
        Assert.Equal(new[] { KeyA, KeyB, KeyC }, pack.Entries.Select(entry => entry.Key));

        foreach (var written in Entries())
        {
            Assert.True(pack.TryGet(written.Key, out var read));
            Assert.Equal(string.Empty, written.DifferenceFrom(read));
            Assert.Equal(written.Origin, read.Origin);
        }

        var module = pack.Entries[2].ToModule();
        Assert.Equal(new uint[] { 64, 1, 1 }, module.Stages[0].ThreadGroupSize);
        Assert.True(module.Params.TryGet("scale", out var scale));
        Assert.Equal(16, scale.Offset);
        Assert.Equal(32, module.Params.Size);
    }

    [Fact]
    public void TheSameEntriesInAnyOrderMakeTheSameBytes()
    {
        byte[] one = Pack(Entries(), [4096, 1024]);
        byte[] two = Pack(Enumerable.Reverse(Entries()), [1024, 4096, 1024]);

        Assert.Equal(one, two);
    }

    [Fact]
    public void TheSameModuleFromTwoFilesIsKeptOnceUnderTheFirstName()
    {
        byte[] pack = Pack([FakeEntry(KeyA, "shaders/z.slang"), FakeEntry(KeyA, "shaders/a.slang")]);

        Assert.Equal("shaders/a.slang", Assert.Single(ShaderPack.Read(new MemoryStream(pack), "test").Entries).Origin);
        Assert.Throws<ShaderPackException>(() => Pack([FakeEntry(KeyA, "a", mark: 1), FakeEntry(KeyA, "b", mark: 2)]));
    }

    [Fact]
    public void APackCutShortAnywhereIsAnError()
    {
        byte[] whole = Pack(Entries());
        for (int length = 0; length < whole.Length; length++)
        {
            var e = Assert.Throws<ShaderPackException>(() => ShaderPack.Read(new MemoryStream(whole[..length]), "cut.hzshaders"));
            Assert.Contains("cut.hzshaders", e.Message);
        }
    }

    [Fact]
    public void AnyFlippedByteIsAnError()
    {
        byte[] whole = Pack(Entries());
        for (int at = 0; at < whole.Length; at++)
        {
            byte[] broken = (byte[])whole.Clone();
            broken[at] ^= 0x5A;
            Assert.Throws<ShaderPackException>(() => ShaderPack.Read(new MemoryStream(broken), "flipped"));
        }
    }

    [Fact]
    public void AnotherFormatSaysWhichItIsAndWhichIsWanted()
    {
        byte[] pack = Pack(Entries());
        pack[8] = 7;

        var e = Assert.Throws<ShaderPackException>(() => ShaderPack.Read(new MemoryStream(pack), "old.hzshaders"));
        Assert.Contains("format 7", e.Message);
        Assert.Contains("Horizon.Shaders.Build", e.Message);
    }

    [Fact]
    public void SomethingElseEntirelyIsntAPack()
    {
        var e = Assert.Throws<ShaderPackException>(() => ShaderPack.Read(new MemoryStream("not a pack at all, a text file"u8.ToArray()), "notes.txt"));
        Assert.Contains("isn't a shader pack", e.Message);
    }

    [Fact]
    public void AStreamThatDoesntEndIsStoppedAtTheLimit()
    {
        var e = Assert.Throws<ShaderPackException>(() => ShaderPack.Read(new EndlessStream(), "endless", maxBytes: 1 << 16));
        Assert.Contains("bigger than", e.Message);
    }

    [Fact]
    public void APackReadsOutOfContentThatTricklesAndCantSeek()
    {
        byte[] bytes = Pack(Entries());
        var content = new MemoryContent(new Dictionary<string, byte[]> { [$"{ShaderPack.FOLDER}/game.hzshaders"] = bytes }, awkward: true);

        var pack = ShaderPack.Load(content, $"{ShaderPack.FOLDER}/game.hzshaders");

        Assert.Equal(3, pack.Entries.Count);
        Assert.Equal(1, content.Opened);
    }

    /// <summary>A stream with no end, what a broken source could hand over.</summary>
    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => count;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
    }
}
