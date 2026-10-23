using System.Collections.Concurrent;
using System.Diagnostics;

using Horizon.Engine;

namespace Horizon.UI.Components;

// The files watched, so a change saved in Hex (or anything) is taken in without a restart. The settings applied, the
// keys bound again, a plugin that changed started again, one that is new found. The watcher's events come on a thread
// of the pool and in bursts (an editor saving writes a file two or three times), so they are queued and taken in on
// the program's thread a fifth of a second after the last of a burst.
public sealed partial class TerminalHost
{
    private const double SETTLE_SECONDS = 0.2;

    private readonly List<FileSystemWatcher> watchers = [];
    private readonly ConcurrentQueue<string> changedFiles = new();
    private long changedAt;

    /// <summary>What was taken in last, for a test, "settings", "keys" or a plugin's id.</summary>
    public string LastTakenIn { get; private set; } = string.Empty;

    /// <summary>
    /// Watches the folders of the settings, the keys and the user's plugins. Once, after <see cref="Start"/>. A folder
    /// that isn't there yet is made, so the watch is in place for the first file somebody puts in it.
    /// </summary>
    public void Watch()
    {
        var folders = new HashSet<string>(Horizon.Core.Platform.PlatformPaths.Comparer);
        if (options.SettingsFile is { } settings && Path.GetDirectoryName(Path.GetFullPath(settings)) is { } settingsFolder)
            folders.Add(settingsFolder);
        foreach (string keys in options.KeysFiles)
        {
            if (Path.GetDirectoryName(Path.GetFullPath(keys)) is { } keysFolder)
                folders.Add(keysFolder);
        }
        foreach (var (folder, builtIn) in options.PluginFolders)
        {
            if (!builtIn)
                folders.Add(Path.GetFullPath(folder));
        }

        foreach (string folder in folders)
        {
            try
            {
                Directory.CreateDirectory(folder);
                var watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size
                };
                watcher.Changed += Changed;
                watcher.Created += Changed;
                watcher.Deleted += Changed;
                watcher.Renamed += (_, e) => Queue(e.FullPath);
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }
            catch (Exception e)
            {
                Say($"{folder} can't be watched, changes to it want a restart: {e.Message}", true);
            }
        }
    }

    private void Changed(object sender, FileSystemEventArgs e) => Queue(e.FullPath);

    private void Queue(string path)
    {
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

        if (Stopwatch.GetElapsedTime(Interlocked.Read(ref changedAt)).TotalSeconds < SETTLE_SECONDS)
        {
            GameEngine.Instance?.WindowManager.RequestFrames();
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
        foreach (FileSystemWatcher watcher in watchers)
            watcher.Dispose();
        watchers.Clear();
    }
}
