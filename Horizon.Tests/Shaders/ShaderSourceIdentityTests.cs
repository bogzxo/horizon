using Horizon.Graphics;
using Horizon.Tests.Content;

using static Horizon.Tests.Shaders.ShaderTestFiles;

namespace Horizon.Tests.Shaders;

/// <summary>
/// What a shader is kept and shipped under. The same sources have to give the same key on Windows and out of an APK,
/// and anything that changes the module (the bindless table size baked into the prelude, an include) has to change it.
/// </summary>
public class ShaderSourceIdentityTests
{
    [Fact]
    public void CrlfAndLfSourcesGiveTheSameTextAndTheSameKey()
    {
        var lf = Sources("\n");
        var crlf = Sources("\r\n");
        var oldMac = Sources("\r");

        using var a = new ShaderPreprocessor(lf, CAPACITY);
        using var b = new ShaderPreprocessor(crlf, CAPACITY);
        using var c = new ShaderPreprocessor(oldMac, CAPACITY);
        string fromLf = a.ProcessFile("shaders/test/compute.slang");
        string fromCrlf = b.ProcessFile("shaders/test/compute.slang");

        Assert.DoesNotContain('\r', fromCrlf);
        Assert.Equal(fromLf, fromCrlf);
        Assert.Equal(fromLf, c.ProcessFile("shaders/test/compute.slang"));
        Assert.Equal(UnitsOf(lf, "shaders/test/compute.slang")[0].Key, UnitsOf(crlf, "shaders/test/compute.slang")[0].Key);

        // And the files read are the same files with the same hashes either way
        Assert.Equal(a.Inputs.Select(input => input.Hash), b.Inputs.Select(input => input.Hash));
    }

    [Fact]
    public void InlineSourceIsTheSameWhateverItsLineEndings()
    {
        var content = Sources();
        using var preprocessor = new ShaderPreprocessor(content, CAPACITY);

        string lf = preprocessor.ProcessSource(string.Empty, ShaderLines.Split("#include <common/tint.slang>\nfloat4 f() { return params.tint; }\n"));
        string crlf = preprocessor.ProcessSource(string.Empty, ShaderLines.Split("#include <common/tint.slang>\r\nfloat4 f() { return params.tint; }\r\n"));

        Assert.Equal(lf, crlf);
        Assert.Contains("struct Params", lf);
    }

    [Fact]
    public void TheBindlessCapacityIsSaidAndChangesTheKey()
    {
        var content = Sources("\n", ("shaders/test/bindless.slang", "BINDLESS_TEXTURES(uTextures);\n"));

        using var small = new ShaderPreprocessor(content, 1024);
        using var large = new ShaderPreprocessor(content, 4096);
        string a = small.ProcessFile("shaders/test/bindless.slang");
        string b = large.ProcessFile("shaders/test/bindless.slang");

        Assert.Equal(1024u, small.BindlessCapacity);
        Assert.Contains("Sampler2D name[1024]", a);
        Assert.Contains("Sampler2D name[4096]", b);
        Assert.NotEqual(UnitsOf(content, "shaders/test/bindless.slang", 1024)[0].Key, UnitsOf(content, "shaders/test/bindless.slang", 4096)[0].Key);
    }

    [Fact]
    public void AKeysPartsCantRunIntoEachOther()
    {
        string fingerprint = ShaderCompiler.CompilerFingerprint;
        Assert.NotEqual(ShaderCompiler.KeyFor("source", "hlsl", "vsMain", fingerprint), ShaderCompiler.KeyFor("vsMainsource", "hlsl", string.Empty, fingerprint));
        Assert.NotEqual(ShaderCompiler.KeyFor("x", "slang", string.Empty, fingerprint), ShaderCompiler.KeyFor(string.Empty, "slang", "x", fingerprint));
        Assert.Contains(ShaderCompiler.TARGET_PROFILE, fingerprint);
    }

    [Fact]
    public void ASlangFileIsOneModuleAndHlslOneAnEntryPoint()
    {
        var content = Sources();

        var slang = UnitsOf(content, "shaders/test/draw.slang");
        var hlsl = UnitsOf(content, "shaders/test/legacy.hlsl");

        Assert.Single(slang);
        Assert.Equal(new[] { "vsMain", "psMain" }, hlsl.Select(unit => unit.Entry));
        Assert.Equal(new[] { ShaderStage.Vertex, ShaderStage.Fragment }, hlsl.Select(unit => unit.Stage));
        Assert.NotEqual(hlsl[0].Key, hlsl[1].Key);
    }

    [Fact]
    public void ThePreprocessorSaysEveryFileItReadInOrder()
    {
        var content = Sources();
        using var preprocessor = new ShaderPreprocessor(content, CAPACITY);
        preprocessor.ProcessFile("shaders/test/compute.slang");

        Assert.Equal(new string?[] { "shaders/test/compute.slang", "shaders/common/tint.slang" },
            preprocessor.Inputs.Select(input => ShaderPaths.Logical(content, input.Path)));
        Assert.Equal(ShaderLines.Hash(ShaderLines.Canonical(TINT)), preprocessor.Inputs[1].Hash);
    }

    [Fact]
    public void ContentPathsAreWrittenTheSameEverywhere()
    {
        var content = Sources();
        var disk = new Horizon.Content.DiskContent(Path.GetTempPath());

        Assert.Equal("shaders/common/tint.slang", ShaderPaths.Logical(content, "shaders\\test\\..\\common\\tint.slang"));
        Assert.Equal("shaders/x.slang", ShaderPaths.Logical(disk, Path.Combine(Path.GetTempPath(), "shaders", "x.slang")));
        Assert.Null(ShaderPaths.Logical(disk, Path.Combine(Path.GetTempPath(), "..", "elsewhere.slang")));
    }
}
