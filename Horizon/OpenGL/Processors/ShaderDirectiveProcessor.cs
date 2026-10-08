using System.Text;

namespace Horizon.OpenGL.Processors;

/// <summary>
/// Puts shader source through the few directives GLSL hasn't got: <c>#include</c>, and the <c>#import</c> shorthands
/// for snippets that come with the engine.
/// <list type="bullet">
/// <item><c>#include "file.glsl"</c> brings in a file next to the one being compiled (or next to the include that asks for it).</item>
/// <item><c>#include &lt;common/camera.glsl&gt;</c> brings in a file from the root of the shaders (<see cref="Root"/>), from anywhere.</item>
/// </list>
/// Includes include in turn, and a file that is asked for twice in one program only comes in once, so a shader and
/// an include it uses can both ask for the camera block without doubling it up. Every <c>#include</c> is replaced
/// by a <c>#line</c> directive and the file, so a compiler error still points at the line of the file it is in.
/// </summary>
public class ShaderDirectiveProcessor : IDisposable
{
    /// <summary>Where <c>#include &lt;...&gt;</c> looks, relative to the working directory of the game.</summary>
    public static string Root { get; set; } = "shaders";

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

            if (trimmed.StartsWith("#import", StringComparison.Ordinal))
            {
                source.AppendLine(Import(Argument(trimmed)));
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

    private static string Argument(string directive)
    {
        int q0 = directive.IndexOf('"') + 1;
        int q1 = directive.LastIndexOf('"');
        return q1 > q0 ? directive[q0..q1] : string.Empty;
    }

    /// <summary>
    /// The snippets that come with the engine, by name.
    /// </summary>
    private static string Import(string what) => what switch
    {
        "fastRandom" => """
            float rand(vec2 co){
                return fract(sin(dot(co, vec2(12.9898, 78.233))) * 43758.5453);
            }

            float rand(float n){return fract(sin(n) * 43758.5453123);}

            float noise(float p){
                float fl = floor(p);
                float fc = fract(p);
                return mix(rand(fl), rand(fl + 1.0), fc);
            }

            float noise(vec2 n) {
                const vec2 d = vec2(0.0, 1.0);
                vec2 b = floor(n), f = smoothstep(vec2(0.0), vec2(1.0), fract(n));
                return mix(mix(rand(b), rand(b + d.yx), f.x), mix(rand(b + d.xy), rand(b + d.yy), f.x), f.y);
            }
            """,
        _ => string.Empty
    };

    public void Dispose()
    {
        fileSources.Clear();
        included.Clear();
        GC.SuppressFinalize(this);
    }
}
