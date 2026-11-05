using Horizon.Graphics.Vulkan;
using Horizon.Logging;

using Slangc.NET;

namespace Horizon.Graphics;

/// <summary>One module to make out of preprocessed source. A Slang file is one with every entry point in it, an HLSL file one an entry point.</summary>
internal readonly record struct ShaderUnit(ShaderLanguage Language, string Source, string Entry, ShaderStage Stage, string Key)
{
    /// <summary>The file, and the entry point for HLSL, as an error names it.</summary>
    public string Label(string fileName) => Entry.Length == 0 ? fileName : $"{fileName} ({Entry})";
}

/// <summary>What one module came to, its stages sharing one SPIR-V module and its Params block.</summary>
internal sealed record ShaderModule(CompiledStage[] Stages, UniformBlockLayout Params);

// Compiling with Slang itself. Nothing in here is touched by a packed run, so Slang's natives never load for one
public static partial class ShaderCompiler
{
    /// <summary>The SPIR-V version modules are made for. 1.6 wants a Vulkan 1.3 device, see notes/android-plan.md for 1.2.</summary>
    public const string TARGET_PROFILE = "spirv_1_6";

    private static readonly string[] SlangArguments = ["-target", "spirv", "-profile", TARGET_PROFILE, "-matrix-layout-row-major", "-fvk-use-entrypoint-name"];

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
    private static volatile bool slangMissing;

    /// <summary>The modules some preprocessed source makes, none for HLSL with no entry point the engine knows.</summary>
    internal static ShaderUnit[] UnitsOf(string source, ShaderLanguage language)
    {
        if (language == ShaderLanguage.Slang)
            return [new ShaderUnit(language, source, string.Empty, ShaderStage.Vertex, KeyFor(source, "slang", string.Empty))];

        var units = new List<ShaderUnit>();
        foreach (var (entryName, stage) in HlslEntryPoints)
        {
            if (!source.Contains(entryName + "(", StringComparison.Ordinal)) continue;
            units.Add(new ShaderUnit(language, source, entryName, stage, KeyFor(source, "hlsl", entryName)));
        }

        return [.. units];
    }

    /// <summary>Helper method to tell an exception that means Slang's natives aren't here from one that means the shader is wrong.</summary>
    private static bool IsSlangMissing(Exception? e)
    {
        for (; e is not null; e = e.InnerException)
        {
            if (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) return true;
        }

        return false;
    }

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
        // On Linux and macOS the package names it with Slang's version (libslang-glslang-2026.19.so), on Windows it doesn't
        string pattern = OperatingSystem.IsWindows() ? "slang-glslang*.dll" : OperatingSystem.IsMacOS() ? "libslang-glslang*.dylib" : "libslang-glslang*.so";

        foreach (string folder in new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native") })
        {
            string? candidate = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, pattern).Order(StringComparer.Ordinal).LastOrDefault() : null;
            if (candidate is null) continue;
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

    /// <summary>Compiles one module with Slang. Throws with Slang's own words when the source is wrong.</summary>
    internal static ShaderModule CompileFresh(in ShaderUnit unit, string fileName)
    {
        string[] arguments = unit.Entry.Length == 0
            ? SlangArguments
            : [.. SlangArguments, "-lang", "hlsl", "-entry", unit.Entry, "-stage", StageName(unit.Stage)];

        SlangReflection reflection;
        byte[] spirv;
        lock (slangLock)
        {
            LoadHelpers();
            spirv = SlangCompiler.CompileWithReflection(unit.Source, arguments, out reflection);
        }

        if (unit.Entry.Length > 0)
        {
            uint[] groups = reflection.EntryPoints.FirstOrDefault()?.ThreadGroupSize ?? [];
            return new ShaderModule([new CompiledStage(unit.Stage, unit.Entry, spirv, groups)], ParamsOf(reflection));
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

        return new ShaderModule([.. stages], ParamsOf(reflection));
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

            var element = parameter.Type.ConstantBuffer!.ElementType;
            if (element.Kind != SlangTypeKind.Struct) continue;

            var members = new List<UniformBlockLayout.Member>();
            foreach (var field in element.Struct!.Fields)
            {
                var binding = field.Bindings.FirstOrDefault(b => b.Kind == SlangParameterCategory.Uniform);
                if (binding is null) continue;

                int count = field.Type.Kind == SlangTypeKind.Array ? (int)field.Type.Array!.ElementCount : 0;
                int stride = count > 0 ? (int)field.Type.Array!.UniformStride : 0;
                int size = count > 0 ? stride : (int)binding.Size;
                members.Add(new UniformBlockLayout.Member(field.Name, TypeName(field.Type), (int)binding.Offset, size, count, stride));
            }

            return UniformBlockLayout.FromMembers(members);
        }

        return UniformBlockLayout.Empty;
    }

    private static string TypeName(SlangType type) => type.Kind switch
    {
        SlangTypeKind.Scalar => type.Scalar!.ScalarType.ToString().ToLowerInvariant(),
        SlangTypeKind.Vector => $"float{type.Vector!.ElementCount}",
        SlangTypeKind.Matrix => $"float{type.Matrix!.RowCount}x{type.Matrix.ColumnCount}",
        SlangTypeKind.Array => "array",
        _ => type.Kind.ToString().ToLowerInvariant()
    };
}
