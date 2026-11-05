using Android.Content;
using System.Runtime.InteropServices;

using AndroidBuild = Android.OS.Build;

using Horizon.Content;
using Horizon.Logging;

namespace Horizon.Platform.Android;

/// <summary>
/// What a process on a phone sets up once, before the first engine is made and before anything reads a setting, a
/// file or the shader cache. The log goes to logcat and the app's files folder, the run's HORIZON_* settings are put
/// in the process's environment, storage is the app's own folders and content is the APK's assets. Every activity made
/// in the process after the first finds it done and takes what's there, the settings of a later intent are ignored
/// with a line in the log, a process only ever has one set of them.
/// </summary>
public static class AndroidBootstrap
{
    [DllImport("libc", EntryPoint = "setenv", SetLastError = true)]
    private static extern int SetNativeEnvironment(string name, string value, int overwrite);

    [DllImport("libc", EntryPoint = "getenv")]
    private static extern nint GetNativeEnvironment(string name);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenNativeFile(string path, int flags, int mode);

    [DllImport("libc", EntryPoint = "dup2", SetLastError = true)]
    private static extern int DuplicateNativeFile(int file, int target);

    [DllImport("libc", EntryPoint = "close")]
    private static extern int CloseNativeFile(int file);

    private static readonly Lock gate = new();
    private static AndroidRuntime? runtime;

    // The settings that are paths, put under the app's folders before the engine reads them
    private const string SCREENSHOT = "HORIZON_SCREENSHOT", INPUT_SCRIPT = "HORIZON_INPUT_SCRIPT", SHADER_MANIFEST = "HORIZON_SHADER_MANIFEST";
    private const string SHADERS = "HORIZON_SHADERS";

    /// <summary>What was set up, null before <see cref="Start"/>.</summary>
    public static AndroidRuntime? Current
    {
        get
        {
            lock (gate) return runtime;
        }
    }

    /// <summary>
    /// Sets the process up for the activity that started it, or hands back what was set up already. The main thread.
    /// Throws when content or storage can't be had, which the activity shows rather than carrying on without them.
    /// </summary>
    public static AndroidRuntime Start(Context context, Intent? intent)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (gate)
        {
            if (runtime is not null)
            {
                if (AndroidSettings.ReadExtras(intent, []).Count > 0)
                    Log.Warning("[Android] This intent's HORIZON_* extras are ignored, the process was set up by an earlier one. Stop the app first (am force-stop) for new settings.");
                return runtime;
            }

            Context app = context.ApplicationContext ?? context;
            AndroidPaths paths = AndroidPaths.Of(app);

            // The log first, so whatever goes wrong from here on is heard, and never in the working directory
            var logger = new AndroidLogger(paths.Files);
            Logger.InitializeLogger(logger);
            Log.Info($"[Android] {AndroidBuild.Manufacturer} {AndroidBuild.Model}, Android {AndroidBuild.VERSION.Release} (API {(int)AndroidBuild.VERSION.SdkInt}), {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}.");
            Log.Info($"[Android] Files in {paths.Files}, cache in {paths.Cache}, screenshots in {paths.Screenshots}, debug.log at '{logger.File}'.");

            var problems = new List<string>();
            Dictionary<string, string> settings = AndroidSettings.ReadFile(Path.Combine(paths.Files, AndroidSettings.FILE), problems);
            foreach ((string name, string value) in AndroidSettings.ReadExtras(intent, problems)) settings[name] = value;
            Place(settings, paths, problems);

            // There's no Slang on a phone, shaders only ever come out of the pack. Never left to the fallback
            if (settings.TryGetValue(SHADERS, out string? asked) && !string.Equals(asked, "packed", StringComparison.OrdinalIgnoreCase))
                problems.Add($"{SHADERS}={asked} was asked for, a phone only has the shader pack, so it's packed.");
            settings[SHADERS] = "packed";

            foreach ((string name, string value) in settings)
            {
                Environment.SetEnvironmentVariable(name, value);
                Log.Info($"[Android] {name}={value}");
            }
            foreach (string problem in problems) Log.Warning("[Android] " + problem);

            // Per-process driver experiments, applied before Vulkan loads and never stored as device settings
            if (settings.TryGetValue("HORIZON_TURNIP_MODE", out string? mode))
            {
                if (mode is "bandwidth" or "profiled" or "prefer_sysmem" or "prefer_gmem")
                {
                    Environment.SetEnvironmentVariable("TU_AUTOTUNE_ALGO", mode);
                    if (SetNativeEnvironment("TU_AUTOTUNE_ALGO", mode, 1) != 0)
                        throw new InvalidOperationException("Could not set the native Turnip diagnostic environment.");
                    string? nativeMode = Marshal.PtrToStringUTF8(GetNativeEnvironment("TU_AUTOTUNE_ALGO"));
                    if (nativeMode != mode) throw new InvalidOperationException("Native Turnip environment verification failed.");
                    Log.Info($"[Android] Turnip diagnostic native TU_AUTOTUNE_ALGO={nativeMode}");
                    SetNativeEnvironment("TU_DEBUG", "startup", 1);
                    if (settings.TryGetValue("HORIZON_TURNIP_TUNE_SMALL", out string? small) && small == "on")
                    {
                        if (SetNativeEnvironment("TU_AUTOTUNE_FLAGS", "tune_small", 1) != 0)
                            throw new InvalidOperationException("Could not set native Turnip tuning flags.");
                        Log.Info("[Android] Turnip diagnostic native TU_AUTOTUNE_FLAGS="
                            + Marshal.PtrToStringUTF8(GetNativeEnvironment("TU_AUTOTUNE_FLAGS")));
                    }
                }
                else Log.Warning("[Android] Unknown HORIZON_TURNIP_MODE ignored.");
            }

            if (settings.TryGetValue("HORIZON_TURNIP_SHADER_DUMP", out string? dump) && dump == "on")
            {
                string path = Path.Combine(paths.External, "turnip-shaders.txt");
                int file = OpenNativeFile(path, 0x241, 0x180);
                if (file < 0) throw new IOException("Could not open the native shader diagnostic file.");
                try
                {
                    if (DuplicateNativeFile(file, 2) < 0) throw new IOException("Could not redirect native shader diagnostics.");
                }
                finally { CloseNativeFile(file); }
                if (SetNativeEnvironment("IR3_SHADER_DEBUG", "vs,fs,cs,disasm,nocache", 1) != 0)
                    throw new InvalidOperationException("Could not enable native shader diagnostics.");
                Environment.SetEnvironmentVariable("HORIZON_SHADER_CACHE", "off");
                Log.Info($"[Android] Native shader diagnostics in {path}");
            }

            // Storage and content before the engine, the shader cache, the settings sections or a font ask for either
            var storage = new AndroidStorage(new AndroidStorageRoots(paths.Files, paths.Cache));
            PlatformStorage.Current = storage;

            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            AndroidContent content = AndroidContent.Open(app);
            ContentFiles.Source = content;
            int files = content.Roots.Sum(root => content.DirectoryExists(root) ? content.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count() : 0);
            Log.Info($"[Android] {files} files of content in the APK under {string.Join(", ", content.Roots)}, indexed in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms.");
            if (!content.DirectoryExists("shaders/packed"))
                Log.Error("[Android] The APK has no shaders/packed, every shader will fail to load. Build the pack (Horizon.Shaders.Build) before the APK.");

            runtime = new AndroidRuntime(paths, logger, storage, content, settings, problems);
            return runtime;
        }
    }

    // The settings that name a file, made full paths inside the app's folders, or dropped with a reason
    private static void Place(Dictionary<string, string> settings, AndroidPaths paths, List<string> problems)
    {
        if (settings.TryGetValue(SCREENSHOT, out string? shot))
        {
            // shot.png@3+8x0.5, only the file before the last @ is a path, the rest is when (GameEngine reads it)
            int at = shot.LastIndexOf('@');
            string file = at > 0 ? shot[..at] : shot;
            string when = at > 0 ? shot[at..] : string.Empty;
            if (Resolve(paths.Screenshots, file, paths) is { } full) settings[SCREENSHOT] = full + when;
            else Drop(SCREENSHOT, file, $"screenshots go under {paths.Screenshots}");
        }

        if (settings.TryGetValue(INPUT_SCRIPT, out string? script))
        {
            // Pushed with adb push to the external folder, or with run-as into the files folder
            string? full = Resolve(paths.External, script, paths);
            if (full is not null && !File.Exists(full) && Resolve(paths.Files, script, paths) is { } inFiles && File.Exists(inFiles)) full = inFiles;
            if (full is not null) settings[INPUT_SCRIPT] = full;
            else Drop(INPUT_SCRIPT, script, $"a script is read from {paths.External} or {paths.Files}");
        }

        if (settings.TryGetValue(SHADER_MANIFEST, out string? manifest))
        {
            if (Resolve(paths.External, manifest, paths) is { } full) settings[SHADER_MANIFEST] = full;
            else Drop(SHADER_MANIFEST, manifest, $"a manifest is written under {paths.External}");
        }

        void Drop(string name, string value, string why)
        {
            settings.Remove(name);
            problems.Add($"{name}={value} is left out, it isn't inside the app's folders ({why}).");
        }
    }

    // Relative from a folder, or a full path that's inside one of the app's folders already
    private static string? Resolve(string folder, string path, AndroidPaths paths)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (Path.IsPathRooted(path))
        {
            string full = Path.GetFullPath(path);
            return paths.Owns(full) ? full : null;
        }

        return AndroidPaths.Inside(folder, path);
    }
}

/// <summary>What <see cref="AndroidBootstrap"/> set the process up with.</summary>
/// <param name="Settings">The HORIZON_* settings that were put in the environment, as they were put there.</param>
/// <param name="Problems">What was wrong with the settings it was handed, also in the log.</param>
public sealed record AndroidRuntime(AndroidPaths Paths, AndroidLogger Logger, AndroidStorage Storage, AndroidContent Content,
    IReadOnlyDictionary<string, string> Settings, IReadOnlyList<string> Problems);
