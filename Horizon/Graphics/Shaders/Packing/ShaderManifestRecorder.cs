using Horizon.Content;
using Horizon.Logging;

namespace Horizon.Graphics;

/// <summary>
/// Writes down every program a run asks for, out of the cache, the pack or the compiler alike, when
/// <c>HORIZON_SHADER_MANIFEST</c> names a file. What's in the file already stays, so a run of every example and a
/// run of the game fill one manifest between them. A program whose sources changed since replaces its old entry.
/// It's an authoring thing for a desktop with the sources next to it, nothing writes a manifest unless asked.
/// </summary>
internal sealed class ShaderManifestRecorder
{
    private const string VARIABLE = "HORIZON_SHADER_MANIFEST";

    private readonly Lock gate = new();
    private readonly ShaderManifest manifest;
    private readonly HashSet<string> warned = new(StringComparer.Ordinal);

    /// <summary>The file it writes, a full path on the disk.</summary>
    public string File { get; }

    /// <summary>What's been recorded so far, with what was in the file before.</summary>
    public ShaderManifest Manifest => manifest;

    public ShaderManifestRecorder(string file)
    {
        File = Path.GetFullPath(file);
        manifest = new ShaderManifest();

        if (!System.IO.File.Exists(File)) return;
        try
        {
            manifest = ShaderManifest.Load(File);
        }
        catch (Exception e) when (e is FormatException or IOException)
        {
            // Not thrown away quietly, the run writes a new one and says the old one wasn't read
            Log.Warning($"[ShaderCompiler] The shader manifest '{File}' couldn't be read and is started again, {e.Message}");
        }
    }

    /// <summary>The recorder the environment asks for, null when it doesn't.</summary>
    public static ShaderManifestRecorder? FromEnvironment()
    {
        string? file = Environment.GetEnvironmentVariable(VARIABLE);
        if (string.IsNullOrWhiteSpace(file)) return null;

        var recorder = new ShaderManifestRecorder(file);
        Log.Info($"[ShaderCompiler] Every shader asked for is written to the manifest '{recorder.File}'.");
        return recorder;
    }

    /// <summary>Writes down one program, after it was preprocessed and before anything was looked up for it.</summary>
    public void Record(in ShaderDefinition definition, string name, ShaderLanguage language, ShaderPreprocessor preprocessor, string source, IContentFiles content)
    {
        try
        {
            var entry = EntryFor(definition, name, language, preprocessor, source, content, out string? unportable);
            if (entry is null)
            {
                lock (gate)
                {
                    if (warned.Add(unportable!))
                        Log.Warning($"[ShaderCompiler] {unportable} isn't under the content root, so the shader manifest can't name it and a pack won't have it.");
                }
                return;
            }

            lock (gate)
            {
                if (manifest.Add(entry)) manifest.Save(File);
            }
        }
        catch (Exception e)
        {
            // A manifest that can't be written mustn't stop the shader from loading, it says so instead
            lock (gate)
            {
                if (warned.Add(e.Message)) Log.Warning($"[ShaderCompiler] The shader manifest '{File}' wasn't written, {e.Message}");
            }
        }
    }

    /// <summary>The manifest entry of a program that was just preprocessed, null when one of its files has no portable path.</summary>
    internal static ShaderManifestEntry? EntryFor(in ShaderDefinition definition, string name, ShaderLanguage language, ShaderPreprocessor preprocessor, string source, IContentFiles content, out string? unportable)
    {
        unportable = null;
        var inputs = new List<ShaderInput>(preprocessor.Inputs.Count);
        foreach (var input in preprocessor.Inputs)
        {
            string? logical = ShaderPaths.Logical(content, input.Path);
            if (logical is null)
            {
                unportable = input.Path;
                return null;
            }

            inputs.Add(input with { Path = logical });
        }

        string hash = ShaderLines.Hash(source);
        if (definition.File is null)
        {
            string label = string.Join('_', name.Split(['\t', '\n', '\r']));
            return new ShaderManifestEntry(ShaderRequestKind.Inline, language, label.Length == 0 ? "inline" : label,
                preprocessor.BindlessCapacity, hash, ShaderLines.NormalizeLineEndings(definition.Source), inputs);
        }

        string? path = ShaderPaths.Logical(content, definition.File);
        if (path is null)
        {
            unportable = definition.File;
            return null;
        }

        return new ShaderManifestEntry(ShaderRequestKind.File, language, path, preprocessor.BindlessCapacity, hash, null, inputs);
    }
}
