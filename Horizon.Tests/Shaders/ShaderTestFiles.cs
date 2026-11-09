using System.Text;

using Horizon.Content;
using Horizon.Graphics;
using Horizon.Tests.Content;

namespace Horizon.Tests.Shaders;

/// <summary>Small shaders and the bits around them the shader pack tests share. Nothing in here needs Slang.</summary>
internal static class ShaderTestFiles
{
    public const uint CAPACITY = 4096;

    /// <summary>A compute shader with a Params block and a work group size, the most reflection a module carries.</summary>
    public const string COMPUTE = """
        #include <common/tint.slang>
        BIND_BUFFER(2) RWStructuredBuffer<float4> output;

        [shader("compute")]
        [numthreads(8, 4, 1)]
        void csMain(uint3 id : SV_DispatchThreadID)
        {
            output[id.x] = params.tint * params.scale;
        }
        """;

    public const string TINT = """
        struct Params { float4 tint; float scale; };
        BIND_UNIFORM(1) ConstantBuffer<Params> params;
        """;

    /// <summary>Two stages in one Slang file, one module between them.</summary>
    public const string VERTEX_AND_FRAGMENT = """
        struct VsOut { float4 position : SV_Position; };

        [shader("vertex")]
        VsOut vsMain(uint id : SV_VertexID)
        {
            VsOut o;
            o.position = float4(0, 0, 0, 1);
            return o;
        }

        [shader("fragment")]
        float4 fsMain() : SV_Target
        {
            return float4(1, 0, 0, 1);
        }
        """;

    /// <summary>HLSL, a module an entry point.</summary>
    public const string HLSL = """
        float4 vsMain(uint id : SV_VertexID) : SV_Position { return float4(0, 0, 0, 1); }
        float4 psMain() : SV_Target { return float4(0, 1, 0, 1); }
        """;

    /// <summary>The usual content, the compute shader and its include, with \n or \r\n line endings.</summary>
    public static MemoryContent Sources(string newline = "\n", params (string Path, string Text)[] more)
    {
        var files = new Dictionary<string, byte[]>
        {
            ["shaders/test/compute.slang"] = Text(COMPUTE, newline),
            ["shaders/common/tint.slang"] = Text(TINT, newline),
            ["shaders/test/draw.slang"] = Text(VERTEX_AND_FRAGMENT, newline),
            ["shaders/test/legacy.hlsl"] = Text(HLSL, newline)
        };
        foreach (var (path, text) in more) files[path] = Text(text, newline);
        return new MemoryContent(files);
    }

    public static byte[] Text(string text, string newline = "\n") =>
        Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n").Replace("\n", newline));

    /// <summary>Content with a pack file in its pack folder next to whatever else.</summary>
    public static MemoryContent WithPack(MemoryContent sources, byte[] pack, string name = "test.hzshaders")
    {
        var files = new Dictionary<string, byte[]>
        {
            ["shaders/test/compute.slang"] = sources.ReadAllBytes("shaders/test/compute.slang"),
            ["shaders/common/tint.slang"] = sources.ReadAllBytes("shaders/common/tint.slang"),
            ["shaders/test/draw.slang"] = sources.ReadAllBytes("shaders/test/draw.slang"),
            ["shaders/test/legacy.hlsl"] = sources.ReadAllBytes("shaders/test/legacy.hlsl"),
            [$"{ShaderPack.FOLDER}/{name}"] = pack
        };
        return new MemoryContent(files);
    }

    public static byte[] ReadAllBytes(this IContentFiles content, string path) => ContentFiles.ReadAllBytes(content, path);

    /// <summary>The keys a file makes, preprocessed the way the compiler would at a capacity.</summary>
    public static ShaderUnit[] UnitsOf(IContentFiles content, string path, uint capacity = CAPACITY)
    {
        using var preprocessor = new ShaderPreprocessor(content, capacity);
        return ShaderCompiler.UnitsOf(preprocessor.ProcessFile(path), ShaderCompiler.LanguageOf(path));
    }

    /// <summary>Twenty bytes that start the way SPIR-V does and are marked so two of them can be told apart.</summary>
    public static byte[] FakeSpirv(byte mark)
    {
        byte[] spirv = new byte[20];
        BitConverter.GetBytes(0x07230203u).CopyTo(spirv, 0);
        spirv[19] = mark;
        return spirv;
    }

    public static ShaderPackEntry FakeEntry(string key, string origin, byte mark = 1, ShaderStage stage = ShaderStage.Compute, string entry = "csMain", uint x = 8, uint y = 4, uint z = 1) => new(
        key, origin, FakeSpirv(mark),
        [new ShaderPackStage(stage, entry, x, y, z)],
        [new UniformBlockLayout.Member("tint", "float4", 0, 16, 0, 0), new UniformBlockLayout.Member("scale", "float32", 16, 4, 0, 0)]);

    public static byte[] Pack(IEnumerable<ShaderPackEntry> entries, uint[]? capacities = null, string? fingerprint = null, string? profile = null) =>
        ShaderPackWriter.Write(fingerprint ?? ShaderCompiler.CompilerFingerprint, profile ?? ShaderCompiler.TARGET_PROFILE, "test.manifest", capacities ?? [CAPACITY], entries);

    /// <summary>Options that read only what a test gives them, never the environment.</summary>
    public static ShaderOptions Options(IContentFiles content, ShaderSourcing sourcing, string? cache = null, ShaderManifestRecorder? recorder = null, ShaderPackSet? packs = null, uint capacity = CAPACITY) => new()
    {
        Sourcing = sourcing,
        Content = content,
        CacheFolder = cache,
        Recorder = recorder,
        Packs = packs,
        BindlessCapacity = capacity
    };

    public static ShaderDescription FileProgram(string path) => new() { Definitions = [new ShaderDefinition(ShaderStage.Compute, path)] };

    public static ShaderDescription InlineProgram(string source) => new() { Definitions = [new ShaderDefinition(ShaderStage.Fragment, null, source)] };

    /// <summary>A folder of its own under the test's temp folder, gone after.</summary>
    public sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "horizon-shader-tests", Guid.NewGuid().ToString("N"));
        public TempFolder() => Directory.CreateDirectory(Path);
        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
        }
    }
}
