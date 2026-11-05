using System.Runtime.CompilerServices;
using System.Text;

using Horizon.Content;
using Horizon.Logging;

namespace Horizon.Graphics;

/// <summary>
/// Every pack in one content source, read once, and what was wrong with the ones that couldn't be used. Kept by the
/// source itself, so a source installed later never gets an earlier source's packs, even under the same file names.
/// </summary>
internal sealed class ShaderPackSet
{
    private static readonly ConditionalWeakTable<IContentFiles, ShaderPackSet> bySource = new();

    public IReadOnlyList<ShaderPack> Packs { get; }

    /// <summary>Packs that were found and not used, each a sentence that says which and why.</summary>
    public IReadOnlyList<string> Problems { get; }

    internal ShaderPackSet(IReadOnlyList<ShaderPack> packs, IReadOnlyList<string> problems)
    {
        Packs = packs;
        Problems = problems;
    }

    /// <summary>The packs of a content source, read the first time anybody asks.</summary>
    public static ShaderPackSet For(IContentFiles content) => bySource.GetValue(content, Load);

    /// <summary>
    /// Reads every pack in <see cref="ShaderPack.FOLDER"/> of a source, in name order. A pack made by another compiler
    /// setup or for another SPIR-V profile is left out with a problem saying so, not half used.
    /// </summary>
    public static ShaderPackSet Load(IContentFiles content) => Load(content, ShaderCompiler.CompilerFingerprint, ShaderCompiler.TARGET_PROFILE);

    internal static ShaderPackSet Load(IContentFiles content, string fingerprint, string profile)
    {
        var packs = new List<ShaderPack>();
        var problems = new List<string>();

        string[] files;
        try
        {
            files = content.DirectoryExists(ShaderPack.FOLDER)
                ? [.. content.EnumerateFiles(ShaderPack.FOLDER, "*" + ShaderPack.EXTENSION).Order(StringComparer.Ordinal)]
                : [];
        }
        catch (Exception e)
        {
            files = [];
            problems.Add($"The folder {ShaderPack.FOLDER} couldn't be listed, {e.Message}");
        }

        foreach (string file in files)
        {
            try
            {
                ShaderPack pack = ShaderPack.Load(content, file);
                if (pack.Fingerprint != fingerprint)
                    problems.Add($"{file} was built by another compiler setup ({pack.Fingerprint}) and this engine wants {fingerprint}, so it's left out.");
                else if (pack.TargetProfile != profile)
                    problems.Add($"{file} holds {pack.TargetProfile} modules and this engine wants {profile}, so it's left out.");
                else
                    packs.Add(pack);
            }
            catch (ShaderPackException e)
            {
                problems.Add(e.Message);
            }
        }

        foreach (string problem in problems) Log.Warning($"[ShaderCompiler] {problem}");
        if (packs.Count > 0) Log.Info($"[ShaderCompiler] Shader packs {string.Join(", ", packs.Select(pack => $"{pack.Name} ({pack.Entries.Count})"))}.");
        return new ShaderPackSet(packs, problems);
    }

    /// <summary>The module of a key from the first pack that has it.</summary>
    public bool TryFind(string key, out ShaderPackEntry entry)
    {
        foreach (var pack in Packs)
        {
            if (pack.TryGet(key, out entry)) return true;
        }

        entry = null!;
        return false;
    }

    /// <summary>What to say when a module isn't in any pack, naming the program, the key, the packs and how to fix it.</summary>
    public string Missing(string program, string key, uint capacity)
    {
        var said = new StringBuilder();
        said.Append(program).Append(" isn't in the shader pack (key ").Append(key).Append(", bindless table of ").Append(capacity).Append(").");

        if (Packs.Count == 0)
            said.Append(" No usable shader pack was found in ").Append(ShaderPack.FOLDER).Append('.');
        else
        {
            said.Append(" Looked in ").Append(string.Join(", ", Packs.Select(pack => $"{pack.Name} (manifest {pack.Manifest})"))).Append('.');
            if (!Packs.Any(pack => pack.BindlessCapacities.Contains(capacity)))
                said.Append(" The packs were built for bindless tables of ")
                    .Append(string.Join(", ", Packs.SelectMany(pack => pack.BindlessCapacities).Distinct().Order()))
                    .Append(" and this device has ").Append(capacity).Append(", build with --capacity ").Append(capacity).Append(" as well.");
        }

        foreach (string problem in Problems) said.Append(' ').Append(problem);
        said.Append(" Record it with HORIZON_SHADER_MANIFEST and build the pack again with Horizon.Shaders.Build.");
        return said.ToString();
    }
}
