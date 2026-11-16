using Foundation;

using Horizon.Engine;
using Horizon.Logging;

using UIKit;

namespace Horizon.Platform.iOS;

/// <summary>
/// The app delegate a game on an iPhone derives from. It sets the process up (the console and debug.log, the run's
/// HORIZON_* settings, the bundle as content, the app's folders as storage), puts a fullscreen Metal view up and hands
/// its layer to the engine, which it makes and starts the first time the view is laid out. Everything after that is
/// UIKit calling back, touches, keys, the app going to the background and coming to the front.
/// <code>
/// [Register("AppDelegate")]
/// public sealed class AppDelegate : HorizonAppDelegate
/// {
///     public override Scene MakeFirstScene() => new MainMenuScene();
/// }
///
/// UIApplication.Main(args, null, typeof(AppDelegate));
/// </code>
/// The app's Info.plist needs a UIApplicationSceneManifest whose one window scene has HorizonSceneDelegate as its
/// delegate, see Horizon.Testing.iOS's. The lifecycle comes through the scene, not through here.
/// </summary>
public abstract class HorizonAppDelegate : UIApplicationDelegate
{
    private HorizonViewController? controller;
    private Exception? setupFailure;

    public override UIWindow? Window { get; set; }

    /// <summary>The process's session, there from FinishedLaunching on unless setting the process up failed.</summary>
    protected IosSession? Session { get; private set; }

    /// <summary>How the engine is made, a window the size of the view as it was when the app came up. Called once a process.</summary>
    public virtual GameEngineConfiguration Configure(IosDisplayInfo screen) => new()
    {
        WindowConfiguration = Horizon.Core.WindowManagerConfiguration.Default1600x900 with
        {
            WindowSize = screen.ViewPixels,
            WindowTitle = NSBundle.MainBundle.ObjectForInfoDictionary("CFBundleDisplayName")?.ToString() ?? "Horizon",
            Fullscreen = true,
            VSync = true
        }
    };

    /// <summary>Makes the engine out of the configuration, whose platform is set to iOS. Called once a process.</summary>
    public virtual GameEngine MakeEngine(GameEngineConfiguration configuration) => new(configuration);

    /// <summary>The scene the engine starts in. Called once a process, right before the engine starts.</summary>
    public abstract Scene MakeFirstScene();

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        // Only the process here, the window comes with the scene (HorizonSceneDelegate), which is the only way iOS 27
        // shows an app at all
        try
        {
            Session = IosSession.For(this, IosBootstrap.Start());
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Horizon] The app couldn't be set up. {e}");
            setupFailure = e;
        }
        return true;
    }

    /// <summary>The scene that came is for the one scene the app has, Info.plist names its delegate.</summary>
    public override UISceneConfiguration GetConfiguration(UIApplication application, UISceneSession connectingSceneSession, UISceneConnectionOptions options) =>
        new("Default Configuration", connectingSceneSession.Role) { DelegateType = typeof(HorizonSceneDelegate) };

    /// <summary>The window for the scene, with the Metal view in it, or what went wrong setting up. Main thread.</summary>
    internal UIWindow MakeWindow(UIWindowScene scene)
    {
        var window = new UIWindow(scene);
        if (Session is null)
        {
            window.RootViewController = new UIViewController();
            Exception e = setupFailure ?? new InvalidOperationException("The session was never made.");
            ShowText(window.RootViewController.View!, $"The app couldn't be set up.\n\n{e.GetType().Name}: {e.Message}");
            return window;
        }

        controller = new HorizonViewController(Session);
        window.RootViewController = controller;
        Window = window;
        return window;
    }

    public override void ReceiveMemoryWarning(UIApplication application) =>
        Log.Warning("[iOS] The system is short of memory.");

    /// <summary>Shows what went wrong over the view. Main thread.</summary>
    internal void ShowFailure(string what, Exception e) =>
        controller?.ShowMessage($"{what}\n\n{e.GetType().Name}: {e.Message}\n\nThe whole story is in Documents/debug.log.");

    /// <summary>The game quit. iOS apps don't close themselves, so it says so and waits to be swiped away. Main thread.</summary>
    internal void ShowStopped() => controller?.ShowMessage("The game has stopped.");

    private static void ShowText(UIView view, string text) => view.AddSubview(new UILabel(view.Bounds.Inset(24, 48))
    {
        Text = text,
        TextColor = UIColor.White,
        Lines = 0,
        TextAlignment = UITextAlignment.Center
    });
}
