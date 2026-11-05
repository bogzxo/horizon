using Horizon.Content;

namespace Horizon.Graphics;

/// <summary>A pack that can't be built, with every reason in the message.</summary>
public sealed class ShaderPackBuildException(IReadOnlyList<string> problems)
    : Exception(string.Join('\n', problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>
/// Makes a <see cref="ShaderPack"/> out of a manifest and the sources in content, and checks one. Everything is
/// compiled fresh, never out of a cache, and nothing is built from a manifest that's behind its sources. A file or an
/// include edited since the run that recorded it is an error naming that file, until the run is done again or the
/// manifest is refreshed on purpose.
/// </summary>
public static class ShaderPackBuilder
{
    /// <summary>Where the manifest no longer matches the sources, a sentence for each program that's behind. Empty when it's current.</summary>
    public static IReadOnlyList<string> Stale(ShaderManifest manifest, IContentFiles content)
    {
        var problems = new List<string>();
        foreach (var entry in manifest.Entries)
        {
            string? problem = StaleProblem(entry, content);
            if (problem is not null) problems.Add(problem);
        }

        return problems;
    }

    private static string? StaleProblem(ShaderManifestEntry entry, IContentFiles content)
    {
        string source;
        IReadOnlyList<ShaderInput> inputs;
        try
        {
            source = Preprocess(entry, content, entry.BindlessCapacity, out inputs);
        }
        catch (Exception e)
        {
            return $"{entry.Label} can't be read any more, {e.Message}";
        }

        var now = inputs.ToDictionary(input => input.Path, StringComparer.Ordinal);
        foreach (var recorded in entry.Inputs)
        {
            if (!now.TryGetValue(recorded.Path, out var current))
                return $"{entry.Label} no longer reads {recorded.Path}, the manifest is behind it.";
            if (current.Hash != recorded.Hash)
                return $"{entry.Label} is behind {recorded.Path}, which changed since the manifest was recorded.";
        }

        var was = entry.Inputs.Select(input => input.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            if (!was.Contains(input.Path))
                return $"{entry.Label} now reads {input.Path}, which the manifest doesn't have.";
        }

        if (ShaderLines.Hash(source) != entry.Hash)
            return $"{entry.Label} preprocesses to something other than what was recorded.";

        return null;
    }

    /// <summary>
    /// The manifest with every entry's inputs and hash taken from the sources as they are now, for when they were
    /// changed on purpose and nobody wants to run everything again. Says which entries moved in <paramref name="changed"/>.
    /// </summary>
    public static ShaderManifest Refresh(ShaderManifest manifest, IContentFiles content, out IReadOnlyList<string> changed)
    {
        var refreshed = new ShaderManifest();
        var moved = new List<string>();
        var problems = new List<string>();

        foreach (var entry in manifest.Entries)
        {
            try
            {
                string source = Preprocess(entry, content, entry.BindlessCapacity, out var inputs);
                var now = entry with { Hash = ShaderLines.Hash(source), Inputs = inputs };
                if (!now.Equals(entry)) moved.Add(entry.Label);
                refreshed.Add(now);
            }
            catch (Exception e)
            {
                problems.Add($"{entry.Label} can't be read any more, {e.Message}");
            }
        }

        if (problems.Count > 0) throw new ShaderPackBuildException(problems);
        changed = moved;
        return refreshed;
    }

    /// <summary>
    /// Compiles every program of a manifest, for every bindless capacity asked for (the manifest's own when null),
    /// into the bytes of a pack. Throws <see cref="ShaderPackBuildException"/> with every problem at once.
    /// </summary>
    public static byte[] Build(ShaderManifest manifest, IContentFiles content, string manifestName, IReadOnlyCollection<uint>? capacities = null)
    {
        var stale = Stale(manifest, content);
        if (stale.Count > 0) throw new ShaderPackBuildException([.. stale, "Run the game again with HORIZON_SHADER_MANIFEST, or refresh the manifest if the change was meant."]);

        uint[] building = CapacitiesFor(manifest, capacities);
        var made = Compile(manifest, content, building, out var problems);
        if (problems.Count > 0) throw new ShaderPackBuildException(problems);

        return ShaderPackWriter.Write(ShaderCompiler.CompilerFingerprint, ShaderCompiler.TARGET_PROFILE, manifestName, building, made.Values);
    }

    /// <summary>
    /// Checks a pack against a fresh compile of its manifest, byte for byte with the reflection. Every module the
    /// manifest wants has to be there and the same, and nothing else may be. Empty when the pack is right.
    /// </summary>
    public static IReadOnlyList<string> Verify(ShaderPack pack, ShaderManifest manifest, IContentFiles content)
    {
        var problems = new List<string>();
        if (pack.Fingerprint != ShaderCompiler.CompilerFingerprint)
            problems.Add($"{pack.Name} was built with {pack.Fingerprint} and this engine is {ShaderCompiler.CompilerFingerprint}.");
        if (pack.TargetProfile != ShaderCompiler.TARGET_PROFILE)
            problems.Add($"{pack.Name} holds {pack.TargetProfile} and this engine makes {ShaderCompiler.TARGET_PROFILE}.");

        problems.AddRange(Stale(manifest, content));
        if (problems.Count > 0) return problems;

        var fresh = Compile(manifest, content, [.. pack.BindlessCapacities], out var compileProblems);
        problems.AddRange(compileProblems);

        foreach (var (key, entry) in fresh)
        {
            if (!pack.TryGet(key, out var packed))
                problems.Add($"{entry.Origin} (key {key}) isn't in {pack.Name}.");
            else if (entry.DifferenceFrom(packed) is { Length: > 0 } difference)
                problems.Add($"{entry.Origin} (key {key}) in {pack.Name} isn't what it compiles to now, {difference}.");
        }

        foreach (var packed in pack.Entries)
        {
            if (!fresh.ContainsKey(packed.Key) && compileProblems.Count == 0)
                problems.Add($"{pack.Name} has {packed.Origin} (key {packed.Key}), which the manifest doesn't ask for.");
        }

        return problems;
    }

    private static uint[] CapacitiesFor(ShaderManifest manifest, IReadOnlyCollection<uint>? capacities)
    {
        IEnumerable<uint> asked = capacities is { Count: > 0 } ? capacities : manifest.Entries.Select(entry => entry.BindlessCapacity);
        uint[] building = [.. asked.Distinct().Order()];
        if (building.Length == 0) building = [Vulkan.BindlessTextures.MAX_CAPACITY];
        if (building.Any(capacity => capacity == 0)) throw new ShaderPackBuildException(["A bindless capacity of 0 isn't one."]);
        return building;
    }

    /// <summary>Helper method to compile every program for every capacity once a key, the origin of each being the first file that asked by name.</summary>
    private static SortedDictionary<string, ShaderPackEntry> Compile(ShaderManifest manifest, IContentFiles content, uint[] capacities, out List<string> problems)
    {
        var made = new SortedDictionary<string, ShaderPackEntry>(StringComparer.Ordinal);
        problems = [];

        foreach (var entry in manifest.Entries)
        {
            foreach (uint capacity in capacities)
            {
                string source;
                try
                {
                    source = Preprocess(entry, content, capacity, out _);
                }
                catch (Exception e)
                {
                    problems.Add($"{entry.Label}, {e.Message}");
                    continue;
                }

                var units = ShaderCompiler.UnitsOf(source, entry.Language);
                if (units.Length == 0) problems.Add($"{entry.Label} has none of vsMain, fsMain, psMain, csMain or gsMain in it.");

                foreach (var unit in units)
                {
                    if (made.TryGetValue(unit.Key, out var already))
                    {
                        if (string.CompareOrdinal(entry.Path, already.Origin) < 0)
                            made[unit.Key] = new ShaderPackEntry(already.Key, entry.Path, already.Spirv, already.Stages, already.Params);
                        continue;
                    }

                    try
                    {
                        var module = ShaderCompiler.CompileFresh(unit, entry.Path);
                        if (module.Stages.Length == 0)
                            problems.Add($"{unit.Label(entry.Label)} has no entry point the engine can use.");
                        else
                            made[unit.Key] = ShaderPackEntry.FromModule(unit.Key, entry.Path, module);
                    }
                    catch (Exception e)
                    {
                        problems.Add($"{unit.Label(entry.Label)} for a bindless table of {capacity} didn't compile\n{e.Message.Trim()}");
                    }
                }
            }
        }

        return made;
    }

    /// <summary>Preprocesses an entry the way the run that recorded it did, giving back what it read by content path.</summary>
    private static string Preprocess(ShaderManifestEntry entry, IContentFiles content, uint capacity, out IReadOnlyList<ShaderInput> inputs)
    {
        using var preprocessor = new ShaderPreprocessor(content, capacity);
        string source = entry.Kind == ShaderRequestKind.File
            ? preprocessor.ProcessFile(entry.Path)
            : preprocessor.ProcessSource(string.Empty, ShaderLines.Split(entry.Source ?? string.Empty));

        var logical = new List<ShaderInput>(preprocessor.Inputs.Count);
        foreach (var input in preprocessor.Inputs)
            logical.Add(input with { Path = ShaderPaths.Logical(content, input.Path) ?? throw new IOException($"{input.Path} isn't under the content root.") });

        inputs = logical;
        return source;
    }
}
