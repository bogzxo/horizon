using Android.App;
using Android.Content.PM;

using Horizon.Engine;
using Horizon.Platform;
using Horizon.Platform.Android;

namespace Horizon.Testing.Android;

/// <summary>
/// Horizon.Testing on a phone. Everything Android is HorizonActivity's. This puts the same <see cref="TestHost"/> and
/// performance overlay on the engine that the desktop's Program does, and <see cref="TestingScenes"/> says where to
/// start, see README.md for how a run is picked and driven over adb.
/// </summary>
[Activity(
    Name = "dev.horizon.testing.TestingActivity",
    Label = "Horizon Testing",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    ScreenOrientation = ScreenOrientation.FullUser,
    ResizeableActivity = true,
    Theme = HorizonActivity.THEME,
    ConfigurationChanges = HorizonActivity.HANDLED_CHANGES)]
public sealed class TestingActivity : HorizonActivity
{
    private TestHost? host;

    public override GameEngineConfiguration Configure()
    {
        GameEngineConfiguration configuration = base.Configure();

        // The process starts in /, which an app may not even list. The app's own files folder instead, the way the
        // desktop's Program starts in the exe's folder, so the terminal example's shell comes up somewhere it can look
        // round. Content comes out of the APK and storage has full paths, neither leans on this
        if (FilesDir?.AbsolutePath is { } files) Environment.CurrentDirectory = files;

        // A run of the checks has nobody watching, so frames go as fast as the swapchain lets them the way the desktop's
        // --checks turns VSync off, and the updates stay at 120, a phone's CPU has less to spare than a desktop's
        if (!TestingScenes.RunsCatalogChecks) return configuration;
        return configuration with { WindowConfiguration = configuration.WindowConfiguration with { VSync = false } };
    }

    public override GameEngine MakeEngine(GameEngineConfiguration configuration)
    {
        GameEngine engine = base.MakeEngine(configuration);

        // On the engine itself and not in a scene, so they outlive every scene the host swaps between, as on a desktop
        host = engine.AddEntity(new TestHost(TestCatalog.Tests));
        engine.AddEntity(new Horizon.UI.PerformanceOverlay { Corner = Horizon.Rendering.Origin.BottomRight });

        // The skin the examples were left in, in the app's own config folder. Not for a run of the checks
        if (!TestingScenes.Checking)
        {
            Horizon.UI.Skinning.UISkins.Remember(PlatformStorage.Config("examples_skin.txt"));
            host.RememberRenderScale(PlatformStorage.Config("examples_render_scale.txt"));
        }

        return engine;
    }

    public override Scene MakeFirstScene() =>
        TestingScenes.Make(host ?? throw new InvalidOperationException("The engine was made without the test host, MakeEngine wasn't called first."));
}
