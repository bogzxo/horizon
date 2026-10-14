using System.Security.Cryptography;
using System.Text;

using Horizon.Graphics.Vulkan;
using Horizon.Logging;

using Slangc.NET;

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
/// </summary>
public static class ShaderCompiler
{
    private const string CACHE_VARIABLE = "HORIZON_SHADER_CACHE";

    private static readonly string[] SlangArguments = ["-target", "spirv", "-profile", "spirv_1_6", "-matrix-layout-row-major", "-fvk-use-entrypoint-name"];

    // What an HLSL file calls its entry points, and the stage each one is
    private static readonly (string Name, ShaderStage Stage)[] HlslEntryPoints =
    [
        ("vsMain", ShaderStage.Vertex),
        ("fsMain", ShaderStage.Fragment),
        ("psMain", ShaderStage.Fragment),
        ("csMain", ShaderStage.Compute),
        ("gsMain", ShaderStage.Geometry)
    ];

    private static readonly Lock slangLock = new();
    private static bool helpersLoaded;

    /// <summary>
    /// Helper method to load the library Slang's optimizer lives in by its full path, once. Slang asks for it by name
    /// and the loader only looks next to the exe, which is not where the package puts it.
    /// </summary>
    private static void LoadHelpers()
    {
        if (helpersLoaded) return;
        helpersLoaded = true;

        string rid = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        rid += System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "-arm64" : "-x64";
        string name = OperatingSystem.IsWindows() ? "slang-glslang.dll" : OperatingSystem.IsMacOS() ? "libslang-glslang.dylib" : "libslang-glslang.so";

        foreach (string candidate in new[] { Path.Combine(AppContext.BaseDirectory, name), Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", name) })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                System.Runtime.InteropServices.NativeLibrary.Load(candidate);
                return;
            }
            catch (Exception)
            {
                // Without it Slang still compiles, it only can't optimise
            }
        }
    }

    private static string? cacheDirectory;
    private static bool checkedCacheDirectory;

    /// <summary>The language a file is in, by its extension.</summary>
    public static ShaderLanguage LanguageOf(string? path) =>
        Path.GetExtension(path ?? string.Empty).Equals(".hlsl", StringComparison.OrdinalIgnoreCase) ? ShaderLanguage.Hlsl : ShaderLanguage.Slang;

    /// <summary>
    /// Compiles every stage of a description. Null, with what went wrong in <paramref name="error"/>, if any of it didn't compile.
    /// </summary>
    public static CompiledShader? Compile(in ShaderDescription description, string name, out string error)
    {
        var result = new CompiledShader();
        var errors = new StringBuilder();

        // The files of a program compile side by side, it is the one thing about shaders that takes a while
        var definitions = description.Definitions;
        var outcomes = new (CompiledStage[] Stages, UniformBlockLayout? Params, string Error)[definitions.Length];

        Parallel.For(0, definitions.Length, i => outcomes[i] = CompileDefinition(definitions[i], name));

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

    private static (CompiledStage[], UniformBlockLayout?, string) CompileDefinition(in ShaderDefinition definition, string name)
    {
        ShaderLanguage language = LanguageOf(definition.File);
        string fileName = definition.File ?? $"{name}.{definition.Stage}";

        string source;
        try
        {
            using var preprocessor = new ShaderPreprocessor();
            source = definition.File is null
                ? preprocessor.ProcessSource(string.Empty, definition.Source.Split('\n').Select(line => line.TrimEnd('\r')))
                : preprocessor.ProcessFile(definition.File);
        }
        catch (Exception e)
        {
            return ([], null, $"{fileName}, {e.Message}");
        }

        return language == ShaderLanguage.Hlsl ? CompileHlsl(source, fileName) : CompileSlang(source, fileName);
    }

    private static (CompiledStage[], UniformBlockLayout?, string) CompileSlang(string source, string fileName)
    {
        string key = KeyFor(source, "slang", string.Empty);
        if (TryLoadCached(key, out var kept)) return (kept.Stages, kept.Params, string.Empty);

        try
        {
            SlangReflection reflection;
            byte[] spirv;
            lock (slangLock)
            {
                LoadHelpers();
                spirv = SlangCompiler.CompileWithReflection(source, SlangArguments, out reflection);
            }

            var stages = new List<CompiledStage>();
            foreach (var entry in reflection.EntryPoints)
            {
                ShaderStage? stage = entry.Stage switch
                {
                    SlangStage.Vertex => ShaderStage.Vertex,
                    SlangStage.Fragment => ShaderStage.Fragment,
                    SlangStage.Compute => ShaderStage.Compute,
                    SlangStage.Geometry => ShaderStage.Geometry,
                    _ => null
                };

                if (stage is null)
                {
                    Log.Warning($"[ShaderCompiler] {fileName} has a {entry.Stage} entry point, which the engine has no use for.");
                    continue;
                }

                stages.Add(new CompiledStage(stage.Value, entry.Name, spirv, entry.ThreadGroupSize ?? []));
            }

            var parameters = ParamsOf(reflection);
            KeepCached(key, stages, parameters);
            return ([.. stages], parameters, string.Empty);
        }
        catch (Exception e)
        {
            return ([], null, $"{fileName}\n{e.Message.Trim()}");
        }
    }

    private static (CompiledStage[], UniformBlockLayout?, string) CompileHlsl(string source, string fileName)
    {
        var stages = new List<CompiledStage>();
        UniformBlockLayout? parameters = null;

        foreach (var (entryName, stage) in HlslEntryPoints)
        {
            if (!source.Contains(entryName + "(", StringComparison.Ordinal)) continue;

            string key = KeyFor(source, "hlsl", entryName);
            if (TryLoadCached(key, out var kept))
            {
                stages.AddRange(kept.Stages);
                parameters ??= kept.Params;
                continue;
            }

            string[] arguments = [.. SlangArguments, "-lang", "hlsl", "-entry", entryName, "-stage", StageName(stage)];
            try
            {
                SlangReflection reflection;
                byte[] spirv;
                lock (slangLock)
                {
                    LoadHelpers();
                    spirv = SlangCompiler.CompileWithReflection(source, arguments, out reflection);
                }

                uint[] groups = reflection.EntryPoints.FirstOrDefault()?.ThreadGroupSize ?? [];
                var made = new CompiledStage(stage, entryName, spirv, groups);
                var found = ParamsOf(reflection);

                KeepCached(key, [made], found);
                stages.Add(made);
                parameters ??= found;
            }
            catch (Exception e)
            {
                return ([], null, $"{fileName} ({entryName})\n{e.Message.Trim()}");
            }
        }

        if (stages.Count == 0) return ([], null, $"{fileName} has none of vsMain, fsMain, psMain, csMain or gsMain in it.");
        return ([.. stages], parameters, string.Empty);
    }

    private static string StageName(ShaderStage stage) => stage switch
    {
        ShaderStage.Vertex => "vertex",
        ShaderStage.Fragment => "fragment",
        ShaderStage.Compute => "compute",
        _ => "geometry"
    };

    /// <summary>Helper method to find the Params block among what Slang reflected, the constant buffer on binding 1 of set 0.</summary>
    private static UniformBlockLayout ParamsOf(SlangReflection reflection)
    {
        foreach (var parameter in reflection.Parameters)
        {
            if (parameter.Type.Kind != SlangTypeKind.ConstantBuffer) continue;

            bool onParams = parameter.Bindings.Any(binding => binding.Kind == SlangParameterCategory.DescriptorTableSlot && binding.Space == 0 && binding.Index == DescriptorLayouts.PARAMS_BINDING);
            if (!onParams) continue;

            var element = parameter.Type.ConstantBuffer.ElementType;
            if (element.Kind != SlangTypeKind.Struct) continue;

            var members = new List<UniformBlockLayout.Member>();
            foreach (var field in element.Struct.Fields)
            {
                var binding = field.Bindings.FirstOrDefault(b => b.Kind == SlangParameterCategory.Uniform);
                if (binding is null) continue;

                int count = field.Type.Kind == SlangTypeKind.Array ? (int)field.Type.Array.ElementCount : 0;
                int stride = count > 0 ? (int)field.Type.Array.UniformStride : 0;
                int size = count > 0 ? stride : (int)binding.Size;
                members.Add(new UniformBlockLayout.Member(field.Name, TypeName(field.Type), (int)binding.Offset, size, count, stride));
            }

            return UniformBlockLayout.FromMembers(members);
        }

        return UniformBlockLayout.Empty;
    }

    private static string TypeName(SlangType type) => type.Kind switch
    {
        SlangTypeKind.Scalar => type.Scalar.ScalarType.ToString().ToLowerInvariant(),
        SlangTypeKind.Vector => $"float{type.Vector.ElementCount}",
        SlangTypeKind.Matrix => $"float{type.Matrix.RowCount}x{type.Matrix.ColumnCount}",
        SlangTypeKind.Array => "array",
        _ => type.Kind.ToString().ToLowerInvariant()
    };

    private static string KeyFor(string source, string language, string entry)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(language));
        hash.AppendData(Encoding.UTF8.GetBytes(entry));
        hash.AppendData(Encoding.UTF8.GetBytes(source));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /* The cache on disk. The module, and a small text file beside it saying where its entry points are and how its Params are laid out */

    /// <summary>Where compiled shaders (and the driver's pipeline cache) are kept, null if they aren't.</summary>
    public static string? CacheDirectory()
    {
        if (checkedCacheDirectory) return cacheDirectory;
        checkedCacheDirectory = true;

        string? setting = Environment.GetEnvironmentVariable(CACHE_VARIABLE);
        if (string.Equals(setting, "off", StringComparison.OrdinalIgnoreCase)) return cacheDirectory = null;

        // By the game rather than the process, which is "dotnet" for a game that is started through it
        string game = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "Horizon";
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        if (string.IsNullOrWhiteSpace(local)) local = AppContext.BaseDirectory;

        cacheDirectory = string.IsNullOrWhiteSpace(setting) ? Path.Combine(local, "Horizon", game, "Shaders") : setting;
        Log.Info($"[ShaderCompiler] Compiled shaders are kept in '{cacheDirectory}'.");
        return cacheDirectory;
    }

    private static bool TryLoadCached(string key, out (CompiledStage[] Stages, UniformBlockLayout Params) kept)
    {
        kept = default;
        string? folder = CacheDirectory();
        if (folder is null) return false;

        string spirvPath = Path.Combine(folder, key + ".spv"), notesPath = Path.Combine(folder, key + ".notes");
        if (!File.Exists(spirvPath) || !File.Exists(notesPath)) return false;

        try
        {
            byte[] spirv = File.ReadAllBytes(spirvPath);
            var stages = new List<CompiledStage>();
            var members = new List<UniformBlockLayout.Member>();

            foreach (string line in File.ReadAllLines(notesPath))
            {
                string[] parts = line.Split('\t');
                if (parts.Length == 0) continue;

                switch (parts[0])
                {
                    case "entry" when parts.Length >= 6:
                        stages.Add(new CompiledStage(Enum.Parse<ShaderStage>(parts[1]), parts[2], spirv, [uint.Parse(parts[3]), uint.Parse(parts[4]), uint.Parse(parts[5])]));
                        break;
                    case "member" when parts.Length >= 7:
                        members.Add(new UniformBlockLayout.Member(parts[1], parts[2], int.Parse(parts[3]), int.Parse(parts[4]), int.Parse(parts[5]), int.Parse(parts[6])));
                        break;
                }
            }

            if (stages.Count == 0) return false;
            kept = ([.. stages], UniformBlockLayout.FromMembers(members));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void KeepCached(string key, IReadOnlyList<CompiledStage> stages, UniformBlockLayout parameters)
    {
        string? folder = CacheDirectory();
        if (folder is null || stages.Count == 0) return;

        try
        {
            Directory.CreateDirectory(folder);

            var notes = new StringBuilder();
            foreach (var stage in stages)
            {
                uint[] groups = stage.ThreadGroupSize.Length == 3 ? stage.ThreadGroupSize : [0, 0, 0];
                notes.Append("entry\t").Append(stage.Stage).Append('\t').Append(stage.EntryPoint).Append('\t').Append(groups[0]).Append('\t').Append(groups[1]).Append('\t').Append(groups[2]).Append('\n');
            }

            foreach (var member in parameters.Members)
                notes.Append("member\t").Append(member.Name).Append('\t').Append(member.Type).Append('\t').Append(member.Offset).Append('\t').Append(member.Size).Append('\t').Append(member.ArrayLength).Append('\t').Append(member.ArrayStride).Append('\n');

            // Written next to where they go and moved there whole, so a game that is closed halfway leaves no half a shader
            string spirvPath = Path.Combine(folder, key + ".spv"), notesPath = Path.Combine(folder, key + ".notes");
            string partial = "." + Environment.CurrentManagedThreadId + ".partial";
            File.WriteAllBytes(spirvPath + partial, stages[0].Spirv);
            File.WriteAllText(notesPath + partial, notes.ToString());
            File.Move(spirvPath + partial, spirvPath, overwrite: true);
            File.Move(notesPath + partial, notesPath, overwrite: true);
        }
        catch (Exception)
        {
            // Not kept, compiled again next time
        }
    }
}
