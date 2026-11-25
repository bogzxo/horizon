using System.Runtime.CompilerServices;

using Horizon.Graphics;

using static Horizon.Tests.Shaders.ShaderTestFiles;

namespace Horizon.Tests.Shaders;

/// <summary>
/// The glass shader as it is in the engine, compiled for real: that it compiles at all, and that its Params has every
/// uniform UIGlassPass sets by name, which nothing else finds out before a window is up with glass in it.
/// </summary>
[Collection(SlangCompiles.NAME)]
public class GlassShaderTests
{
    private static string ShaderFolder([CallerFilePath] string at = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(at)!, "..", "..", "Horizon", "shaders"));

    [Fact]
    public void TheGlassShaderCompilesWithTheUniformsThePassSets()
    {
        string folder = ShaderFolder();
        var sources = Sources("\n",
            ("shaders/post/glass.slang", File.ReadAllText(Path.Combine(folder, "post", "glass.slang"))),
            ("shaders/common/screen.slang", File.ReadAllText(Path.Combine(folder, "common", "screen.slang"))));

        var program = new ShaderDescription { Definitions = [new ShaderDefinition(ShaderStage.Fragment, "shaders/post/glass.slang")] };
        var compiled = ShaderCompiler.Compile(program, "glass", Options(sources, ShaderSourcing.Compile), out string error);

        Assert.True(compiled is not null, error);
        foreach (string uniform in new[] { "uRect", "uClip", "uSharpRect", "uTint", "uTexel", "uRadius", "uOpacity", "uShare", "uRefraction", "uBevelDepth", "uBevelWidth", "uMagnify", "uAberration" })
            Assert.True(compiled.Params.TryGet(uniform, out _), uniform);
    }
}
