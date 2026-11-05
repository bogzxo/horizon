using System.Reflection;
using System.Text;

using Horizon.Logging;

namespace Horizon.Graphics;

/* The cache on disk. The module, and a small text file beside it saying where its entry points are and how its Params are laid out */
public static partial class ShaderCompiler
{
    private const string CACHE_VARIABLE = "HORIZON_SHADER_CACHE";

    // Days a compiled shader nobody has loaded stays on disk, every edit of a shader leaves the old one behind
    private const int CACHE_DAYS = 30;

    private static string? cacheDirectory;
    private static bool checkedCacheDirectory;

    /// <summary>Where compiled shaders (and the driver's pipeline cache) are kept, null if they aren't.</summary>
    public static string? CacheDirectory()
    {
        if (checkedCacheDirectory) return cacheDirectory;
        checkedCacheDirectory = true;

        string? setting = Environment.GetEnvironmentVariable(CACHE_VARIABLE);
        if (string.Equals(setting, "off", StringComparison.OrdinalIgnoreCase)) return cacheDirectory = null;

        // By the game rather than the process, which is "dotnet" for a game that is started through it. LocalAppData on
        // Windows, ~/Library/Caches on macOS, ~/.cache on Linux, or the cache folder next to a game that keeps its files there
        string game = Assembly.GetEntryAssembly()?.GetName().Name ?? "Horizon";
        cacheDirectory = string.IsNullOrWhiteSpace(setting)
            ? Path.Combine(Horizon.Platform.PlatformStorage.Cache(Path.Combine("Horizon", game)), "Shaders")
            : setting;

        Log.Info($"[ShaderCompiler] Compiled shaders are kept in '{cacheDirectory}'.");
        string folder = cacheDirectory;
        Task.Run(() => Prune(folder));
        return cacheDirectory;
    }

    /// <summary>
    /// Helper method to throw away what hasn't been loaded for a month, off whatever thread asked first. A shader is
    /// kept by a hash of its source, so every edit of one leaves the old one behind for good otherwise.
    /// </summary>
    private static void Prune(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return;

            DateTime before = DateTime.UtcNow.AddDays(-CACHE_DAYS);
            foreach (string file in Directory.EnumerateFiles(folder))
            {
                string extension = Path.GetExtension(file);
                if (extension is not (".spv" or ".notes" or ".partial")) continue;
                if (File.GetLastWriteTimeUtc(file) >= before) continue;

                try
                {
                    File.Delete(file);
                }
                catch (Exception)
                {
                    // Another instance has it open, it goes next time
                }
            }
        }
        catch (Exception)
        {
            // A cache that can't be tidied is a cache that's a bit bigger
        }
    }

    /// <summary>Helper method to say a kept file was used, so the pruning leaves it alone. Once a day is plenty.</summary>
    private static void Touch(string file)
    {
        try
        {
            DateTime now = DateTime.UtcNow;
            if (now - File.GetLastWriteTimeUtc(file) > TimeSpan.FromDays(1))
                File.SetLastWriteTimeUtc(file, now);
        }
        catch (Exception)
        {
            // Read only, or somebody else is touching it, either way it was loaded
        }
    }

    internal static bool TryLoadCached(string folder, string key, out ShaderModule kept)
    {
        kept = null!;
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
            kept = new ShaderModule([.. stages], UniformBlockLayout.FromMembers(members));
            Touch(spirvPath);
            Touch(notesPath);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static void KeepCached(string folder, string key, ShaderModule module)
    {
        if (module.Stages.Length == 0) return;

        try
        {
            Directory.CreateDirectory(folder);

            var notes = new StringBuilder();
            foreach (var stage in module.Stages)
            {
                uint[] groups = stage.ThreadGroupSize.Length == 3 ? stage.ThreadGroupSize : [0, 0, 0];
                notes.Append("entry\t").Append(stage.Stage).Append('\t').Append(stage.EntryPoint).Append('\t').Append(groups[0]).Append('\t').Append(groups[1]).Append('\t').Append(groups[2]).Append('\n');
            }

            foreach (var member in module.Params.Members)
                notes.Append("member\t").Append(member.Name).Append('\t').Append(member.Type).Append('\t').Append(member.Offset).Append('\t').Append(member.Size).Append('\t').Append(member.ArrayLength).Append('\t').Append(member.ArrayStride).Append('\n');

            // Written next to where they go and moved there whole, so a game that is closed halfway leaves no half a shader
            string spirvPath = Path.Combine(folder, key + ".spv"), notesPath = Path.Combine(folder, key + ".notes");
            string partial = "." + Environment.CurrentManagedThreadId + ".partial";
            File.WriteAllBytes(spirvPath + partial, module.Stages[0].Spirv);
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
