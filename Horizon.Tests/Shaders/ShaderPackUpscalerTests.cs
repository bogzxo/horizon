using Horizon.Content;
using Horizon.Graphics;

namespace Horizon.Tests.Shaders;

public class ShaderPackUpscalerTests
{
    [Theory]
    [InlineData("copy")]
    [InlineData("cas")]
    [InlineData("upscale_nearest")]
    [InlineData("gsr1")]
    [InlineData("fsr1_easu")]
    [InlineData("fsr1_rcas")]
    [InlineData("crt")]
    [InlineData("crt_shrink")]
    public void Shipped_pack_contains_the_runtime_key_for_every_offered_pass(string shader)
    {
        var content = new DiskContent(AppContext.BaseDirectory);
        var options = ShaderTestFiles.Options(content, ShaderSourcing.Packed);
        var compiled = ShaderCompiler.Compile(ShaderDescription.Screen($"shaders/post/{shader}.slang"), shader, options, out string error);
        Assert.True(compiled is not null, error);
        Assert.Contains(compiled!.Stages, stage => stage.Stage == ShaderStage.Vertex);
        Assert.Contains(compiled.Stages, stage => stage.Stage == ShaderStage.Fragment);
    }

    [Theory]
    [InlineData("fsr1_easu")]
    [InlineData("fsr1_rcas")]
    public void Missing_one_fsr_pass_is_an_unavailable_shader_not_an_empty_valid_program(string shader)
    {
        var content = new DiskContent(AppContext.BaseDirectory);
        var shipped = ShaderPack.Load(content, "shaders/packed/horizon.hzshaders");
        string path = $"shaders/post/{shader}.slang";
        var incomplete = ShaderPackWriter.Write(shipped.Fingerprint, shipped.TargetProfile, shipped.Manifest, shipped.BindlessCapacities,
            shipped.Entries.Where(entry => entry.Origin != path));
        var packs = new ShaderPackSet([ShaderPack.Read(new MemoryStream(incomplete), "missing-fsr-pass")], []);
        var options = ShaderTestFiles.Options(content, ShaderSourcing.Packed, packs: packs);
        Assert.Null(ShaderCompiler.Compile(ShaderDescription.Screen(path), shader, options, out string error));
        Assert.Contains(path, error);
        Assert.Contains("4096", error);
    }
}
