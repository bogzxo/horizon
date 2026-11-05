using Android.Views;

using Horizon.Logging;

namespace Horizon.Platform.Android;

// Input as the activity is handed it, before any view sees it, and Back. Back is taken with nothing but the platform,
// no AndroidX for one handler. From API 33 an OnBackInvokedCallback is registered on the activity's own
// OnBackInvokedDispatcher (the gesture and the button both reach it, with android:enableOnBackInvokedCallback on in the
// manifest), before that OnBackPressed is overridden, and the Back key itself is taken in dispatchKeyEvent on every
// version. A press that comes both ways is heard once.
public abstract partial class HorizonActivity
{
    // Two reports of one press of Back closer than this are one
    private const long BACK_REPEAT_MILLISECONDS = 250;

    private BackCallback? back;
    private long lastBack;

    /// <summary>
    /// Whether Back finishes the activity when nothing in the game takes it. Off, which is a game's usual wish (Back
    /// goes to the menu as Escape does on a desktop). A game turns it on, or calls <see cref="RequestQuit"/> itself
    /// when its main menu hears Escape.
    /// </summary>
    protected virtual bool BackQuits => false;

    /// <summary>
    /// Back was pressed or swiped. Escape pressed and let go of by default, which Horizon.UI's navigator and a game's
    /// pause menu take as going back, or quitting when <see cref="BackQuits"/> says so. Main thread.
    /// </summary>
    protected virtual void OnBackRequested()
    {
        if (BackQuits)
        {
            RequestQuit();
            return;
        }

        Session?.Host?.Input.Tap(Horizon.Input.Key.Escape);
    }

    private void BackHeard()
    {
        long now = Environment.TickCount64;
        if (now - lastBack < BACK_REPEAT_MILLISECONDS) return;
        lastBack = now;
        OnBackRequested();
    }

    public override bool DispatchTouchEvent(MotionEvent? e)
    {
        if (e is not null && Session?.Host is { } host && Session.Engine is not null) return host.Input.OnTouch(e);
        return base.DispatchTouchEvent(e);
    }

    public override bool DispatchGenericMotionEvent(MotionEvent? e)
    {
        if (e is not null && Session?.Host is { } host && Session.Engine is not null && host.Input.OnGeneric(e)) return true;
        return base.DispatchGenericMotionEvent(e);
    }

    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e is null) return base.DispatchKeyEvent(e);
        AndroidHost? host = Session?.Engine is not null ? Session.Host : null;

        // A pad's buttons first, a pad's Back is its Back button and not the app's
        if (host is not null && IsFromPad(e) && host.Input.OnKey(e)) return true;

        if (e.KeyCode == Keycode.Back)
        {
            // Back goes on its way up, the way the system has it, and a cancelled one (a long press taken over) not at all
            if (e.Action == KeyEventActions.Up && !e.IsCanceled) BackHeard();
            return true;
        }

        if (host is not null && host.Input.OnKey(e)) return true;
        return base.DispatchKeyEvent(e);
    }

#pragma warning disable CS0672, CA1422 // the only way to hear Back before API 33, and never called after it with the callback registered
    public override void OnBackPressed() => BackHeard();
#pragma warning restore CS0672, CA1422

    private static bool IsFromPad(KeyEvent e) =>
        e.IsFromSource(InputSourceType.Gamepad) || e.IsFromSource(InputSourceType.Joystick) || KeyEvent.IsGamepadButton(e.KeyCode);

    private void ListenForBack()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33) || back is not null) return;
        back = new BackCallback(this);

        // OnBackInvokedDispatcher.PRIORITY_DEFAULT is 0
        OnBackInvokedDispatcher.RegisterOnBackInvokedCallback(0, back);
        Log.Info("[Android] Back is heard through OnBackInvokedDispatcher.");
    }

    private void StopListeningForBack()
    {
        if (back is null) return;
        if (OperatingSystem.IsAndroidVersionAtLeast(33)) OnBackInvokedDispatcher.UnregisterOnBackInvokedCallback(back);
        back.Dispose();
        back = null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android33.0")]
    private sealed class BackCallback(HorizonActivity activity) : Java.Lang.Object, global::Android.Window.IOnBackInvokedCallback
    {
        public void OnBackInvoked() => activity.BackHeard();
    }
}
