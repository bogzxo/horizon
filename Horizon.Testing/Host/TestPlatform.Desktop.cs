using Horizon.Engine;
using Horizon.Testing.Examples.Internals;

namespace Horizon.Testing;

/// <summary>
/// What the catalog and the host need to know about the platform they're built for, the desktop's half. The Android
/// head has a file of its own with the same members (Horizon.Testing.Android/Host/TestPlatform.Android.cs) and leaves
/// this one out, which is how the terminal stays on a desktop without a single #if in the catalog.
/// </summary>
internal static class TestPlatform
{
    /// <summary>What the platform is called in a report.</summary>
    public const string NAME = "desktop";

    /// <summary>Examples and checks only this platform has, put after the shared ones.</summary>
    public static TestDefinition[] Extra => [];

    /// <summary>The terminal example. A real shell needs a pty, which only the desktop platform has.</summary>
    public static Func<Scene> Terminal => () => new TerminalExample();

    /// <summary>Why the terminal can't run here, null on a desktop.</summary>
    public static string? TerminalUnsupported => null;

    /// <summary>How to start straight in an example, said under its details in the selector.</summary>
    public static string HowToStart(string id) => $"Start straight in it with   Horizon.Testing {id}";
}
