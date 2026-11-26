using System.Diagnostics;

using Horizon.UI.Components;

namespace Horizon.Tests.Terminal;

/// <summary>
/// Unit and regression tests for Pass 7-D:
/// TerminalHost watched-path filtering and non-recursive settings/keys watchers,
/// lazy zero-closure TerminalCommandLookup PATH scanning, and non-blocking WSL profile discovery.
/// </summary>
public sealed class Pass7TerminalIdleAndStartupTests
{
    [Fact]
    public void TerminalHost_IsWatchedPath_filters_runtime_files_and_uses_non_recursive_settings_watcher()
    {
        string root = Path.Combine(Path.GetTempPath(), $"pass7-terminal-watch-{Guid.NewGuid():N}");
        string settingsFile = Path.Combine(root, "settings.hor");
        string keysFile = Path.Combine(root, "keys.hor");
        string userPlugins = Path.Combine(root, "plugins");
        string missingUserPlugins = Path.Combine(root, "plugins-missing");
        string builtInPlugins = Path.Combine(root, "builtin");
        string cacheFolder = Path.Combine(root, "cache");

        Directory.CreateDirectory(root);
        Directory.CreateDirectory(userPlugins);
        Directory.CreateDirectory(builtInPlugins);
        Directory.CreateDirectory(cacheFolder);

        try
        {
            using var panel = new TerminalPanel { AnimationTime = 0 };
            using var host = new TerminalHost(panel, new TerminalHostOptions
            {
                DiscoverProfiles = false,
                SettingsFile = settingsFile,
                KeysFiles = [keysFile],
                PluginFolders =
                [
                    (builtInPlugins, true),
                    (userPlugins, false),
                    (missingUserPlugins, false)
                ]
            });

            // Watched files: settings, keys, and files inside configured user plugin folders
            Assert.True(host.IsWatchedPath(settingsFile));
            Assert.True(host.IsWatchedPath(keysFile));
            Assert.True(host.IsWatchedPath(Path.Combine(userPlugins, "my_plugin", "plugin.hor")));
            Assert.True(host.IsWatchedPath(Path.Combine(userPlugins, "my_plugin", "nested", "helper.hor")));

            // Unwatched runtime / cache / built-in files must never match
            Assert.False(host.IsWatchedPath(Path.Combine(root, "sessions.hor")));
            Assert.False(host.IsWatchedPath(Path.Combine(root, "api.hor")));
            Assert.False(host.IsWatchedPath(Path.Combine(root, "history.txt")));
            Assert.False(host.IsWatchedPath(Path.Combine(cacheFolder, "commands-pwsh.txt")));
            Assert.False(host.IsWatchedPath(Path.Combine(builtInPlugins, "core", "plugin.hor")));
            Assert.False(host.IsWatchedPath(string.Empty));
            Assert.False(host.IsWatchedPath("   "));

            // Queue() must ignore unwatched paths before enqueuing or waking the window
            host.Queue(Path.Combine(root, "sessions.hor"));
            host.Queue(Path.Combine(root, "api.hor"));
            host.Queue(Path.Combine(root, "history.txt"));
            host.Queue(Path.Combine(cacheFolder, "commands-pwsh.txt"));
            Assert.Equal(0, host.PendingWatchCount);

            host.Queue(settingsFile);
            Assert.Equal(1, host.PendingWatchCount);

            // Watch() attaches a non-recursive watcher for settings/keys and recursive watchers only for existing user plugin folders
            host.Watch();
            Assert.Equal(2, host.Watchers.Count);

            FileSystemWatcher? rootWatcher = host.Watchers.FirstOrDefault(w => string.Equals(
                Path.GetFullPath(w.Path).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar),
                Horizon.Core.Platform.PlatformPaths.Comparison));
            FileSystemWatcher? pluginsWatcher = host.Watchers.FirstOrDefault(w => string.Equals(
                Path.GetFullPath(w.Path).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(userPlugins).TrimEnd(Path.DirectorySeparatorChar),
                Horizon.Core.Platform.PlatformPaths.Comparison));

            Assert.NotNull(rootWatcher);
            Assert.False(rootWatcher.IncludeSubdirectories);

            Assert.NotNull(pluginsWatcher);
            Assert.True(pluginsWatcher.IncludeSubdirectories);
            Assert.False(Directory.Exists(missingUserPlugins));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TerminalCommandLookup_defers_ScanPath_lazily_and_matches_extensions_with_zero_allocations()
    {
        TerminalCommandLookup.ResetPathScanForTests();

        // Constructing lookups for cmd/powershell must not eagerly scan PATH at cold startup
        var pwsh = new TerminalCommandLookup("powershell");
        var cmd = new TerminalCommandLookup("cmd");
        Assert.False(TerminalCommandLookup.HasStartedPathScan);

        // Built-in shell words resolve immediately without triggering a PATH scan
        Assert.True(pwsh.Knows("cd"));
        Assert.True(cmd.Knows("dir"));
        Assert.False(TerminalCommandLookup.HasStartedPathScan);

        // First non-builtin lookup triggers the lazy background PATH scan
        _ = cmd.Knows("\u0001pass7-lazy-probe");
        Assert.True(TerminalCommandLookup.HasStartedPathScan);

        // Zero-closure, zero-allocation extension matching
        string[] extensions = [".COM", ".EXE", ".BAT", ".CMD"];
        Assert.True(TerminalCommandLookup.HasExecutableExtension(@"C:\Windows\System32\cmd.exe".AsSpan(), extensions));
        Assert.True(TerminalCommandLookup.HasExecutableExtension(@"C:\Tools\script.CMD".AsSpan(), extensions));
        Assert.True(TerminalCommandLookup.HasExecutableExtension(@"C:\Tools\build.bat".AsSpan(), extensions));
        Assert.False(TerminalCommandLookup.HasExecutableExtension(@"C:\Tools\library.dll".AsSpan(), extensions));
        Assert.False(TerminalCommandLookup.HasExecutableExtension(@"C:\Tools\README".AsSpan(), extensions));

        // Warm up JIT, then verify 0 managed bytes allocated across repeated checks
        ReadOnlySpan<char> sampleExe = @"C:\Program Files\Git\cmd\git.exe".AsSpan();
        ReadOnlySpan<char> sampleDll = @"C:\Program Files\Git\cmd\libgit.dll".AsSpan();
        bool matched = TerminalCommandLookup.HasExecutableExtension(sampleExe, extensions);
        bool rejected = TerminalCommandLookup.HasExecutableExtension(sampleDll, extensions);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++)
        {
            matched &= TerminalCommandLookup.HasExecutableExtension(sampleExe, extensions);
            rejected |= TerminalCommandLookup.HasExecutableExtension(sampleDll, extensions);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(matched);
        Assert.False(rejected);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void TerminalProfiles_Discover_does_not_block_on_pending_WSL_and_merges_with_monotonic_Version()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var pendingWsl = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string?>? previousTask = TerminalProfiles.SwapWslTaskForTests(pendingWsl.Task);
        try
        {
            int versionBefore = TerminalProfiles.Version;

            var sw = Stopwatch.StartNew();
            List<TerminalProfile> initial = TerminalProfiles.Discover();
            sw.Stop();

            // Must return native Windows shells immediately without blocking on pendingWsl
            Assert.False(pendingWsl.Task.IsCompleted);
            Assert.Contains(initial, p => p.Id == "powershell");
            Assert.Contains(initial, p => p.Id == "cmd");
            Assert.DoesNotContain(initial, p => p.Id.StartsWith("wsl:", StringComparison.Ordinal));
            Assert.True(sw.ElapsedMilliseconds < 2000, $"Expected non-blocking Discover(), took {sw.ElapsedMilliseconds} ms.");

            // Once WSL finishes in the background, profiles merge and Version increments monotonically
            const string wslOutput = "Ubuntu\r\nArch\r\n";
            pendingWsl.TrySetResult(wslOutput);
            TerminalProfiles.MergeWslForTests(wslOutput);

            Assert.True(TerminalProfiles.Version > versionBefore);

            List<TerminalProfile> updated = TerminalProfiles.Discover();
            Assert.Contains(updated, p => p.Id == "wsl:Ubuntu" && p.Command == "wsl.exe -d Ubuntu");
            Assert.Contains(updated, p => p.Id == "wsl:Arch" && p.Command == "wsl.exe -d Arch");
        }
        finally
        {
            TerminalProfiles.SwapWslTaskForTests(previousTask);
            TerminalProfiles.InvalidateCache();
        }
    }
}
