using Horizon.Engine;

namespace Horizon.Testing;

/// <summary>
/// The iOS half of what the catalog and the host know about their platform, in place of
/// Horizon.Testing/Host/TestPlatform.Desktop.cs, which this project leaves out. Same members, other answers.
/// </summary>
internal static class TestPlatform
{
    /// <summary>What the platform is called in a report.</summary>
    public const string NAME = "ios";

    /// <summary>
    /// Why the terminal can't run here. An iOS app may not start another process at all (no fork, no posix_spawn
    /// outside the simulator), so there's no shell to put in it.
    /// </summary>
    public static string? TerminalUnsupported => "it needs a shell in a pseudo terminal, and an iOS app isn't allowed to start another process.";

    /// <summary>The terminal, a screen that says why it isn't here.</summary>
    public static Func<Scene> Terminal => () => new UnsupportedScene("A terminal", TerminalUnsupported!);

    /// <summary>No checks of the device's own yet, the catalog's are what an iPhone runs.</summary>
    public static TestDefinition[] Extra => [];

    /// <summary>How to start straight in an example, said under its details in the selector.</summary>
    public static string HowToStart(string id) => $"Start straight in it with   SIMCTL_CHILD_HORIZON_TESTING_SCENE={id} xcrun simctl launch booted dev.horizon.testing";
}
