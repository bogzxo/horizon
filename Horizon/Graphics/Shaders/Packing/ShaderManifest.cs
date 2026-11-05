using System.Text;

using Horizon.Content;

namespace Horizon.Graphics;

/// <summary>Whether a manifest entry is a file in content or source a program made up in code.</summary>
public enum ShaderRequestKind
{
    File,
    Inline
}

/// <summary>
/// One program a run asked for. A file by its content path, or inline source carried whole. The hash is of the
/// preprocessed source at the bindless capacity the run had, and the inputs are every file it read with a hash of
/// each, so a builder can tell a manifest that's behind the sources and say which file moved.
/// </summary>
public sealed record ShaderManifestEntry(
    ShaderRequestKind Kind,
    ShaderLanguage Language,
    string Path,
    uint BindlessCapacity,
    string Hash,
    string? Source,
    IReadOnlyList<ShaderInput> Inputs)
{
    /// <summary>What makes two entries the same request. An inline program is told apart by its source, its name only labels it.</summary>
    internal string Identity => Kind == ShaderRequestKind.File
        ? $"file|{Language}|{Path}|{BindlessCapacity}"
        : $"inline|{Language}|{ShaderLines.Hash(Source ?? string.Empty)}|{BindlessCapacity}";

    /// <summary>How an error names it.</summary>
    public string Label => Kind == ShaderRequestKind.File ? Path : $"inline program {Path}";

    public bool Equals(ShaderManifestEntry? other) => other is not null && Kind == other.Kind && Language == other.Language
        && Path == other.Path && BindlessCapacity == other.BindlessCapacity && Hash == other.Hash && Source == other.Source
        && Inputs.SequenceEqual(other.Inputs);

    public override int GetHashCode() => HashCode.Combine(Kind, Language, Path, BindlessCapacity, Hash);
}

/// <summary>
/// Every program some runs asked for, which is what <c>Horizon.Shaders.Build</c> compiles into a pack. A text file,
/// a line a program and a line an input under it, tab separated, sorted, with \n endings and nothing about the
/// machine or the time, so it can be checked in and diffed.
/// <code>
/// horizon-shader-manifest	1
/// file	slang	4096	&lt;hash&gt;	shaders/post/blur.slang
/// input	shaders/post/blur.slang	&lt;hash&gt;
/// inline	slang	4096	&lt;hash&gt;	name	&lt;base64 source&gt;
/// </code>
/// </summary>
public sealed class ShaderManifest
{
    private const string HEADER = "horizon-shader-manifest";
    internal const int FORMAT = 1;

    private readonly Dictionary<string, ShaderManifestEntry> entries = new(StringComparer.Ordinal);

    /// <summary>Every entry in the order they're written in.</summary>
    public IReadOnlyList<ShaderManifestEntry> Entries => [.. entries.Values.OrderBy(Order, StringComparer.Ordinal)];

    /// <summary>
    /// Puts an entry in, replacing one for the same request. True when that changed anything, so a recorder only
    /// writes the file when there's something new.
    /// </summary>
    public bool Add(ShaderManifestEntry entry)
    {
        if (entries.TryGetValue(entry.Identity, out var already) && already.Equals(entry)) return false;
        entries[entry.Identity] = entry;
        return true;
    }

    private static string Order(ShaderManifestEntry entry) =>
        $"{(int)entry.Kind}\t{entry.Path}\t{entry.Language}\t{entry.BindlessCapacity:D10}\t{entry.Identity}";

    public string Write()
    {
        var text = new StringBuilder();
        text.Append(HEADER).Append('\t').Append(FORMAT).Append('\n');
        foreach (var entry in Entries)
        {
            text.Append(entry.Kind == ShaderRequestKind.File ? "file" : "inline").Append('\t')
                .Append(entry.Language == ShaderLanguage.Hlsl ? "hlsl" : "slang").Append('\t')
                .Append(entry.BindlessCapacity).Append('\t')
                .Append(entry.Hash).Append('\t')
                .Append(entry.Path);
            if (entry.Kind == ShaderRequestKind.Inline)
                text.Append('\t').Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.Source ?? string.Empty)));
            text.Append('\n');

            foreach (var input in entry.Inputs)
                text.Append("input\t").Append(input.Path).Append('\t').Append(input.Hash).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>Reads a manifest. Anything off is a <see cref="FormatException"/> that names the file and the line.</summary>
    public static ShaderManifest Parse(string text, string name)
    {
        var manifest = new ShaderManifest();
        string[] lines = ShaderLines.Split(text);
        if (lines.Length == 0 || lines[0].Split('\t') is not [HEADER, var format])
            throw new FormatException($"{name} isn't a shader manifest, its first line should be '{HEADER}\t{FORMAT}'.");
        if (format != FORMAT.ToString())
            throw new FormatException($"{name} is manifest format {format} and this engine reads {FORMAT}. Record it again.");

        (ShaderRequestKind Kind, ShaderLanguage Language, uint Capacity, string Hash, string Path, string? Source)? open = null;
        var inputs = new List<ShaderInput>();

        void Close()
        {
            if (open is not { } o) return;
            manifest.Add(new ShaderManifestEntry(o.Kind, o.Language, o.Path, o.Capacity, o.Hash, o.Source, [.. inputs]));
            inputs.Clear();
            open = null;
        }

        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length == 0) continue;
            string[] parts = line.Split('\t');
            FormatException Bad(string why) => new($"{name} line {i + 1} {why}.");

            switch (parts[0])
            {
                case "file" or "inline":
                {
                    Close();
                    bool inline = parts[0] == "inline";
                    if (parts.Length != (inline ? 6 : 5)) throw Bad($"should have {(inline ? 6 : 5)} fields and has {parts.Length}");

                    ShaderLanguage language = parts[1] switch
                    {
                        "slang" => ShaderLanguage.Slang,
                        "hlsl" => ShaderLanguage.Hlsl,
                        _ => throw Bad($"has a language '{parts[1]}'")
                    };
                    if (!uint.TryParse(parts[2], out uint capacity) || capacity == 0) throw Bad($"has a bindless capacity '{parts[2]}'");
                    if (!IsHash(parts[3])) throw Bad("has a hash that isn't 64 hex digits");

                    string? source = null;
                    if (inline)
                    {
                        try { source = Encoding.UTF8.GetString(Convert.FromBase64String(parts[5])); }
                        catch (FormatException) { throw Bad("has inline source that isn't base64"); }
                    }
                    else if (ShaderPaths.Problem(parts[4]) is { } problem) throw Bad(problem);

                    open = (inline ? ShaderRequestKind.Inline : ShaderRequestKind.File, language, capacity, parts[3], parts[4], source);
                    break;
                }
                case "input":
                    if (open is null) throw Bad("is an input before any program");
                    if (parts.Length != 3) throw Bad($"should have 3 fields and has {parts.Length}");
                    if (ShaderPaths.Problem(parts[1]) is { } inputProblem) throw Bad(inputProblem);
                    if (!IsHash(parts[2])) throw Bad("has a hash that isn't 64 hex digits");
                    inputs.Add(new ShaderInput(parts[1], parts[2]));
                    break;
                default:
                    throw Bad($"starts with '{parts[0]}', which is none of file, inline or input");
            }
        }

        Close();
        return manifest;
    }

    /// <summary>Reads a manifest file off the disk, where the tools keep them.</summary>
    public static ShaderManifest Load(string file) => Parse(File.ReadAllText(file), Path.GetFileName(file));

    /// <summary>Writes the manifest next to where it goes and moves it there whole, so a run that dies halfway never leaves half of one.</summary>
    public void Save(string file)
    {
        string? folder = Path.GetDirectoryName(Path.GetFullPath(file));
        if (folder is not null) Directory.CreateDirectory(folder);

        string partial = file + "." + Environment.ProcessId + ".partial";
        File.WriteAllText(partial, Write(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(partial, file, overwrite: true);
    }

    private static bool IsHash(string text) => text.Length == 64 && text.All(char.IsAsciiHexDigitUpper);
}

/// <summary>Content paths as a manifest writes them, forward slashes and relative to the content root, the same on every machine.</summary>
public static class ShaderPaths
{
    /// <summary>
    /// The portable path of a file a content source read, or null when there isn't one (a file outside the content
    /// root, which no other machine would have).
    /// </summary>
    public static string? Logical(IContentFiles content, string path)
    {
        string resolved;
        try
        {
            resolved = content.ResolvePath(path);
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (System.IO.Path.IsPathRooted(resolved))
        {
            // A disk source gives full paths back, the part under its root is the content path
            if (content is not DiskContent disk) return Normalise(path);
            string relative = System.IO.Path.GetRelativePath(disk.Root, resolved);
            if (System.IO.Path.IsPathRooted(relative)) return null;
            resolved = relative;
        }

        return Normalise(resolved);
    }

    private static string? Normalise(string path)
    {
        if (System.IO.Path.IsPathRooted(path)) return null;
        var parts = new List<string>();
        foreach (string part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) return null;
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part);
        }

        string logical = string.Join('/', parts);
        return logical.Length == 0 || Problem(logical) is not null ? null : logical;
    }

    /// <summary>What's wrong with a path for a manifest, null when nothing is.</summary>
    public static string? Problem(string path)
    {
        if (path.Length == 0) return "has an empty path";
        if (path.IndexOfAny(['\t', '\n', '\r', '\\', ':']) >= 0) return $"has a path '{path}' with a tab, line break, backslash or colon in it";
        if (path.StartsWith('/')) return $"has a path '{path}' that isn't relative";
        if (path.Split('/').Any(part => part is "" or "." or "..")) return $"has a path '{path}' that isn't plain";
        return null;
    }
}
