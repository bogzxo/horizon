using Horizon.Graphics;

using static Horizon.Tests.Shaders.ShaderTestFiles;

namespace Horizon.Tests.Shaders;

/// <summary>
/// The manifest a run writes. Every program asked for goes in, the ones that came out of a cache or a pack too, the
/// text is the same whatever order they were asked in, and a builder can tell when the sources moved on without it.
/// </summary>
public class ShaderManifestTests
{
    [Fact]
    public void AProgramOutOfTheCacheIsWrittenDownToo()
    {
        var content = Sources();
        string key = UnitsOf(content, "shaders/test/compute.slang")[0].Key;
        using var folder = new TempFolder();
        ShaderCompiler.KeepCached(folder.Path, key, FakeEntry(key, "cached").ToModule());

        var recorder = new ShaderManifestRecorder(folder.File("run.manifest"));
        var compiled = ShaderCompiler.Compile(FileProgram("shaders/test/compute.slang"), "compute", Options(content, ShaderSourcing.Compile, cache: folder.Path, recorder: recorder), out string error);

        // It came out of the cache, the fake module proves Slang never saw it
        Assert.True(compiled is not null, error);
        Assert.Equal(FakeSpirv(1), compiled.Stages[0].Spirv);

        var entry = Assert.Single(ShaderManifest.Load(recorder.File).Entries);
        Assert.Equal((ShaderRequestKind.File, "shaders/test/compute.slang", CAPACITY), (entry.Kind, entry.Path, entry.BindlessCapacity));
        Assert.Equal(new[] { "shaders/test/compute.slang", "shaders/common/tint.slang" }, entry.Inputs.Select(input => input.Path));
    }

    [Fact]
    public void AProgramThatFailsInThePackIsStillWrittenDown()
    {
        using var folder = new TempFolder();
        var recorder = new ShaderManifestRecorder(folder.File("run.manifest"));

        ShaderCompiler.Compile(FileProgram("shaders/test/compute.slang"), "compute", Options(Sources(), ShaderSourcing.Packed, recorder: recorder), out _);
        ShaderCompiler.Compile(InlineProgram("float4 f() { return 1; }\n"), "made-up", Options(Sources(), ShaderSourcing.Packed, recorder: recorder), out _);

        var entries = recorder.Manifest.Entries;
        Assert.Equal(2, entries.Count);
        Assert.Equal("float4 f() { return 1; }\n", entries.Single(entry => entry.Kind == ShaderRequestKind.Inline).Source);
    }

    [Fact]
    public void TheTextIsTheSameWhateverOrderThingsWereAskedIn()
    {
        var content = Sources();
        var a = new ShaderManifest();
        var b = new ShaderManifest();
        var entries = new[] { Entry(content, "shaders/test/compute.slang"), Entry(content, "shaders/test/draw.slang"), Entry(content, "shaders/test/legacy.hlsl"), Entry(content, "shaders/test/compute.slang", 1024) };

        foreach (var entry in entries) a.Add(entry);
        foreach (var entry in entries.AsEnumerable().Reverse()) b.Add(entry);

        Assert.Equal(a.Write(), b.Write());
        Assert.DoesNotContain('\r', a.Write());
        Assert.Equal(a.Write(), ShaderManifest.Parse(a.Write(), "again").Write());
        Assert.Equal(4, ShaderManifest.Parse(a.Write(), "again").Entries.Count);
    }

    [Fact]
    public void AnEditedProgramReplacesItsOldEntryAndTheRestStay()
    {
        using var folder = new TempFolder();
        string file = folder.File("run.manifest");

        var before = new ShaderManifest();
        before.Add(Entry(Sources(), "shaders/test/compute.slang"));
        before.Add(Entry(Sources(), "shaders/test/draw.slang"));
        before.Save(file);

        // A later run with the include changed records into the same file
        var edited = Sources("\n", ("shaders/common/tint.slang", "struct Params { float4 tint; float scale; float extra; };\nBIND_UNIFORM(1) ConstantBuffer<Params> params;\n"));
        var recorder = new ShaderManifestRecorder(file);
        ShaderCompiler.Compile(FileProgram("shaders/test/compute.slang"), "compute", Options(edited, ShaderSourcing.Packed, recorder: recorder), out _);

        var after = ShaderManifest.Load(file).Entries;
        Assert.Equal(2, after.Count);
        Assert.NotEqual(before.Entries.Single(e => e.Path.EndsWith("compute.slang")).Hash, after.Single(e => e.Path.EndsWith("compute.slang")).Hash);
        Assert.Equal(before.Entries.Single(e => e.Path.EndsWith("draw.slang")), after.Single(e => e.Path.EndsWith("draw.slang")));
    }

    [Theory]
    [InlineData("", "isn't a shader manifest")]
    [InlineData("horizon-shader-manifest\t9\n", "manifest format 9")]
    [InlineData("horizon-shader-manifest\t1\ninput\tshaders/a.slang\t0000000000000000000000000000000000000000000000000000000000000000\n", "line 2 is an input before any program")]
    [InlineData("horizon-shader-manifest\t1\nfile\tglsl\t4096\t0000000000000000000000000000000000000000000000000000000000000000\tshaders/a.slang\n", "line 2 has a language 'glsl'")]
    [InlineData("horizon-shader-manifest\t1\nfile\tslang\t4096\tnope\tshaders/a.slang\n", "line 2 has a hash")]
    [InlineData("horizon-shader-manifest\t1\nfile\tslang\t4096\t0000000000000000000000000000000000000000000000000000000000000000\tC:/abs/a.slang\n", "line 2 has a path")]
    [InlineData("horizon-shader-manifest\t1\nfile\tslang\t4096\t0000000000000000000000000000000000000000000000000000000000000000\t../a.slang\n", "line 2 has a path")]
    [InlineData("horizon-shader-manifest\t1\nprogram\tx\n", "line 2 starts with 'program'")]
    public void AManifestThatsOffSaysWhereAndWhy(string text, string said)
    {
        var e = Assert.Throws<FormatException>(() => ShaderManifest.Parse(text, "bad.manifest"));
        Assert.Contains("bad.manifest", e.Message);
        Assert.Contains(said, e.Message);
    }

    /// <summary>The entry a run would record for a file of some content.</summary>
    internal static ShaderManifestEntry Entry(Horizon.Content.IContentFiles content, string path, uint capacity = CAPACITY)
    {
        using var preprocessor = new ShaderPreprocessor(content, capacity);
        string source = preprocessor.ProcessFile(path);
        var definition = new ShaderDefinition(ShaderStage.Fragment, path);
        return ShaderManifestRecorder.EntryFor(definition, "x", ShaderCompiler.LanguageOf(path), preprocessor, source, content, out _)!;
    }
}
