using Foundation;

using Horizon.Engine;
using Horizon.Logging;
using Horizon.Platform;
using Horizon.Platform.iOS;

using UIKit;

namespace Horizon.Testing.iOS;

/// <summary>
/// Horizon.Testing on an iPhone. Everything iOS is HorizonAppDelegate's. This puts the same <see cref="TestHost"/>
/// and performance overlay on the engine that the desktop's Program does and starts in whatever HORIZON_TESTING_SCENE
/// says, the selector when nothing does, see README.md.
/// </summary>
[Register("AppDelegate")]
public sealed class AppDelegate : HorizonAppDelegate
{
    // The setting that picks where to start, from the environment (SIMCTL_CHILD_HORIZON_TESTING_SCENE) or horizon.env
    private const string SCENE = "HORIZON_TESTING_SCENE";
    private const string ALL_CHECKS = "all-checks";

    private TestHost? host;

    private static string Asked => Environment.GetEnvironmentVariable(SCENE) is { Length: > 0 } asked ? asked.Trim() : "selector";

    private static bool Checking => Asked.Equals(ALL_CHECKS, StringComparison.OrdinalIgnoreCase) || Asked.StartsWith("check-", StringComparison.OrdinalIgnoreCase);

    public static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));

    public override GameEngineConfiguration Configure(IosDisplayInfo screen)
    {
        GameEngineConfiguration configuration = base.Configure(screen);

        // The process starts in the bundle, which is read only. Documents instead, the way the desktop's Program starts
        // in the exe's folder. Content comes out of the bundle and storage has full paths, neither leans on this
        if (IosBootstrap.Current is { } runtime) Environment.CurrentDirectory = runtime.Documents;

        // A run of the checks has nobody watching, so frames go as fast as the swapchain lets them
        if (!Checking) return configuration;
        return configuration with { WindowConfiguration = configuration.WindowConfiguration with { VSync = false } };
    }

    public override GameEngine MakeEngine(GameEngineConfiguration configuration)
    {
        GameEngine engine = base.MakeEngine(configuration);

        // On the engine itself and not in a scene, so they outlive every scene the host swaps between, as on a desktop
        host = engine.AddEntity(new TestHost(TestCatalog.Tests));
        engine.AddEntity(new Horizon.UI.PerformanceOverlay { Corner = Horizon.Rendering.Origin.BottomRight });

        if (!Checking)
        {
            Horizon.UI.Skinning.UISkins.Remember(PlatformStorage.Config("examples_skin.txt"));
            host.RememberRenderScale(PlatformStorage.Config("examples_render_scale.txt"));
        }

        return engine;
    }

    public override Scene MakeFirstScene()
    {
        TestHost testing = host ?? throw new InvalidOperationException("The engine was made without the test host, MakeEngine wasn't called first.");
        string id = Asked;

        if (id.Equals(ALL_CHECKS, StringComparison.OrdinalIgnoreCase))
        {
            testing.CheckTimeout = TimeSpan.FromSeconds(300);
            testing.ChecksDone += Write;
            Log.Info("[Testing] Running every check, back to the menu after.");
            testing.RunChecks();
            return testing.Selector;
        }

        if (!id.Equals("selector", StringComparison.OrdinalIgnoreCase))
        {
            if (TestCatalog.Find(id) is { } test)
            {
                Log.Info($"[Testing] Starting '{test.Id}'{(test.Unsupported is null ? "" : ", which can't run here and says why")}.");
                testing.Start(test);
                return testing.Selector;
            }

            Log.Error($"[Testing] There's no example called '{id}'. There are selector, {ALL_CHECKS}, {string.Join(", ", TestCatalog.Tests.Select(test => test.Id))}.");
        }

        Log.Info("[Testing] Starting in the selector.");
        return testing.Selector;
    }

    // The report of a run of every check, text to read and JSON for a script, in Documents/checks
    private static void Write(CheckReport report)
    {
        string folder = Path.Combine(IosBootstrap.Current?.Documents ?? Path.GetTempPath(), "checks");
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "full-report.txt"), report.ToText());
            using (var stream = File.Create(Path.Combine(folder, "full-report.json")))
                report.WriteJson(stream);

            Log.Write(report.Failed == 0 ? LogLevel.Success : LogLevel.Error,
                $"[Checks] Every check, {report.Passed} of {report.Count} passed, {report.Failed} failed, {report.Skipped} skipped, report in {folder}.");
        }
        catch (Exception e)
        {
            Log.Error($"[Checks] The full report couldn't be written to {folder}. {e.Message}");
        }
    }
}
