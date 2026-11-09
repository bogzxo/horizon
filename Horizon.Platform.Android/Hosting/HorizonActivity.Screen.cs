using Android.OS;
using Android.Views;
using Android.Widget;

namespace Horizon.Platform.Android;

// Fullscreen and immersive, the screen kept on, and what's shown instead of a crash when the engine can't run
public abstract partial class HorizonActivity
{
    // WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE, the bars come back for a moment on a swipe and go again
    private const int SHOW_BARS_BY_SWIPE = 2;

    /// <summary>The system bars hidden and the picture under the cutout too. Again every time focus comes back.</summary>
    protected void GoFullscreen()
    {
        if (Window is not { } window) return;
        window.AddFlags(WindowManagerFlags.KeepScreenOn);

        if (OperatingSystem.IsAndroidVersionAtLeast(28))
        {
            WindowManagerLayoutParams? attributes = window.Attributes;
            if (attributes is not null)
            {
                attributes.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
                window.Attributes = attributes;
            }
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            window.SetDecorFitsSystemWindows(false);
            if (window.InsetsController is { } insets)
            {
                insets.Hide(WindowInsets.Type.SystemBars());
                insets.SystemBarsBehavior = SHOW_BARS_BY_SWIPE;
            }
        }
        else
        {
#pragma warning disable CA1422, CS0618 // the flags are how it was done before API 30
            window.DecorView.SystemUiVisibility = (StatusBarVisibility)(SystemUiFlags.ImmersiveSticky | SystemUiFlags.Fullscreen |
                SystemUiFlags.HideNavigation | SystemUiFlags.LayoutStable | SystemUiFlags.LayoutHideNavigation | SystemUiFlags.LayoutFullscreen);
#pragma warning restore CA1422, CS0618
        }
    }

    /// <summary>
    /// Puts what went wrong on the screen in place of the game, for an engine that couldn't start or stopped with an
    /// error (a GPU without what the engine needs, a shader missing from the pack). It stays up until the player leaves,
    /// and it's in the log and debug.log as well. Main thread.
    /// </summary>
    internal void ShowFailure(string what, Exception error)
    {
        try
        {
            string reason = error.GetBaseException().Message;
            string files = Session?.Runtime.Logger.File is { Length: > 0 } log ? log : "logcat (adb logcat -s Horizon)";
            var text = new TextView(this)
            {
                Text = $"{what}\n\n{reason}\n\nDevice {Build.Manufacturer} {Build.Model}, Android {Build.VERSION.Release} (API {(int)Build.VERSION.SdkInt}).\n" +
                    $"GPU {AndroidSession.DescribeGraphics() ?? "not made"}.\n\nThe whole story is in {files}.\n\n{error}",
                TextSize = 15.0f
            };
            text.SetTextColor(global::Android.Graphics.Color.White);
            text.SetPadding(48, 48, 48, 48);
            text.SetTextIsSelectable(true);

            var scroll = new ScrollView(this);
            scroll.SetBackgroundColor(global::Android.Graphics.Color.Rgb(40, 16, 16));
            scroll.AddView(text);
            SetContentView(scroll);
        }
        catch (Exception again)
        {
            Horizon.Logging.Log.Error($"[Android] The failure couldn't be shown either. {again.Message}");
        }
    }
}
