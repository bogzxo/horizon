using Horizon.Engine;
using Horizon.Testing.Android;

namespace Horizon.Testing;

/// <summary>
/// The Android half of what the catalog and the host know about their platform, in place of
/// Horizon.Testing/Host/TestPlatform.Desktop.cs, which this project leaves out. Same members, other answers.
/// </summary>
internal static class TestPlatform
{
    /// <summary>What the platform is called in a report.</summary>
    public const string NAME = "android";

    /// <summary>
    /// Why the terminal can't run on this phone, null when it can. PseudoTerminal.Start (Horizon/Core/Processes) has a pty
    /// on Android from API 34, which is when bionic got posix_spawn_file_actions_addchdir_np, and runs /system/bin/sh
    /// there. Below 34 it throws before it makes anything, so the example isn't even started.
    /// </summary>
    public static string? TerminalUnsupported => OperatingSystem.IsAndroidVersionAtLeast(34) ? null :
        $"it needs a pseudo terminal, which Horizon has on Android from API 34 (posix_spawn_file_actions_addchdir_np in bionic), and this is API {Api}.";

    private static int Api => (int)global::Android.OS.Build.VERSION.SdkInt;

    /// <summary>The terminal, a real shell from API 34 and a screen that says why below it.</summary>
    public static Func<Scene> Terminal => TerminalUnsupported is { } why
        ? () => new UnsupportedScene("A terminal", why)
        : () => new Horizon.Testing.Examples.Internals.TerminalExample();

    /// <summary>The checks only a phone has, content out of the APK, the app's storage, AAudio, the SurfaceView and the GPU.</summary>
    public static TestDefinition[] Extra =>
    [
        new("check-device", TestLevel.Checks, "The device",
            "What only a phone can say. The APK's content is all there with the right hashes, storage commits and abandons, the speakers call back, the SurfaceView's surface follows the app and the GPU is drawing.",
            ["AndroidContent", "AndroidStorage", "AAudioOutput", "AndroidSurface", "GraphicsDevice"],
            "../Horizon.Testing.Android/Checks/DeviceChecksScene.cs", () => new DeviceChecksScene { QuitWhenDone = false }) { Aliases = ["device-checks"] },
    ];

    /// <summary>How to start straight in an example, said under its details in the selector.</summary>
    public static string HowToStart(string id) => $"Start straight in it with   adb shell am start -n dev.horizon.testing/dev.horizon.testing.TestingActivity --es HORIZON_TESTING_SCENE {id}";
}
