using Horizon.Graphics;

using static Horizon.Tests.Shaders.ShaderTestFiles;

namespace Horizon.Tests.Shaders;

/// <summary>
/// Building a pack out of a manifest with Slang, the way Horizon.Shaders.Build does. These compile for real, so they
/// want Slang's natives next to the tests the way every desktop build has them.
/// </summary>
public class ShaderPackBuilderTests
{
    private static ShaderManifest ManifestOf(Horizon.Content.IContentFiles content, params string[] files)
    {
        var manifest = new ShaderManifest();
        foreach (string file in files) manifest.Add(ShaderManifestTests.Entry(content, file));
        return manifest;
    }

    [Fact]
    public void APackedProgramIsWhatTheCompilerMakesWithItsReflection()
    {
        var sources = Sources();
        var manifest = ManifestOf(sources, "shaders/test/compute.slang", "shaders/test/draw.slang", "shaders/test/legacy.hlsl");
        var content = WithPack(sources, ShaderPackBuilder.Build(manifest, sources, "test.manifest"));

        foreach (string file in new[] { "shaders/test/compute.slang", "shaders/test/draw.slang", "shaders/test/legacy.hlsl" })
        {
            var compiled = ShaderCompiler.Compile(FileProgram(file), file, Options(sources, ShaderSourcing.Compile), out string compileError);
            var packed = ShaderCompiler.Compile(FileProgram(file), file, Options(content, ShaderSourcing.Packed), out string packError);

            Assert.True(compiled is not null, compileError);
            Assert.True(packed is not null, packError);
            Assert.Equal(compiled.Stages.Select(stage => (stage.Stage, stage.EntryPoint)), packed.Stages.Select(stage => (stage.Stage, stage.EntryPoint)));
            for (int i = 0; i < compiled.Stages.Count; i++)
                Assert.Equal(compiled.Stages[i].Spirv, packed.Stages[i].Spirv);
            Assert.Equal(compiled.Params.Members, packed.Params.Members);
        }

        var compute = ShaderCompiler.Compile(FileProgram("shaders/test/compute.slang"), "compute", Options(content, ShaderSourcing.Packed), out _)!;
        Assert.Equal(new uint[] { 8, 4, 1 }, compute.Stages[0].ThreadGroupSize);
        Assert.True(compute.Params.TryGet("scale", out var scale));
        Assert.Equal(16, scale.Offset);

        // One module for both stages of the Slang file, one an entry point for HLSL
        var draw = ShaderCompiler.Compile(FileProgram("shaders/test/draw.slang"), "draw", Options(content, ShaderSourcing.Packed), out _)!;
        Assert.Same(draw.Stages[0].Spirv, draw.Stages[1].Spirv);
    }

    [Fact]
    public void BuildingTwiceMakesTheSameBytes()
    {
        var sources = Sources();
        var crlf = Sources("\r\n");
        var manifest = ManifestOf(sources, "shaders/test/compute.slang", "shaders/test/legacy.hlsl");

        byte[] one = ShaderPackBuilder.Build(manifest, sources, "test.manifest", [4096, 1024]);
        byte[] two = ShaderPackBuilder.Build(manifest, crlf, "elsewhere/test.manifest", [1024, 4096]);

        Assert.Equal(one, two);
        Assert.Equal(new uint[] { 1024, 4096 }, ShaderPack.Read(new MemoryStream(one), "one").BindlessCapacities);
    }

    [Fact]
    public void EachCapacityAskedForIsInThePack()
    {
        var sources = Sources("\n", ("shaders/test/bindless.slang", "BINDLESS_TEXTURES(uTextures);\n[shader(\"fragment\")] float4 fsMain() : SV_Target { return uTextures[0].Sample(float2(0, 0)); }\n"));
        var manifest = ManifestOf(sources, "shaders/test/bindless.slang");
        var content = WithPackOf(sources, ShaderPackBuilder.Build(manifest, sources, "test.manifest", [4096, 1024]));

        Assert.NotNull(ShaderCompiler.Compile(FileProgram("shaders/test/bindless.slang"), "b", Options(content, ShaderSourcing.Packed, capacity: 1024), out string small));
        Assert.NotNull(ShaderCompiler.Compile(FileProgram("shaders/test/bindless.slang"), "b", Options(content, ShaderSourcing.Packed, capacity: 4096), out string large));
        Assert.Null(ShaderCompiler.Compile(FileProgram("shaders/test/bindless.slang"), "b", Options(content, ShaderSourcing.Packed, capacity: 2048), out string other));
        Assert.Contains("1024, 4096", other);
    }

    [Fact]
    public void AnIncludeEditedSinceTheManifestStopsTheBuildUntilItsRefreshed()
    {
        var recorded = Sources();
        var manifest = ManifestOf(recorded, "shaders/test/compute.slang");
        var edited = Sources("\n", ("shaders/common/tint.slang", "struct Params { float4 tint; float scale; float extra; };\nBIND_UNIFORM(1) ConstantBuffer<Params> params;\n"));

        var stale = ShaderPackBuilder.Stale(manifest, edited);
        Assert.Contains("shaders/common/tint.slang", Assert.Single(stale));
        var e = Assert.Throws<ShaderPackBuildException>(() => ShaderPackBuilder.Build(manifest, edited, "test.manifest"));
        Assert.Contains(e.Problems, problem => problem.Contains("shaders/test/compute.slang") && problem.Contains("shaders/common/tint.slang"));

        var refreshed = ShaderPackBuilder.Refresh(manifest, edited, out var changed);
        Assert.Equal("shaders/test/compute.slang", Assert.Single(changed));
        Assert.Empty(ShaderPackBuilder.Stale(refreshed, edited));
        var content = WithPackOf(edited, ShaderPackBuilder.Build(refreshed, edited, "test.manifest"));
        var compiled = ShaderCompiler.Compile(FileProgram("shaders/test/compute.slang"), "compute", Options(content, ShaderSourcing.Packed), out string error);
        Assert.True(compiled is not null, error);
        Assert.True(compiled.Params.TryGet("extra", out _));
    }

    [Fact]
    public void AFileThatNowIncludesSomethingNewIsBehind()
    {
        var recorded = Sources("\n", ("shaders/test/plain.slang", "[shader(\"fragment\")] float4 fsMain() : SV_Target { return 1; }\n"));
        var manifest = ManifestOf(recorded, "shaders/test/plain.slang");
        var edited = Sources("\n", ("shaders/test/plain.slang", "#include <common/tint.slang>\n[shader(\"fragment\")] float4 fsMain() : SV_Target { return params.tint; }\n"));

        Assert.Contains("shaders/test/plain.slang is behind shaders/test/plain.slang", Assert.Single(ShaderPackBuilder.Stale(manifest, edited)));
    }

    [Theory]
    [InlineData("\n", 0)]
    [InlineData("\n", 1)]
    [InlineData("\n", 3)]
    [InlineData("\r\n", 3)]
    [InlineData("\r", 3)]
    public void InlineProgramsAreBuiltFromTheSourceTheManifestCarries(string newline, int trailingBreaks)
    {
        string inline = "#include <common/tint.slang>" + newline +
            "[shader(\"fragment\")] float4 fsMain() : SV_Target { return params.tint; }" +
            string.Concat(Enumerable.Repeat(newline, trailingBreaks));
        var sources = Sources();
        using var folder = new TempFolder();
        var recorder = new ShaderManifestRecorder(folder.File("run.manifest"));
        ShaderCompiler.Compile(InlineProgram(inline), "made-up", Options(sources, ShaderSourcing.Packed, recorder: recorder), out _);

        var manifest = ShaderManifest.Load(recorder.File);
        Assert.Empty(ShaderPackBuilder.Stale(manifest, sources));
        var content = WithPackOf(sources, ShaderPackBuilder.Build(manifest, sources, "run.manifest"));
        var compiled = ShaderCompiler.Compile(InlineProgram(inline), "made-up", Options(content, ShaderSourcing.Packed), out string error);

        Assert.True(compiled is not null, error);
        Assert.True(compiled.Params.TryGet("tint", out _));
    }

    [Fact]
    public void VerifyTellsAPackThatMatchesFromOneThatDoesnt()
    {
        var sources = Sources();
        var manifest = ManifestOf(sources, "shaders/test/compute.slang", "shaders/test/draw.slang");
        byte[] bytes = ShaderPackBuilder.Build(manifest, sources, "test.manifest");
        var pack = ShaderPack.Read(new MemoryStream(bytes), "good");

        Assert.Empty(ShaderPackBuilder.Verify(pack, manifest, sources));

        // The same keys with somebody else's SPIR-V in one of them, and a module nobody asked for
        var tampered = pack.Entries.Select((entry, i) => i == 0 ? new ShaderPackEntry(entry.Key, entry.Origin, FakeSpirv(7), entry.Stages, entry.Params) : entry)
            .Append(FakeEntry(new string('F', 64), "stray"));
        var bad = ShaderPack.Read(new MemoryStream(Pack(tampered)), "bad");
        var problems = ShaderPackBuilder.Verify(bad, manifest, sources);

        Assert.Contains(problems, problem => problem.Contains("the SPIR-V differs"));
        Assert.Contains(problems, problem => problem.Contains("stray") && problem.Contains("doesn't ask for"));
    }

    /// <summary>Content with the given sources and a pack, the sources here not being the usual set.</summary>
    private static Horizon.Tests.Content.MemoryContent WithPackOf(Horizon.Tests.Content.MemoryContent sources, byte[] pack)
    {
        var files = new Dictionary<string, byte[]> { [$"{ShaderPack.FOLDER}/test.hzshaders"] = pack };
        foreach (string file in sources.EnumerateFiles(string.Empty, "*", SearchOption.AllDirectories))
            files[file] = sources.ReadAllBytes(file);
        return new Horizon.Tests.Content.MemoryContent(files);
    }
}
