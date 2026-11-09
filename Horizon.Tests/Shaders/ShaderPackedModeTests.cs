using Horizon.Graphics;

using static Horizon.Tests.Shaders.ShaderTestFiles;

namespace Horizon.Tests.Shaders;

/// <summary>
/// A run told to use the pack. Programs come out of it whole, and one that isn't in it is an error that says which,
/// even with Slang and a cache that has it right there. None of these compile anything.
/// </summary>
public class ShaderPackedModeTests
{
    [Fact]
    public void AProgramComesOutOfThePackWithItsStagesGroupsAndParams()
    {
        var sources = Sources();
        string key = UnitsOf(sources, "shaders/test/compute.slang")[0].Key;
        var content = WithPack(sources, Pack([FakeEntry(key, "shaders/test/compute.slang", mark: 9)]));

        var compiled = ShaderCompiler.Compile(FileProgram("shaders/test/compute.slang"), "compute", Options(content, ShaderSourcing.Packed), out string error);

        Assert.True(compiled is not null, error);
        var stage = Assert.Single(compiled.Stages);
        Assert.Equal((ShaderStage.Compute, "csMain"), (stage.Stage, stage.EntryPoint));
        Assert.Equal(new uint[] { 8, 4, 1 }, stage.ThreadGroupSize);
        Assert.Equal(FakeSpirv(9), stage.Spirv);
        Assert.True(compiled.Params.TryGet("tint", out _));
        Assert.Equal(32, compiled.Params.Size);
    }

    [Fact]
    public void AMissingProgramIsAnErrorEvenWithTheCacheHoldingIt()
    {
        var sources = Sources();
        string key = UnitsOf(sources, "shaders/test/compute.slang")[0].Key;
        var content = WithPack(sources, Pack([FakeEntry(new string('0', 64), "shaders/other.slang")]));

        // The cache has exactly the module wanted, a packed run mustn't look
        using var cache = new TempFolder();
        ShaderCompiler.KeepCached(cache.Path, key, FakeEntry(key, "cached").ToModule());
        Assert.True(ShaderCompiler.TryLoadCached(cache.Path, key, out _));

        var options = Options(content, ShaderSourcing.Packed, cache: cache.Path);
        var compiled = ShaderCompiler.Compile(FileProgram("shaders/test/compute.slang"), "compute", options, out string error);

        Assert.Null(compiled);
        Assert.Contains("shaders/test/compute.slang", error);
        Assert.Contains(key, error);
        Assert.Contains("test.manifest", error);
        Assert.Contains("HORIZON_SHADER_MANIFEST", error);
    }

    [Fact]
    public void WithNoPackAtAllTheErrorSaysSo()
    {
        var compiled = ShaderCompiler.Compile(FileProgram("shaders/test/compute.slang"), "compute", Options(Sources(), ShaderSourcing.Packed), out string error);

        Assert.Null(compiled);
        Assert.Contains($"No usable shader pack was found in {ShaderPack.FOLDER}", error);
    }

    [Fact]
    public void ADeviceWithAnotherBindlessTableIsToldWhichSizesThePackHas()
    {
        var sources = Sources();
        string key = UnitsOf(sources, "shaders/test/compute.slang", 4096)[0].Key;
        var content = WithPack(sources, Pack([FakeEntry(key, "shaders/test/compute.slang")], [4096]));

        var compiled = ShaderCompiler.Compile(FileProgram("shaders/test/compute.slang"), "compute", Options(content, ShaderSourcing.Packed, capacity: 2048), out string error);

        Assert.Null(compiled);
        Assert.Contains("bindless tables of 4096 and this device has 2048", error);
        Assert.Contains("--capacity 2048", error);
    }

    [Fact]
    public void APackMadeByAnotherCompilerIsLeftOutAndSaidWhy()
    {
        var sources = Sources();
        string key = UnitsOf(sources, "shaders/test/compute.slang")[0].Key;
        var content = WithPack(sources, Pack([FakeEntry(key, "shaders/test/compute.slang")], fingerprint: "1|slang-old|-target spirv"));

        var compiled = ShaderCompiler.Compile(FileProgram("shaders/test/compute.slang"), "compute", Options(content, ShaderSourcing.Packed), out string error);

        Assert.Null(compiled);
        Assert.Contains("another compiler setup", error);
    }

    [Fact]
    public void ABrokenPackIsNamedInTheError()
    {
        var sources = Sources();
        byte[] pack = Pack([FakeEntry(new string('A', 64), "a")]);
        var content = WithPack(sources, pack[..(pack.Length / 2)], "broken.hzshaders");

        var compiled = ShaderCompiler.Compile(FileProgram("shaders/test/compute.slang"), "compute", Options(content, ShaderSourcing.Packed), out string error);

        Assert.Null(compiled);
        Assert.Contains("broken.hzshaders", error);
        Assert.Contains("cut short", error);
    }

    [Fact]
    public void EveryHlslEntryPointIsItsOwnModuleInThePack()
    {
        var sources = Sources();
        var units = UnitsOf(sources, "shaders/test/legacy.hlsl");
        var content = WithPack(sources, Pack(
        [
            FakeEntry(units[0].Key, "shaders/test/legacy.hlsl", mark: 1, ShaderStage.Vertex, "vsMain", 0, 0, 0),
            FakeEntry(units[1].Key, "shaders/test/legacy.hlsl", mark: 2, ShaderStage.Fragment, "psMain", 0, 0, 0)
        ]));

        var compiled = ShaderCompiler.Compile(FileProgram("shaders/test/legacy.hlsl"), "legacy", Options(content, ShaderSourcing.Packed), out string error);

        Assert.True(compiled is not null, error);
        Assert.Equal(new[] { "vsMain", "psMain" }, compiled.Stages.Select(stage => stage.EntryPoint));
        Assert.Equal(FakeSpirv(2), compiled.Stages[1].Spirv);
    }

    [Fact]
    public void InlineSourceComesOutOfThePackToo()
    {
        var sources = Sources();
        const string inline = "#include <common/tint.slang>\n[shader(\"fragment\")] float4 fsMain() : SV_Target { return params.tint; }\n";
        using var preprocessor = new ShaderPreprocessor(sources, CAPACITY);
        string key = ShaderCompiler.UnitsOf(preprocessor.ProcessSource(string.Empty, ShaderLines.Split(inline)), ShaderLanguage.Slang)[0].Key;
        var content = WithPack(sources, Pack([FakeEntry(key, "made-up", stage: ShaderStage.Fragment, entry: "fsMain", x: 0, y: 0, z: 0)]));

        // Written with \r\n this time, the key is the same
        var compiled = ShaderCompiler.Compile(InlineProgram(inline.Replace("\n", "\r\n")), "made-up", Options(content, ShaderSourcing.Packed), out string error);

        Assert.True(compiled is not null, error);
        Assert.Equal("fsMain", Assert.Single(compiled.Stages).EntryPoint);
    }

    [Fact]
    public void EachContentSourceHasItsOwnPacks()
    {
        var sources = Sources();
        string key = UnitsOf(sources, "shaders/test/compute.slang")[0].Key;
        var first = WithPack(sources, Pack([FakeEntry(key, "first", mark: 1)]));
        var second = WithPack(sources, Pack([FakeEntry(key, "second", mark: 2)]));

        Assert.True(ShaderPackSet.For(first).TryFind(key, out var a));
        Assert.True(ShaderPackSet.For(second).TryFind(key, out var b));
        Assert.True(ShaderPackSet.For(first).TryFind(key, out var again));

        Assert.Equal(FakeSpirv(1), a.Spirv);
        Assert.Equal(FakeSpirv(2), b.Spirv);
        Assert.Same(a, again);
        Assert.Same(ShaderPackSet.For(first), ShaderPackSet.For(first));
    }

    [Fact]
    public void TheEnvironmentWordsMeanWhatTheySay()
    {
        Assert.Equal(ShaderSourcing.Packed, ShaderCompiler.SourcingFrom("packed"));
        Assert.Equal(ShaderSourcing.Packed, ShaderCompiler.SourcingFrom(" PACKED "));
        Assert.Equal(ShaderSourcing.Compile, ShaderCompiler.SourcingFrom("compile"));
        Assert.Equal(ShaderSourcing.Automatic, ShaderCompiler.SourcingFrom(null));
        Assert.Equal(ShaderSourcing.Automatic, ShaderCompiler.SourcingFrom("anything"));
    }
}
