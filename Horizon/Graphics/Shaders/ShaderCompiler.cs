using System.Text;

using Horizon.Content;
using Horizon.Logging;

namespace Horizon.Graphics;

/// <summary>Which language a shader file is written in, told by its extension.</summary>
public enum ShaderLanguage
{
    /// <summary>.slang, the engine's own shaders. One file holds every stage, each marked <c>[shader("vertex")]</c> and so on.</summary>
    Slang,

    /// <summary>.hlsl, compiled by Slang in its HLSL mode. Entry points are vsMain, fsMain (or psMain), csMain and gsMain.</summary>
    Hlsl
}

/// <summary>One stage of a compiled program and where it starts. The stages of one Slang file share one SPIR-V module.</summary>
public sealed record CompiledStage(ShaderStage Stage, string EntryPoint, byte[] Spirv, uint[] ThreadGroupSize);

/// <summary>What came out of compiling a program, for <see cref="Shader"/> to make modules of.</summary>
public sealed class CompiledShader
{
    public List<CompiledStage> Stages { get; } = [];
    public UniformBlockLayout Params { get; set; } = UniformBlockLayout.Empty;
}

/// <summary>
/// Turns shader source into SPIR-V with the Slang compiler, which the engine brings with it, and keeps what it
/// compiled on disk by a hash of the source so nothing is compiled twice. Slang is the language the engine's own
/// shaders are in, and HLSL is taken as well through Slang's HLSL mode. Every program's <c>Params</c> block comes
/// back laid out (see <see cref="UniformBlockLayout"/>) out of the compiler's reflection.
/// <para>
/// Set <c>HORIZON_SHADER_CACHE=off</c> to compile every time, or to a folder to keep the cache there.
/// </para>
/// <para>
/// A platform with no Slang (a phone) loads the same modules out of a shader pack in content instead, see
/// <see cref="ShaderPack"/>. <c>HORIZON_SHADERS=packed</c> makes a desktop run do that too and nothing else, no
/// cache and no compiler, so a pack can be proven without a phone. <c>HORIZON_SHADERS=compile</c> never looks at a
/// pack. Unset, the compiler is used and the pack only once Slang turns out not to be there.
/// <c>HORIZON_SHADER_MANIFEST=&lt;file&gt;</c> writes down every program asked for, cache hits too, which is what
/// <c>Horizon.Shaders.Build</c> makes a pack out of.
/// </para>
/// </summary>
public static partial class ShaderCompiler
{
    private const string SOURCING_VARIABLE = "HORIZON_SHADERS";

    private static ShaderOptions? defaults;

    /// <summary>The language a file is in, by its extension.</summary>
    public static ShaderLanguage LanguageOf(string? path) =>
        Path.GetExtension(path ?? string.Empty).Equals(".hlsl", StringComparison.OrdinalIgnoreCase) ? ShaderLanguage.Hlsl : ShaderLanguage.Slang;

    /// <summary>What the environment asks for, read once.</summary>
    // Programs compile on several threads at once and only one recorder may own the manifest file
    internal static ShaderOptions Defaults => LazyInitializer.EnsureInitialized(ref defaults, DefaultsFromEnvironment);

    private static ShaderOptions DefaultsFromEnvironment()
    {
        ShaderSourcing sourcing = SourcingFrom(Environment.GetEnvironmentVariable(SOURCING_VARIABLE));
        if (sourcing == ShaderSourcing.Packed)
            Log.Info("[ShaderCompiler] HORIZON_SHADERS=packed, every shader comes out of the shader pack and nothing is compiled or cached.");

        return new ShaderOptions
        {
            Sourcing = sourcing,
            // A packed run never reads the cache, an old module there would hide a pack that's missing it
            CacheFolder = sourcing == ShaderSourcing.Packed ? null : CacheDirectory(),
            Recorder = ShaderManifestRecorder.FromEnvironment()
        };
    }

    internal static ShaderSourcing SourcingFrom(string? setting) => setting?.Trim().ToLowerInvariant() switch
    {
        "packed" or "pack" => ShaderSourcing.Packed,
        "compile" or "compiler" => ShaderSourcing.Compile,
        _ => ShaderSourcing.Automatic
    };

    /// <summary>
    /// Compiles every stage of a description. Null, with what went wrong in <paramref name="error"/>, if any of it didn't compile.
    /// </summary>
    public static CompiledShader? Compile(in ShaderDescription description, string name, out string error) =>
        Compile(description, name, Defaults, out error);

    /// <summary>The same with the options said rather than read from the environment, what the tests and the pack builder use.</summary>
    internal static CompiledShader? Compile(in ShaderDescription description, string name, ShaderOptions options, out string error)
    {
        var result = new CompiledShader();
        var errors = new StringBuilder();

        // One content source for the whole program, the installed one can be swapped while it compiles
        IContentFiles content = options.Content ?? ContentFiles.Source;

        // The files of a program compile side by side, it is the one thing about shaders that takes a while
        var definitions = description.Definitions;
        var outcomes = new (CompiledStage[] Stages, UniformBlockLayout? Params, string Error)[definitions.Length];

        Parallel.For(0, definitions.Length, i => outcomes[i] = CompileDefinition(definitions[i], name, options, content));

        foreach (var (stages, parameters, said) in outcomes)
        {
            if (said.Length > 0)
            {
                errors.AppendLine(said);
                continue;
            }

            result.Stages.AddRange(stages);

            // Every file that has a Params block has to see the same one, the draw writes one block for all of them
            if (parameters is { Size: > 0 })
            {
                if (result.Params.Size > 0 && result.Params.Size != parameters.Size)
                    Log.Warning($"[ShaderCompiler] The files of {name} don't agree on their Params block ({result.Params.Size} and {parameters.Size} bytes), the first one wins.");
                else if (result.Params.Size == 0)
                    result.Params = parameters;
            }
        }

        error = errors.ToString().Trim();
        if (error.Length > 0) return null;

        if (result.Stages.Count == 0)
        {
            error = $"{name} has no stages in it.";
            return null;
        }

        return result;
    }

    private static (CompiledStage[], UniformBlockLayout?, string) CompileDefinition(in ShaderDefinition definition, string name, ShaderOptions options, IContentFiles content)
    {
        ShaderLanguage language = LanguageOf(definition.File);
        string fileName = definition.File ?? $"{name}.{definition.Stage}";

        string source;
        try
        {
            using var preprocessor = new ShaderPreprocessor(content, options.BindlessCapacity);
            source = definition.File is null
                ? preprocessor.ProcessSource(string.Empty, ShaderLines.Split(definition.Source))
                : preprocessor.ProcessFile(definition.File);

            // Before anything is looked up, so a program that comes out of the cache or the pack is written down too
            options.Recorder?.Record(definition, name, language, preprocessor, source, content);
        }
        catch (Exception e)
        {
            return ([], null, $"{fileName}, {e.Message}");
        }

        var units = UnitsOf(source, language);
        if (units.Length == 0) return ([], null, $"{fileName} has none of vsMain, fsMain, psMain, csMain or gsMain in it.");

        var stages = new List<CompiledStage>();
        UniformBlockLayout? parameters = null;
        foreach (var unit in units)
        {
            ShaderModule module;
            try
            {
                module = Resolve(unit, fileName, options, content);
            }
            catch (ShaderUnavailableException e)
            {
                return ([], null, e.Message);
            }
            catch (Exception e)
            {
                return ([], null, $"{unit.Label(fileName)}\n{e.Message.Trim()}");
            }

            stages.AddRange(module.Stages);
            parameters ??= module.Params;
        }

        return ([.. stages], parameters, string.Empty);
    }

    /// <summary>
    /// Helper method to get one module from wherever the options say. The pack alone when it's asked for, which
    /// never falls back to the cache or the compiler, otherwise the cache, then Slang, then the pack if Slang isn't there.
    /// </summary>
    private static ShaderModule Resolve(in ShaderUnit unit, string fileName, ShaderOptions options, IContentFiles content)
    {
        if (options.Sourcing == ShaderSourcing.Packed || (options.Sourcing == ShaderSourcing.Automatic && slangMissing))
            return FromPack(unit, fileName, options, content);

        if (options.CacheFolder is { } folder && TryLoadCached(folder, unit.Key, out var kept))
            return kept;

        ShaderModule made;
        try
        {
            made = CompileFresh(unit, fileName);
        }
        catch (Exception e) when (options.Sourcing == ShaderSourcing.Automatic && IsSlangMissing(e))
        {
            if (!slangMissing) Log.Info("[ShaderCompiler] Slang isn't here, shaders come out of the shader pack from now on.");
            slangMissing = true;
            return FromPack(unit, fileName, options, content);
        }

        if (options.CacheFolder is { } keepIn) KeepCached(keepIn, unit.Key, made);
        return made;
    }
}

/// <summary>Where compiled programs come from.</summary>
internal enum ShaderSourcing
{
    /// <summary>The cache and the compiler, and the pack once Slang turns out not to be there.</summary>
    Automatic,

    /// <summary>The cache and the compiler, never a pack.</summary>
    Compile,

    /// <summary>The pack and nothing else.</summary>
    Packed
}

/// <summary>How <see cref="ShaderCompiler"/> goes about it. Null members mean what a running game would use.</summary>
internal sealed record ShaderOptions
{
    public ShaderSourcing Sourcing { get; init; }

    /// <summary>The disk cache, null for none.</summary>
    public string? CacheFolder { get; init; }

    /// <summary>Writes down every program asked for, null when nobody asked.</summary>
    public ShaderManifestRecorder? Recorder { get; init; }

    /// <summary>The bindless table compiled for, the device's when null.</summary>
    public uint? BindlessCapacity { get; init; }

    /// <summary>Where sources and packs are read from, the installed content when null.</summary>
    public IContentFiles? Content { get; init; }

    /// <summary>The packs to look in, the ones in the content when null.</summary>
    public ShaderPackSet? Packs { get; init; }
}

/// <summary>A program that can't be had, with a message that already says which and why.</summary>
internal sealed class ShaderUnavailableException(string message) : Exception(message);
