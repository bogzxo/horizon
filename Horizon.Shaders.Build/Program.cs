using Horizon.Content;
using Horizon.Graphics;

namespace Horizon.Shaders.Build;

/// <summary>
/// The command line of the pack builder. Every command takes the manifest a run wrote (HORIZON_SHADER_MANIFEST) and the
/// folder its content paths are under, which is the bin folder of the game that ran, with shaders/ in it.
/// <code>
/// Horizon.Shaders.Build build   --manifest m.manifest --content bin/Debug/net11.0 --out horizon.hzshaders [--capacity 4096]...
/// Horizon.Shaders.Build verify  --manifest m.manifest --content bin/Debug/net11.0 --pack horizon.hzshaders
/// Horizon.Shaders.Build check   --manifest m.manifest --content bin/Debug/net11.0
/// Horizon.Shaders.Build refresh --manifest m.manifest --content bin/Debug/net11.0 [--out other.manifest]
/// Horizon.Shaders.Build info    --pack horizon.hzshaders
/// </code>
/// It exits 0 when it did it, 1 when the manifest or the pack is wrong, 2 when it was asked wrong.
/// </summary>
internal static class Program
{
    private const string USAGE = """
        Horizon.Shaders.Build makes and checks shader packs.

          build   --manifest <file> --content <dir> --out <pack> [--capacity <n>]...
                  Compiles every program of the manifest fresh into a pack. Fails on a manifest that's behind its
                  sources. Capacities are the bindless table sizes to build for, the manifest's own when none.
          verify  --manifest <file> --content <dir> --pack <pack>
                  Compiles the manifest fresh and compares every module and its reflection with the pack.
          check   --manifest <file> --content <dir>
                  Says whether the manifest is behind the sources, and which file moved.
          refresh --manifest <file> --content <dir> [--out <file>]
                  Takes the sources as they are now into the manifest, for a change that was meant.
          info    --pack <pack>
                  What a pack holds.
        """;

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(USAGE);
            return args.Length == 0 ? 2 : 0;
        }

        Arguments options;
        try
        {
            options = Arguments.Parse(args[1..]);
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine(USAGE);
            return 2;
        }

        try
        {
            return args[0] switch
            {
                "build" => Build(options),
                "verify" => Verify(options),
                "check" => Check(options),
                "refresh" => Refresh(options),
                "info" => Info(options),
                _ => Usage($"There's no command '{args[0]}'.")
            };
        }
        catch (ArgumentException e)
        {
            return Usage(e.Message);
        }
        catch (ShaderPackBuildException e)
        {
            foreach (string problem in e.Problems) Console.Error.WriteLine(problem);
            return 1;
        }
        catch (Exception e) when (e is ShaderPackException or FormatException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    private static int Usage(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine(USAGE);
        return 2;
    }

    private static int Build(Arguments options)
    {
        string manifestFile = options.Required("manifest");
        string output = options.Required("out");
        var manifest = ShaderManifest.Load(manifestFile);
        var content = options.Content();

        byte[] pack = ShaderPackBuilder.Build(manifest, content, Path.GetFileName(manifestFile), options.Capacities());

        // Next to where it goes and moved there whole, a pack is never half written
        string full = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string partial = full + "." + Environment.ProcessId + ".partial";
        File.WriteAllBytes(partial, pack);
        File.Move(partial, full, overwrite: true);

        var made = ShaderPack.Read(new MemoryStream(pack), output);
        Console.WriteLine($"{output} has {made.Entries.Count} modules for {manifest.Entries.Count} programs, bindless tables of {string.Join(", ", made.BindlessCapacities)}, {pack.Length} bytes, SHA-256 {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pack))}.");
        return 0;
    }

    private static int Verify(Arguments options)
    {
        string manifestFile = options.Required("manifest");
        string packFile = options.Required("pack");
        var manifest = ShaderManifest.Load(manifestFile);

        ShaderPack pack;
        using (var stream = File.OpenRead(packFile)) pack = ShaderPack.Read(stream, packFile);

        var problems = ShaderPackBuilder.Verify(pack, manifest, options.Content());
        foreach (string problem in problems) Console.Error.WriteLine(problem);
        if (problems.Count > 0) return 1;

        Console.WriteLine($"{packFile} is what {manifestFile} compiles to, {pack.Entries.Count} modules the same to the byte with the same reflection.");
        return 0;
    }

    private static int Check(Arguments options)
    {
        string manifestFile = options.Required("manifest");
        var manifest = ShaderManifest.Load(manifestFile);
        var stale = ShaderPackBuilder.Stale(manifest, options.Content());
        foreach (string problem in stale) Console.Error.WriteLine(problem);
        if (stale.Count > 0) return 1;

        Console.WriteLine($"{manifestFile} is up to date with its sources, {manifest.Entries.Count} programs.");
        return 0;
    }

    private static int Refresh(Arguments options)
    {
        string manifestFile = options.Required("manifest");
        string output = options.Optional("out") ?? manifestFile;
        var refreshed = ShaderPackBuilder.Refresh(ShaderManifest.Load(manifestFile), options.Content(), out var changed);
        refreshed.Save(output);

        foreach (string label in changed) Console.WriteLine($"Took {label} as it is now.");
        Console.WriteLine($"{output} has {refreshed.Entries.Count} programs, {changed.Count} of them refreshed.");
        return 0;
    }

    private static int Info(Arguments options)
    {
        string packFile = options.Required("pack");
        ShaderPack pack;
        using (var stream = File.OpenRead(packFile)) pack = ShaderPack.Read(stream, packFile);

        Console.WriteLine($"{packFile}, {pack.TargetProfile}, made from {pack.Manifest}");
        Console.WriteLine($"compiler {pack.Fingerprint}");
        Console.WriteLine($"this engine {ShaderCompiler.CompilerFingerprint}{(pack.Fingerprint == ShaderCompiler.CompilerFingerprint ? ", the same" : ", different, the pack won't be used")}");
        Console.WriteLine($"bindless tables of {string.Join(", ", pack.BindlessCapacities)}");
        foreach (var entry in pack.Entries)
        {
            string stages = string.Join(", ", entry.Stages.Select(stage => stage.X == 0 ? $"{stage.Stage} {stage.EntryPoint}" : $"{stage.Stage} {stage.EntryPoint} {stage.X}x{stage.Y}x{stage.Z}"));
            Console.WriteLine($"{entry.Key}  {entry.Origin}  {stages}  {entry.Params.Count} params  {entry.Spirv.Length} bytes");
        }

        return 0;
    }

    /// <summary>The options after the command, each <c>--name value</c>, and <c>--capacity</c> as often as wanted.</summary>
    private sealed class Arguments
    {
        private readonly Dictionary<string, List<string>> values = new(StringComparer.Ordinal);

        public static Arguments Parse(string[] args)
        {
            var parsed = new Arguments();
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal) || args[i].Length == 2)
                    throw new ArgumentException($"'{args[i]}' isn't an option, they go --name value.");
                if (i + 1 >= args.Length)
                    throw new ArgumentException($"{args[i]} wants a value after it.");

                string name = args[i][2..];
                if (!parsed.values.TryGetValue(name, out var list)) parsed.values[name] = list = [];
                list.Add(args[++i]);
            }

            foreach (var (name, list) in parsed.values)
            {
                if (name is not ("manifest" or "content" or "out" or "pack" or "capacity"))
                    throw new ArgumentException($"There's no option --{name}.");
                if (name != "capacity" && list.Count > 1)
                    throw new ArgumentException($"--{name} is given more than once.");
            }

            return parsed;
        }

        public string? Optional(string name) => values.TryGetValue(name, out var list) ? list[0] : null;

        public string Required(string name) => Optional(name) ?? throw new ArgumentException($"--{name} is needed for this.");

        /// <summary>The content folder, read through the same content source a game on the desktop has.</summary>
        public IContentFiles Content()
        {
            string folder = Required("content");
            if (!Directory.Exists(folder)) throw new ArgumentException($"The content folder '{folder}' isn't there.");
            return new DiskContent(folder);
        }

        public IReadOnlyCollection<uint>? Capacities()
        {
            if (!values.TryGetValue("capacity", out var list)) return null;
            return [.. list.Select(text => uint.TryParse(text, out uint capacity) && capacity > 0
                ? capacity
                : throw new ArgumentException($"--capacity {text} isn't a size, it's a whole number above 0."))];
        }
    }
}
