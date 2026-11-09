using System.Numerics;

using Android.Content;
using Android.Views;

using Horizon.Input;

using InputEvent = Horizon.Input.InputEvent;

namespace Horizon.Platform.Android;

/// <summary>
/// The keys, the pointers and the text of a phone, as the engine's events in the order Android delivered them. The
/// activity hands over every key, touch and generic motion event it's given on the main thread, which is the platform's
/// thread, and this sorts them into a pad's, the mouse's, a finger's or the keyboard's. Text comes off the hardware
/// keyboard's key events, and from the soft keyboard through <see cref="HorizonSurfaceView"/> while the UI asks for it.
/// </summary>
public sealed class AndroidInput : IPlatformInput
{
    private readonly AndroidHost host;
    private readonly AndroidPointers pointers;
    private readonly HashSet<Key> held = [];
    private readonly List<AndroidGamepads> gamepads = [];

    public event Action<InputEvent>? EventReceived;

    internal AndroidInput(AndroidHost host)
    {
        this.host = host;
        pointers = new AndroidPointers(Publish, host.ToPixels);
    }

    public Vector2 PointerPosition => pointers.MousePosition;

    /// <summary>How many fingers are down right now, for a check.</summary>
    public int Contacts => pointers.ContactCount;

    /// <summary>How many pads are connected, for a check.</summary>
    public int Gamepads => gamepads.Sum(feed => feed.Count);

    /// <summary>Whether the primary finger also drives the mouse, see AndroidPointers. Off, the UI and the game read fingers as fingers.</summary>
    public bool MirrorPrimaryToMouse
    {
        get => pointers.MirrorPrimaryToMouse;
        set => pointers.MirrorPrimaryToMouse = value;
    }

    /// <summary>A phone has no pointer to lock, PlatformFeatures says so and this does nothing.</summary>
    public void SetPointerCaptured(bool captured)
    {
    }

    /// <summary>The clipboard's text, through ClipboardManager. Main thread, which is the platform's.</summary>
    public string ClipboardText
    {
        get
        {
            if (host.Context.GetSystemService(Context.ClipboardService) is not ClipboardManager clipboard) return string.Empty;
            ClipData? clip = clipboard.PrimaryClip;
            if (clip is null || clip.ItemCount == 0) return string.Empty;
            return clip.GetItemAt(0)?.CoerceToText(host.Context)?.ToString() ?? string.Empty;
        }
        set
        {
            if (host.Context.GetSystemService(Context.ClipboardService) is ClipboardManager clipboard)
                clipboard.PrimaryClip = ClipData.NewPlainText("Horizon", value ?? string.Empty);
        }
    }

    public IPlatformGamepads? OpenGamepads()
    {
        if (!host.ReadsGamepads) return null;
        // Asked on the render thread as the engine sets up, the list is the main thread's, which goes through it on every key
        var feed = new AndroidGamepads(host);
        host.Post(() => gamepads.Add(feed));
        return feed;
    }

    /// <summary>A touch event of the activity. Main thread.</summary>
    public bool OnTouch(MotionEvent motion)
    {
        bool handled = pointers.Touch(motion);
        host.HeardInput();
        return handled;
    }

    /// <summary>A generic motion event, a pad's sticks or a mouse that isn't pressing. Main thread.</summary>
    public bool OnGeneric(MotionEvent motion)
    {
        foreach (AndroidGamepads feed in gamepads)
        {
            if (feed.OnMotion(motion)) return true;
        }

        bool handled = pointers.Generic(motion);
        if (handled) host.HeardInput();
        return handled;
    }

    /// <summary>
    /// A key event of the activity, false for one that isn't ours and goes on to the system (volume, power). Back is
    /// the activity's to decide, it comes here only from a pad. Main thread.
    /// </summary>
    public bool OnKey(KeyEvent key)
    {
        foreach (AndroidGamepads feed in gamepads)
        {
            if (feed.OnKey(key)) return true;
        }

        // Text that has no key of its own, a character from a keyboard's compose or a paste of the system
        if (key.Action == KeyEventActions.Multiple && key.KeyCode == Keycode.Unknown)
        {
            if (key.Characters is { Length: > 0 } characters) Publish(InputEvent.Typed(characters) with { Timestamp = AndroidPointers.Stamp(key.EventTime) });
            host.HeardInput();
            return true;
        }

        Key? mapped = AndroidKeys.Of(key.KeyCode);
        string? text = key.Action == KeyEventActions.Down ? AndroidKeys.Text(key) : null;
        if (mapped is null && text is null) return false;

        long stamp = AndroidPointers.Stamp(key.EventTime);
        if (mapped is { } engineKey)
        {
            if (key.Action == KeyEventActions.Down && key.RepeatCount == 0 && held.Add(engineKey))
            {
                // The clipboard belongs to this exact Ctrl+V, read here on the main thread the way a desktop's is
                InputEvent down = Change(engineKey, true, key, stamp);
                if (engineKey == Key.V && key.IsCtrlPressed) down = down with { ClipboardText = ClipboardText };
                Publish(down);
            }
            else if (key.Action == KeyEventActions.Up && held.Remove(engineKey))
                Publish(Change(engineKey, false, key, stamp));
        }

        // Typed text repeats with the key held, the way a desktop's does
        if (text is not null) Publish(InputEvent.Typed(text) with { Timestamp = stamp });
        host.HeardInput();
        return true;
    }

    /// <summary>
    /// Everything held let go of, fingers cancelled, keys up and pads at rest. For focus going or the app going to the
    /// background, the ups would never come otherwise. Main thread.
    /// </summary>
    public void CancelAll()
    {
        pointers.CancelAll();

        long stamp = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (Key key in held) Publish(InputEvent.KeyChange(key, false) with { Timestamp = stamp, Modifiers = KeyModifiers.None, HasModifierSnapshot = true });
        held.Clear();

        foreach (AndroidGamepads feed in gamepads) feed.ReleaseAll();
    }

    /// <summary>The soft keyboard up or away, on the main thread whichever thread asks (the UI's is the simulation's).</summary>
    public void SetTextInputActive(bool active) => host.Post(() => HorizonSurfaceView.Current?.SetTextActive(active));

    /// <summary>Text the soft keyboard committed. Main thread.</summary>
    internal void TypeFromIme(string text)
    {
        if (text.Length == 0) return;
        Publish(InputEvent.Typed(text));
        host.HeardInput();
    }

    /// <summary>A key the soft keyboard pressed by itself (Backspace for a deletion, Enter for its action). Main thread.</summary>
    internal void TapFromIme(Key key) => Tap(key);

    /// <summary>Has a key pressed and let go of at once, for Back read as Escape. Main thread.</summary>
    public void Tap(Key key)
    {
        long stamp = System.Diagnostics.Stopwatch.GetTimestamp();
        Publish(InputEvent.KeyChange(key, true) with { Timestamp = stamp, HasModifierSnapshot = true });
        Publish(InputEvent.KeyChange(key, false) with { Timestamp = stamp, HasModifierSnapshot = true });
        host.HeardInput();
    }

    private static InputEvent Change(Key key, bool down, KeyEvent native, long stamp) =>
        InputEvent.KeyChange(key, down) with { Timestamp = stamp, Modifiers = AndroidKeys.Modifiers(native), HasModifierSnapshot = true };

    private void Publish(InputEvent input) => EventReceived?.Invoke(input);

    /// <summary>The host is going, every feed stops listening. Main thread.</summary>
    internal void Close()
    {
        foreach (AndroidGamepads feed in gamepads) feed.Dispose();
        gamepads.Clear();
        EventReceived = null;
    }
}
