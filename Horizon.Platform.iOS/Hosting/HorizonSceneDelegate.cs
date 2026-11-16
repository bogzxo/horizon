using Foundation;

using UIKit;

namespace Horizon.Platform.iOS;

/// <summary>
/// The app's one window scene, which is where iOS 27 shows an app at all, it won't put up a window an app delegate made
/// itself. Named in Info.plist's UIApplicationSceneManifest as HorizonSceneDelegate. It hands its scene to the app
/// delegate for the window and passes what happens to the scene on to the session, in front, behind, in the
/// background, gone.
/// <para>
/// It says it's an IUIWindowSceneDelegate as well as deriving from the model class. NativeAOT's static registrar adds
/// the protocol either way, the one a CoreCLR build runs with didn't, and UIKit throws on a scene delegate class that
/// doesn't conform to UISceneDelegate before the app has done a thing.
/// </para>
/// </summary>
[Register("HorizonSceneDelegate")]
public sealed class HorizonSceneDelegate : UIWindowSceneDelegate, IUIWindowSceneDelegate
{
    public override UIWindow? Window { get; set; }

    private static HorizonAppDelegate? App => UIApplication.SharedApplication.Delegate as HorizonAppDelegate;

    public override void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        if (scene is not UIWindowScene windowScene) return;
        Window = App?.MakeWindow(windowScene) ?? new UIWindow(windowScene);
        Window.MakeKeyAndVisible();
    }

    public override void DidBecomeActive(UIScene scene) => IosSession.Current?.Activated();

    public override void WillResignActive(UIScene scene) => IosSession.Current?.Deactivated();

    public override void DidEnterBackground(UIScene scene) => IosSession.Current?.EnteredBackground();

    public override void WillEnterForeground(UIScene scene) => IosSession.Current?.EnteringForeground();

    // The system let go of the scene (swiped away in the switcher), which is as good as the app ending for a game
    public override void DidDisconnect(UIScene scene) => IosSession.Current?.Terminating();
}
