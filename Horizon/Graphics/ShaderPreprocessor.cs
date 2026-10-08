using System.Text;

namespace Horizon.Graphics;

/// <summary>
/// Puts shader source through the one directive the engine handles itself, which is <c>#include</c>, and puts the
/// handful of macros every shader is written with in front of it.
/// <list type="bullet">
/// <item><c>#include "file.slang"</c> brings in a file next to the one being compiled (or next to the include that asks for it).</item>
/// <item><c>#include &lt;common/camera.slang&gt;</c> brings in a file from the root of the shaders (<see cref="Root"/>), from anywhere.</item>
/// </list>
/// Includes include in turn, and a file that is asked for twice in one program only comes in once, so a shader and
/// an include it uses can both ask for the camera block without doubling it up. Every <c>#include</c> is replaced
/// by a <c>#line</c> directive and the file, so a compiler error still points at the line of the file it is in.
/// Doing the includes here rather than leaving them to Slang is what lets the compiled program be kept on disk by a
/// hash of everything that went into it.
/// <para>
/// The macros say where things are bound, so a shader never spells out a descriptor set.
/// <c>BIND_UNIFORM(1) ConstantBuffer&lt;Params&gt; params</c>, <c>BIND_BUFFER(2) StructuredBuffer&lt;Light&gt; lights</c>,
/// <c>BIND_TEXTURE(0) Sampler2D uSource</c>, <c>BIND_IMAGE(0) RWTexture2D&lt;float4&gt; uOut</c>.
/// Set 0 holds the buffers (0 the camera, 1 the params of the draw, 2 and up storage), set 1 the textures (0 to 7),
/// the images (8 to 11) and the samplers on their own (12 to 19, the sampler of the texture unit of the same number).
/// Set 2 is the bindless table, <c>BINDLESS_TEXTURES(name)</c> declares it and an item's texture index goes straight in.
/// </para>
/// </summary>
public sealed class ShaderPreprocessor : IDisposable
{
    /// <summary>Where <c>#include &lt;...&gt;</c> looks, relative to the working directory of the game.</summary>
    public static string Root { get; set; } = "shaders";

    /// <summary>How many storage buffers a shader can have bound at once, on bindings 2 and up of set 0.</summary>
    public const int STORAGE_BINDINGS = 8;

    /// <summary>How many textures, on set 1.</summary>
    public const int TEXTURE_BINDINGS = 8;

    /// <summary>How many storage images, after the textures on set 1.</summary>
    public const int IMAGE_BINDINGS = 4;

    /// <summary>How many samplers on their own, after the images on set 1, each the sampler of the texture unit of the same number. For HLSL.</summary>
    public const int SAMPLER_BINDINGS = 8;

    private const string Prelude = """
        #define HORIZON_SLANG 1
        #define BIND_UNIFORM(n) [[vk::binding(n, 0)]]
        #define BIND_BUFFER(n) [[vk::binding(n, 0)]]
        #define BIND_TEXTURE(n) [[vk::binding(n, 1)]]
        #define BIND_IMAGE(n) [[vk::binding(8 + n, 1)]]
        #define BIND_SAMPLER(n) [[vk::binding(12 + n, 1)]]
        #define BINDLESS_TEXTURES(name) [[vk::binding(0, 2)]] Sampler2D name[4096]
        #define NO_TEXTURE 0xFFFFu
        """;

    private readonly Dictionary<string, string> fileSources = new();
    private readonly HashSet<string> included = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Scans a file for the directives and brings everything in.</summary>
    /// <exception cref="FileNotFoundException" />
    public string ProcessFile(in string file)
    {
        if (!File.Exists(file))
            throw new FileNotFoundException($"Input file {file} not found! Please supply a valid shader source file.");

        included.Clear();
        included.Add(Path.GetFullPath(file));
        return Process(file, File.ReadAllLines(file), top: true);
    }

    /// <summary>Scans source that didn't come out of a file. Includes are found from the root alone.</summary>
    public string ProcessSource(in string file, in IEnumerable<string> lines)
    {
        included.Clear();
        return Process(file, lines, top: true);
    }

    private string Process(string file, IEnumerable<string> lines, bool top)
    {
        var source = new StringBuilder();
        int lineNumber = 0;

        if (top)
        {
            source.AppendLine(Prelude);
            source.Append("#line 1\n");
        }

        foreach (string line in lines)
        {
            lineNumber++;
            string trimmed = line.TrimStart();

            if (trimmed.StartsWith("#include", StringComparison.Ordinal))
            {
                string? path = ResolveInclude(file, trimmed);
                if (path is null)
                    throw new FileNotFoundException($"The include {trimmed.Trim()} of {file} wasn't found.");

                // Once per program is plenty, see the summary
                if (!included.Add(Path.GetFullPath(path)))
                    continue;

                source.Append("#line 1\n");
                source.Append(Process(path, ReadLines(path), top: false));
                source.Append("#line ").Append(lineNumber + 1).Append('\n');
                continue;
            }

            source.AppendLine(line);
        }

        return source.ToString();
    }

    private string[] ReadLines(string path)
    {
        if (!fileSources.TryGetValue(path, out string? text))
            fileSources[path] = text = File.ReadAllText(path);

        return text.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
    }

    private static string? ResolveInclude(string file, string directive)
    {
        int open = directive.IndexOfAny(['"', '<']);
        if (open < 0) return null;

        char close = directive[open] == '<' ? '>' : '"';
        int end = directive.IndexOf(close, open + 1);
        if (end < 0) return null;

        string argument = directive[(open + 1)..end];

        if (close == '>')
        {
            string fromRoot = Path.Combine(Root, argument);
            return File.Exists(fromRoot) ? fromRoot : null;
        }

        string directory = Path.GetDirectoryName(file) ?? string.Empty;
        string beside = Path.Combine(directory, argument);
        if (File.Exists(beside)) return beside;

        // What isn't beside the file may still be at the root, for a snippet that is used from anywhere
        string rooted = Path.Combine(Root, argument);
        return File.Exists(rooted) ? rooted : null;
    }

    public void Dispose()
    {
        fileSources.Clear();
        included.Clear();
    }
}
