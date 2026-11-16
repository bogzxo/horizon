using System.Runtime.InteropServices;

using Foundation;

using Horizon.Content;
using Horizon.Logging;

using UIKit;

namespace Horizon.Platform.iOS;

/// <summary>
/// What a process on an iPhone sets up once, before the first engine is made and before anything reads a setting, a
/// file or the shader cache. The log goes to the console and Documents/debug.log, the run's HORIZON_* settings are put
/// in the process's environment, storage is the app's own folders and content is the signed bundle.
/// <para>
/// The settings come from Documents/horizon.env (one NAME=value a line) and then from the process's environment,
/// which wins. The simulator hands a launch whatever it was given as SIMCTL_CHILD_NAME, so
/// <c>SIMCTL_CHILD_HORIZON_SCREENSHOT=shot.png@3 xcrun simctl launch booted dev.horizon.testing</c> works like the
/// desktop's environment does.
/// </para>
/// </summary>
public static class IosBootstrap
{
    private const string PREFIX = "HORIZON_", FILE = "horizon.env";

    // The settings that are paths, put under Documents before the engine reads them
    private const string SCREENSHOT = "HORIZON_SCREENSHOT", INPUT_SCRIPT = "HORIZON_INPUT_SCRIPT", SHADER_MANIFEST = "HORIZON_SHADER_MANIFEST";
    private const string SHADERS = "HORIZON_SHADERS";

    private static readonly Lock gate = new();
    private static IosRuntime? runtime;

    /// <summary>What was set up, null before <see cref="Start"/>.</summary>
    public static IosRuntime? Current
    {
        get
        {
            lock (gate) return runtime;
        }
    }

    /// <summary>
    /// Sets the process up, or hands back what was set up already. The main thread. Throws when content or storage
    /// can't be had, which the app shows rather than carrying on without them.
    /// </summary>
    public static IosRuntime Start()
    {
        lock (gate)
        {
            if (runtime is not null) return runtime;

            string documents = Folder(NSSearchPathDirectory.DocumentDirectory);
            string screenshots = Path.Combine(documents, "screenshots");
            Directory.CreateDirectory(screenshots);

            // The log first, so whatever goes wrong from here on is heard
            var logger = new IosLogger(documents);
            Logger.InitializeLogger(logger);
            UIDevice device = UIDevice.CurrentDevice;
            Log.Info($"[iOS] {device.Model} ({Machine()}), {device.SystemName} {device.SystemVersion}, {RuntimeInformation.ProcessArchitecture}, {RuntimeInformation.FrameworkDescription}.");
            Log.Info($"[iOS] Documents in {documents}, debug.log at '{logger.File}'.");

            var problems = new List<string>();
            Dictionary<string, string> settings = ReadFile(Path.Combine(documents, FILE), problems);
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is string name && name.StartsWith(PREFIX, StringComparison.Ordinal) && entry.Value is string value) settings[name] = value;
            }
            Place(settings, documents, screenshots, problems);

            // There's no Slang on a phone, shaders only ever come out of the pack. Never left to the fallback
            if (settings.TryGetValue(SHADERS, out string? asked) && !string.Equals(asked, "packed", StringComparison.OrdinalIgnoreCase))
                problems.Add($"{SHADERS}={asked} was asked for, an iPhone only has the shader pack, so it's packed.");
            settings[SHADERS] = "packed";

            foreach ((string name, string value) in settings)
            {
                Environment.SetEnvironmentVariable(name, value);
                Log.Info($"[iOS] {name}={value}");
            }
            foreach (string problem in problems) Log.Warning("[iOS] " + problem);

            // Storage and content before the engine, the shader cache, the settings sections or a font ask for either
            var storage = new IosStorage();
            PlatformStorage.Current = storage;

            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            var content = new IosContent();
            ContentFiles.Source = content;
            string[] roots = ["Assets", "shaders"];
            int files = roots.Sum(root => content.DirectoryExists(root) ? content.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count() : 0);
            Log.Info($"[iOS] {files} files of content in the bundle under {string.Join(", ", roots)}, indexed in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms.");
            if (!content.DirectoryExists("shaders/packed"))
                Log.Error("[iOS] The bundle has no shaders/packed, every shader will fail to load. Build the pack (Horizon.Shaders.Build) before the app.");

            runtime = new IosRuntime(documents, screenshots, logger, storage, content, settings, problems);
            return runtime;
        }
    }

    // NAME=value lines, # for a comment, only HORIZON_ names
    private static Dictionary<string, string> ReadFile(string path, List<string> problems)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return settings;

        int number = 0;
        foreach (string raw in File.ReadLines(path))
        {
            number++;
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            int equals = line.IndexOf('=');
            string name = equals > 0 ? line[..equals].Trim() : string.Empty;
            if (!name.StartsWith(PREFIX, StringComparison.Ordinal))
            {
                problems.Add($"{FILE} line {number} isn't a HORIZON_NAME=value line, it's skipped.");
                continue;
            }
            settings[name] = line[(equals + 1)..].Trim();
        }

        return settings;
    }

    // The settings that name a file, made full paths inside Documents, or dropped with a reason
    private static void Place(Dictionary<string, string> settings, string documents, string screenshots, List<string> problems)
    {
        if (settings.TryGetValue(SCREENSHOT, out string? shot))
        {
            // shot.png@3+8x0.5, only the file before the last @ is a path, the rest is when (GameEngine reads it)
            int at = shot.LastIndexOf('@');
            string file = at > 0 ? shot[..at] : shot;
            string when = at > 0 ? shot[at..] : string.Empty;
            if (Inside(screenshots, file) is { } full) settings[SCREENSHOT] = full + when;
            else Drop(SCREENSHOT, file, $"screenshots go under {screenshots}");
        }

        if (settings.TryGetValue(INPUT_SCRIPT, out string? script))
        {
            if (Inside(documents, script) is { } full) settings[INPUT_SCRIPT] = full;
            else Drop(INPUT_SCRIPT, script, $"a script is read from {documents}");
        }

        if (settings.TryGetValue(SHADER_MANIFEST, out string? manifest))
        {
            if (Inside(documents, manifest) is { } full) settings[SHADER_MANIFEST] = full;
            else Drop(SHADER_MANIFEST, manifest, $"a manifest is written under {documents}");
        }

        void Drop(string name, string value, string why)
        {
            settings.Remove(name);
            problems.Add($"{name}={value} is left out, it isn't inside the app's Documents ({why}).");
        }
    }

    // Relative from a folder, or a full path that's inside it already. Nothing that climbs out
    private static string? Inside(string folder, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string root = Path.GetFullPath(folder).TrimEnd('/');
        string full = Path.GetFullPath(path, root);
        return full.StartsWith(root + '/', StringComparison.Ordinal) ? full : null;
    }

    private static string Folder(NSSearchPathDirectory which)
    {
        string folder = NSSearchPath.GetDirectories(which, NSSearchPathDomain.User, true).First();
        Directory.CreateDirectory(folder);
        return folder;
    }

    [DllImport("libc", EntryPoint = "sysctlbyname")]
    private static extern int SysctlByName(string name, byte[]? value, ref nint length, nint newValue, nint newLength);

    // The model's code (iPhone17,1), the simulator says arm64
    private static string Machine()
    {
        nint length = 0;
        if (SysctlByName("hw.machine", null, ref length, 0, 0) != 0 || length <= 0) return "unknown";
        byte[] value = new byte[length];
        if (SysctlByName("hw.machine", value, ref length, 0, 0) != 0) return "unknown";
        return System.Text.Encoding.ASCII.GetString(value, 0, (int)Math.Max(0, length - 1));
    }
}

/// <summary>What <see cref="IosBootstrap"/> set the process up with.</summary>
/// <param name="Documents">The app's Documents folder, where debug.log, horizon.env and the scripts go.</param>
/// <param name="Screenshots">Where a screenshot key's pictures go.</param>
/// <param name="Settings">The HORIZON_* settings that were put in the environment, as they were put there.</param>
/// <param name="Problems">What was wrong with the settings it was handed, also in the log.</param>
public sealed record IosRuntime(string Documents, string Screenshots, IosLogger Logger, IosStorage Storage, IosContent Content,
    IReadOnlyDictionary<string, string> Settings, IReadOnlyList<string> Problems);
