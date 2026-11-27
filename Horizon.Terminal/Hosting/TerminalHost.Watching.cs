using System.Collections.Concurrent;
using System.Diagnostics;

using Horizon.Engine;

namespace Horizon.UI.Components;

// The files watched, so a change saved in Hex (or anything) is reloaded without a restart. The settings applied, the
// keys bound again, a plugin that changed started again, one that is new found. The watcher's events come on a thread
// of the pool and in bursts (an editor saving writes a file two or three times), so they are queued and reloaded on
// the program's thread a fifth of a second after the last of a burst.
public sealed partial class TerminalHost
{
    private const double SETTLE_SECONDS = 0.2;

    private readonly List<FileSystemWatcher> watchers = [];
    private readonly ConcurrentQueue<string> changedFiles = new();
    private long changedAt;

    /// <summary>What was taken in last, for a test, "settings", "keys" or a plugin's id.</summary>
    public string LastTakenIn { get; private set; } = string.Empty;

    /// <summary>The active file system watchers, for testing.</summary>
    internal IReadOnlyList<FileSystemWatcher> Watchers => watchers;

    /// <summary>Number of queued or unsettled watched paths, for testing.</summary>
    internal int PendingWatchCount => changedFiles.Count + pending.Count;

    /// <summary>
    /// Whether a file path is one this host actually reloads: the settings file, any configured keys file, or a file
    /// inside a configured user plugin folder. Unrelated writes in the user folder (sessions.hor, api.hor, history.txt,
    /// cache/commands-*.txt) return false so they never queue work or wake the window.
    /// </summary>
    public bool IsWatchedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            if (options.SettingsFile is { } settingsFile && SameFile(path, settingsFile))
                return true;

            for (int i = 0; i < options.KeysFiles.Count; i++)
            {
                if (SameFile(path, options.KeysFiles[i]))
                    return true;
            }

            return PluginFolderOf(path) is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Watches the folders of the settings, the keys and the user's plugins. Once, after <see cref="Start"/>. The
    /// settings and keys folders are watched non-recursively so cache writes never fire the watcher; separate recursive
    /// watchers are attached only for existing configured user plugin folders.
    /// </summary>
    public void Watch()
    {
        if (deferredStartPending)
        {
            watchDeferred = true;
            return;
        }

        WatchNow();
    }

    private void WatchNow()
    {
        var shallowFolders = new HashSet<string>(Horizon.Core.Platform.PlatformPaths.Comparer);
        if (options.SettingsFile is { } settings && Path.GetDirectoryName(Path.GetFullPath(settings)) is { } settingsFolder)
            shallowFolders.Add(settingsFolder);
        for (int i = 0; i < options.KeysFiles.Count; i++)
        {
            if (Path.GetDirectoryName(Path.GetFullPath(options.KeysFiles[i])) is { } keysFolder)
                shallowFolders.Add(keysFolder);
        }

        var recursiveFolders = new HashSet<string>(Horizon.Core.Platform.PlatformPaths.Comparer);
        for (int i = 0; i < options.PluginFolders.Count; i++)
        {
            var (folder, builtIn) = options.PluginFolders[i];
            if (builtIn)
                continue;

            string full = Path.GetFullPath(folder);
            if (Directory.Exists(full))
            {
                recursiveFolders.Add(full);
                shallowFolders.Remove(full);
            }
        }

        foreach (string folder in shallowFolders)
            AttachWatcher(folder, includeSubdirectories: false, createIfMissing: true);

        foreach (string folder in recursiveFolders)
            AttachWatcher(folder, includeSubdirectories: true, createIfMissing: false);
    }

    private void AttachWatcher(string folder, bool includeSubdirectories, bool createIfMissing)
    {
        try
        {
            if (createIfMissing)
                Directory.CreateDirectory(folder);
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = includeSubdirectories,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size
            };
            watcher.Changed += Changed;
            watcher.Created += Changed;
            watcher.Deleted += Changed;
            watcher.Renamed += Renamed;
            watcher.EnableRaisingEvents = true;
            watchers.Add(watcher);
        }
        catch (Exception e)
        {
            Say($"{folder} can't be watched, changes to it want a restart: {e.Message}", true);
        }
    }

    private void Changed(object sender, FileSystemEventArgs e) => Queue(e.FullPath);

    private void Renamed(object sender, RenamedEventArgs e) => Queue(e.FullPath);

    internal void Queue(string path)
    {
        if (!IsWatchedPath(path))
            return;

        changedFiles.Enqueue(path);
        Interlocked.Exchange(ref changedAt, Stopwatch.GetTimestamp());
        GameEngine.Instance?.WindowManager.Wake();
    }

    // The files of a burst, gathered until it settles
    private readonly HashSet<string> pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The files that changed taken in, once their burst has settled. Every tick, nothing to do most of them.</summary>
    private void TakeChanges()
    {
        if (changedFiles.IsEmpty && pending.Count == 0)
            return;

        while (changedFiles.TryDequeue(out string? path))
            pending.Add(path);

        double elapsed = Stopwatch.GetElapsedTime(Interlocked.Read(ref changedAt)).TotalSeconds;
        if (elapsed < SETTLE_SECONDS)
        {
            GameEngine.Instance?.WindowManager.RequestTickIn(Math.Max(0.01, SETTLE_SECONDS - elapsed));
            return;
        }

        TakeSettled();
    }

    /// <summary>A burst that settled sorted into what it was and taken in. Apart from TakeChanges, which runs every tick.</summary>
    private void TakeSettled()
    {
        bool settings = false, keys = false;
        var pluginFolders = new HashSet<string>(Horizon.Core.Platform.PlatformPaths.Comparer);
        foreach (string path in pending)
        {
            if (options.SettingsFile is { } file && SameFile(path, file))
                settings = true;
            else if (options.KeysFiles.Any(keysFile => SameFile(path, keysFile)))
                keys = true;
            else if (PluginFolderOf(path) is { } folder)
                pluginFolders.Add(folder);
        }

        pending.Clear();
        TakeIn(settings, keys, pluginFolders);
    }

    /// <summary>What changed taken in, the settings read again, the keys bound again, the plugins that changed started again.</summary>
    public void TakeIn(bool settings, bool keys, IEnumerable<string> pluginFolders)
    {
        if (settings && writtenSettings is not null && options.SettingsFile is { } file && File.Exists(file) && ReadOrEmpty(file) == writtenSettings)
            settings = false;

        if (settings)
        {
            // The file is the editor's as well, a change to its section alone (Hex saving its own) is nothing to say
            string before = Settings.OwnText();
            if (Settings.Reload())
            {
                if (Settings.OwnText() != before)
                {
                    ApplySettings();
                    Say("settings.hor read again");
                }
            }
            else
            {
                Say("settings.hor doesn't read, it is left as it was", true);
            }

            LastTakenIn = "settings";
        }

        if (keys)
        {
            Commands.LoadKeys([.. options.KeysFiles]);
            Say("keys.hor read again");
            LastTakenIn = "keys";
        }

        foreach (string folder in pluginFolders)
        {
            TerminalPlugin? plugin = plugins.Find(each => SameFile(each.Folder, folder));
            if (plugin is null && File.Exists(Path.Combine(folder, TerminalPlugin.FILE)))
            {
                plugin = new TerminalPlugin(folder, builtIn: false) { Enabled = !Settings.DisabledPlugins.Contains(Path.GetFileName(folder)) };
                plugins.Add(plugin);
            }

            if (plugin is not { Enabled: true })
                continue;

            if (!File.Exists(Path.Combine(folder, TerminalPlugin.FILE)))
            {
                Commands.Unregister(plugin);
                plugins.Remove(plugin);
                Say($"the plugin {plugin.Name} is gone");
            }
            else
            {
                objects.Remove(plugin);
                StartPlugin(plugin);
                Commands.LoadKeys([.. options.KeysFiles]);
                if (plugin.Failed is null)
                    Say($"the plugin {plugin.Name} started again");
            }

            LastTakenIn = plugin.Id;
        }

        if (keys || pluginFolders.Any())
            WriteApi();
    }

    /// <summary>The folder of the user's plugin a path is in, null for one that is in none.</summary>
    private string? PluginFolderOf(string path)
    {
        foreach (var (folder, builtIn) in options.PluginFolders)
        {
            if (builtIn)
                continue;

            string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(path);
            if (!full.StartsWith(root, Horizon.Core.Platform.PlatformPaths.Comparison))
                continue;

            string inside = full[root.Length..];
            int slash = inside.IndexOf(Path.DirectorySeparatorChar);
            return Path.Combine(root, slash < 0 ? inside : inside[..slash]);
        }

        return null;
    }

    private static string ReadOrEmpty(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static bool SameFile(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), Horizon.Core.Platform.PlatformPaths.Comparison);

    /// <summary>The watchers let go of.</summary>
    public void Dispose()
    {
        if (saveSettingsPending)
            SaveSettings();

        foreach (FileSystemWatcher watcher in watchers)
            watcher.Dispose();
        watchers.Clear();
    }
}
