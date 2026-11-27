using System.Diagnostics;
using System.Numerics;
using Horizon.Engine;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Diagnostics;
using Horizon.UI.Drawing;
using Horizon.UI.Scripting;
using Horizon.UI.Skinning;

namespace Horizon.Tests.UI;

/// <summary>
/// Regression tests for Pass 9 startup and working-set optimizations: lazy skin metadata discovery,
/// deferred disabled <see cref="UICompositor"/> initialization, on-demand <c>devicon</c> symbol loading,
/// lazy <see cref="PerformanceFace"/> card construction, <see cref="CodeEditor"/> prefix colouring handoff,
/// and deferred <see cref="TerminalHost"/> startup.
/// </summary>
public sealed class Pass9StartupAndWorkingSetTests
{
    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "Horizon.sln")))
            dir = Directory.GetParent(dir)!.FullName;
        return dir;
    }

    [Fact]
    public void UISkin_DescribeMetadata_evaluates_only_usual_theme_on_startup_scan()
    {
        string flatDir = Path.Combine(RepoRoot(), "Horizon", "Assets", "uix", "flat") + Path.DirectorySeparatorChar;

        int before = UISkin.ThemeFilesEvaluated;
        var (title, themes, usual) = UISkin.DescribeMetadata(flatDir, UISkins.FILE);
        int evaluated = UISkin.ThemeFilesEvaluated - before;

        Assert.False(string.IsNullOrEmpty(title));
        Assert.Equal("horizon", usual);
        Assert.True(themes.Length >= 8);
        Assert.Equal(1, evaluated);
    }

    [Fact]
    public void Disabled_UICompositor_defers_skin_load_until_enabled()
    {
        var camera = new Camera2D(new Vector2(800, 600));
        using var compositor = new UICompositor(camera, null) { Enabled = false };
        compositor.Initialize();
        compositor.UpdateState(1.0f / 60.0f);

        Assert.Null(compositor.Skin);
        Assert.False(compositor.IsSkinSourceResolved);

        compositor.Enabled = true;
        compositor.UpdateState(1.0f / 60.0f);

        Assert.True(compositor.IsSkinSourceResolved);

        string flatDir = Path.Combine(RepoRoot(), "Horizon", "Assets", "uix", "flat") + Path.DirectorySeparatorChar;
        int before = UISkin.ThemeFilesEvaluated;
        using var flatCompositor = new UICompositor(camera, flatDir, UISkins.FILE) { Enabled = false };
        flatCompositor.Initialize();
        flatCompositor.UpdateState(1.0f / 60.0f);

        Assert.Equal(before, UISkin.ThemeFilesEvaluated);

        flatCompositor.Enabled = true;
        flatCompositor.UpdateState(1.0f / 60.0f);

        Assert.True(UISkin.ThemeFilesEvaluated > before || flatCompositor.Skin is not null);
    }

    [Fact]
    public void UISymbols_loads_codicons_without_eagerly_building_devicon_unless_filetype_requested()
    {
        UISymbols.EnsureLoaded();
        bool loadedBefore = UISymbols.AreFileKindsLoaded;
        Assert.True(UISymbols.TryGet("search", out _));
        if (!loadedBefore)
            Assert.False(UISymbols.AreFileKindsLoaded);

        // Requesting a file-kind icon triggers lazy devicon loading even when no GPU device is present.
        _ = UISymbols.TryGet("filetype_csharp", out _);
        Assert.True(UISymbols.AreFileKindsLoaded);
    }

    [Fact]
    public void PerformanceFace_defers_card_layout_until_Full_detail_is_requested()
    {
        var compositor = new UICompositor(new Camera2D(new Vector2(1280, 720)));
        var module = compositor.CreateModule();
        var sample = new PerformanceSample();

        var face = new PerformanceFace(module, sample);
        Assert.False(face.HasCard);
        face.HideCard();
        face.Show(PerformanceDetail.Compact);
        Assert.False(face.HasCard);

        face.Show(PerformanceDetail.Full);
        Assert.True(face.HasCard);
        Assert.NotNull(face.Card);

        compositor.Dispose();
    }

    [Fact]
    public void CodeEditor_defers_past_96_lines_synchronously_and_finishes_background_colouring()
    {
        var lines = new List<string>(142) { "public class Box {" };
        for (int i = 1; i <= 108; i++)
            lines.Add($"    public int Value{i} => {i};");
        lines.Add("    public int Compute() {");
        for (int i = 110; i <= 138; i++)
            lines.Add($"        int v{i} = {i};");
        lines.Add("        return v138;");
        lines.Add("    }");
        lines.Add("}");

        var editor = new CodeEditor(string.Join('\n', lines)) { Highlighter = CodeLanguages.ForExtension(".cs") };

        editor.UpdateTree(0.02f);

        // Synchronous Recolour stops at COLOUR_AT_ONCE = 96 lines, so blocks closing past line 96 are not yet folded.
        Assert.False(editor.CanFold(0));
        Assert.False(editor.CanFold(109));

        // Pump until the asynchronous background pass completes past line 96 and installs folds.
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < 5000 && (!editor.CanFold(0) || !editor.CanFold(109)))
        {
            editor.UpdateTree(0.05f);
            Thread.Sleep(5);
        }

        Assert.True(editor.CanFold(0));
        Assert.True(editor.CanFold(109));
    }

    [Fact]
    public void TerminalHost_with_DeferStartup_skips_heavy_startup_work_until_EnsureStarted()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "horizon-term-defer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string historyFile = Path.Combine(tempDir, "history.txt");
        File.WriteAllLines(historyFile, ["echo one", "echo two"]);

        try
        {
            var panel = new TerminalPanel();
            using var host = new TerminalHost(panel, new TerminalHostOptions
            {
                DiscoverProfiles = false,
                HistoryFile = historyFile,
                SettingsFile = Path.Combine(tempDir, "settings.hor"),
                KeysFiles = [Path.Combine(tempDir, "keys.hor")],
                PluginFolders = [(Path.Combine(tempDir, "plugins"), false)],
                DeferStartup = true
            });

            host.Start();
            host.Watch();

            Assert.False(host.IsStartupCompleted);
            Assert.Empty(host.Watchers);

            host.EnsureStarted();

            Assert.True(host.IsStartupCompleted);
            Assert.Equal(2, host.History.Count);
            Assert.NotEmpty(host.Watchers);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
