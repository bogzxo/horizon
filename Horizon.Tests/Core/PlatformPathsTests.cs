using Horizon.Core.Platform;
using Horizon.Graphics;

namespace Horizon.Tests.Core;

/// <summary>
/// The program's folders kept next to it or in the user's, and the shaders' cache keys. BesideProgram is one switch for
/// the whole process, so these run on their own and put it back after.
/// </summary>
[CollectionDefinition(NAME, DisableParallelization = true)]
public sealed class GlobalPathsCollection
{
    public const string NAME = "global paths";
}

[Collection(GlobalPathsCollection.NAME)]
public class PlatformPathsTests
{
    [Fact]
    public void Beside_the_program_every_folder_is_next_to_the_exe()
    {
        bool was = PlatformPaths.BesideProgram;
        try
        {
            PlatformPaths.BesideProgram = true;

            // The test's bin folder is written to by the build, so it's writable and the switch takes
            Assert.True(PlatformPaths.BesideProgram);
            Assert.True(PlatformPaths.Same(Path.Combine(PlatformPaths.Program, PlatformPaths.SETTINGS_BESIDE), PlatformPaths.Config("Anything")));
            Assert.True(PlatformPaths.Same(Path.Combine(PlatformPaths.Program, PlatformPaths.DATA_BESIDE), PlatformPaths.Data("Anything")));
            Assert.True(PlatformPaths.Same(Path.Combine(PlatformPaths.Program, PlatformPaths.DATA_BESIDE), PlatformPaths.State("Anything")));
            Assert.True(PlatformPaths.Same(Path.Combine(PlatformPaths.Program, PlatformPaths.CACHE_BESIDE), PlatformPaths.Cache("Anything")));
        }
        finally
        {
            PlatformPaths.BesideProgram = was;
        }
    }

    [Fact]
    public void Otherwise_they_are_the_users_and_the_roots_never_move()
    {
        bool was = PlatformPaths.BesideProgram;
        try
        {
            PlatformPaths.BesideProgram = false;
            Assert.True(PlatformPaths.IsUnder(PlatformPaths.Cache("HorizonTests"), PlatformPaths.CacheRoot));
            Assert.True(PlatformPaths.IsUnder(PlatformPaths.Config("HorizonTests"), PlatformPaths.ConfigRoot));

            // The roots are the system's whichever way, the trash is in one of them
            string root = PlatformPaths.DataRoot;
            PlatformPaths.BesideProgram = true;
            Assert.Equal(root, PlatformPaths.DataRoot);
        }
        finally
        {
            PlatformPaths.BesideProgram = was;
        }
    }

    [Fact]
    public void A_shaders_key_changes_with_the_compiler_and_its_flags()
    {
        string key = ShaderCompiler.KeyFor("float4 main() {}", "slang", string.Empty);
        Assert.Equal(key, ShaderCompiler.KeyFor("float4 main() {}", "slang", string.Empty, ShaderCompiler.CompilerFingerprint));
        Assert.NotEqual(key, ShaderCompiler.KeyFor("float4 main() {}", "slang", string.Empty, ShaderCompiler.CompilerFingerprint + " -O3"));
        Assert.NotEqual(key, ShaderCompiler.KeyFor("float4 main() { }", "slang", string.Empty));
        Assert.Contains("-target spirv", ShaderCompiler.CompilerFingerprint);
    }
}
