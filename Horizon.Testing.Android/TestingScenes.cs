using Horizon.Engine;
using Horizon.Logging;
using Horizon.Platform.Android;

namespace Horizon.Testing.Android;

/// <summary>
/// What the APK starts in, picked with the intent extra HORIZON_TESTING_SCENE (<c>--es HORIZON_TESTING_SCENE shapes</c>)
/// or a line of horizon.env. The same host and catalog as the desktop's Horizon.Testing, so every id the desktop knows
/// works here too, and there are three more.
/// <list type="bullet">
/// <item><c>selector</c>, the default, the menu of every example, the same as the desktop with no arguments.</item>
/// <item><c>all-checks</c>, every check of the catalog one after the other, the desktop's <c>--checks</c>, with the
/// device's own checks last and the report in checks/full-report.txt and .json.</item>
/// <item><c>checks</c>, the device checks on their own, the way the APK always had them, report in checks/report.txt.</item>
/// </list>
/// </summary>
internal static class TestingScenes
{
    /// <summary>The setting that picks where to start, put in the environment from the intent's extras or horizon.env.</summary>
    public const string VARIABLE = "HORIZON_TESTING_SCENE";

    /// <summary>With all-checks or checks, quits once the report is written, the way --checks does on a desktop.</summary>
    public const string QUIT_VARIABLE = "HORIZON_TESTING_QUIT";

    /// <summary>How long one check scene gets in a run of every check before it counts as hung, in seconds.</summary>
    public const string TIMEOUT_VARIABLE = "HORIZON_TESTING_CHECK_TIMEOUT";

    public const string SELECTOR = "selector";
    public const string ALL_CHECKS = "all-checks";
    public const string DEVICE_CHECKS = "checks";

    private const double DEFAULT_TIMEOUT_SECONDS = 300.0;

    /// <summary>What was asked for, the selector when nothing was.</summary>
    public static string Asked => Environment.GetEnvironmentVariable(VARIABLE) is { Length: > 0 } asked ? asked.Trim() : SELECTOR;

    /// <summary>Whether this run is one that checks, which wants the same skin every time and no remembered one.</summary>
    public static bool Checking => Asked.Equals(ALL_CHECKS, StringComparison.OrdinalIgnoreCase) || Asked.StartsWith("check", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this run is the catalog's checks, every one or one by its id, the runs a desktop makes with VSync off.</summary>
    public static bool RunsCatalogChecks => Asked.Equals(ALL_CHECKS, StringComparison.OrdinalIgnoreCase) || Asked.StartsWith("check-", StringComparison.OrdinalIgnoreCase);

    private static bool Quits => string.Equals(Environment.GetEnvironmentVariable(QUIT_VARIABLE), "on", StringComparison.OrdinalIgnoreCase);

    /// <summary>Has the host start what was asked for, and gives the scene the engine starts in.</summary>
    public static Scene Make(TestHost host)
    {
        string id = Asked;

        if (id.Equals(DEVICE_CHECKS, StringComparison.OrdinalIgnoreCase))
        {
            Log.Info("[Testing] Starting the device checks on their own.");
            return new DeviceChecksScene();
        }

        if (id.Equals(ALL_CHECKS, StringComparison.OrdinalIgnoreCase))
        {
            double seconds = double.TryParse(Environment.GetEnvironmentVariable(TIMEOUT_VARIABLE), System.Globalization.CultureInfo.InvariantCulture, out double asked) && asked > 0 ? asked : DEFAULT_TIMEOUT_SECONDS;
            host.CheckTimeout = TimeSpan.FromSeconds(seconds);
            host.ExitAfterChecks = Quits;
            host.ChecksDone += Write;
            Log.Info($"[Testing] Running every check, {host.CheckTimeout.Value.TotalSeconds:0} s at most each, {(Quits ? "quitting" : "back to the menu")} after.");
            host.RunChecks();
            return host.Selector;
        }

        if (!id.Equals(SELECTOR, StringComparison.OrdinalIgnoreCase))
        {
            if (TestCatalog.Find(id) is { } test)
            {
                Log.Info($"[Testing] Starting '{test.Id}'{(test.Unsupported is null ? "" : ", which can't run here and says why")}.");
                host.Start(test);
                return host.Selector;
            }

            Log.Error($"[Testing] There's no example called '{id}'. There are {SELECTOR}, {ALL_CHECKS}, {DEVICE_CHECKS}, {string.Join(", ", TestCatalog.Tests.Select(test => test.Id))}.");
        }

        Log.Info("[Testing] Starting in the selector.");
        return host.Selector;
    }

    // The report of a run of every check, text to read and JSON for a script, next to the device checks' own report
    private static void Write(CheckReport report)
    {
        string folder = Path.Combine(AndroidSession.Current?.Runtime.Paths.External ?? Path.GetTempPath(), "checks");
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
