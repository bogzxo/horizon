using System.Diagnostics;
using System.Numerics;

using CoreGraphics;

using Foundation;

using Horizon.Input;

using UIKit;

using InputEvent = Horizon.Input.InputEvent;

namespace Horizon.Platform.iOS;

/// <summary>
/// The touches, the keys and the text of an iPhone or an iPad, as the engine's events in the order UIKit delivered
/// them. The view and its controller hand over every touch and press on the main thread, which is the platform's
/// thread. Text comes off a hardware keyboard's presses, and from the soft keyboard through the view while the UI asks
/// for it. Controllers come through GameController, see IosGamepads.
/// </summary>
public sealed class IosInput : IPlatformInput
{
    private readonly IosHost host;
    private readonly IosPointers pointers;
    private readonly HashSet<Key> held = [];
    private readonly List<IosGamepads> gamepads = [];
    private bool textActive;

    public event Action<InputEvent>? EventReceived;

    internal IosInput(IosHost host)
    {
        this.host = host;
        pointers = new IosPointers(Publish, host.ToPixels);
    }

    public Vector2 PointerPosition => pointers.MousePosition;

    /// <summary>How many fingers are down right now, for a check.</summary>
    public int Contacts => pointers.ContactCount;

    /// <summary>Whether the primary finger also drives the mouse, see IosPointers. Off, the UI and the game read fingers as fingers.</summary>
    public bool MirrorPrimaryToMouse
    {
        get => pointers.MirrorPrimaryToMouse;
        set => pointers.MirrorPrimaryToMouse = value;
    }

    /// <summary>A phone has no pointer to lock, PlatformFeatures says so and this does nothing.</summary>
    public void SetPointerCaptured(bool captured)
    {
    }

    /// <summary>The general pasteboard's text. Main thread, which is the platform's.</summary>
    public string ClipboardText
    {
        get => UIPasteboard.General.String ?? string.Empty;
        set => UIPasteboard.General.String = value ?? string.Empty;
    }

    /// <summary>How many pads are connected, for a check.</summary>
    public int Gamepads => gamepads.Sum(feed => feed.Count);

    public IPlatformGamepads? OpenGamepads()
    {
        if (!host.ReadsGamepads) return null;

        // Asked on the render thread as the engine sets up, the list is the main thread's
        var feed = new IosGamepads(host);
        host.Post(() => gamepads.Add(feed));
        return feed;
    }

    /// <summary>Touches of the view in one of their phases. Main thread.</summary>
    internal void Touches(UIView view, NSSet touches, IosPointers.Phase phase)
    {
        pointers.Touches(view, touches, phase);
        host.HeardInput();
    }

    /// <summary>A trackpad or mouse moving over the view, in points. Main thread.</summary>
    internal void Hover(CGPoint point)
    {
        pointers.Hover(point);
        host.HeardInput();
    }

    /// <summary>
    /// A hardware keyboard's presses, down or up (an up is also how a cancel comes). False when none of them were keys
    /// of ours, which goes on up the responder chain to the system. Main thread.
    /// </summary>
    internal bool Presses(NSSet<UIPress> presses, bool down)
    {
        bool handled = false;
        foreach (UIPress press in presses.ToArray())
        {
            if (press.Key is not { } native) continue;

            Key? mapped = IosKeys.Of(native.KeyCode);
            KeyModifiers modifiers = IosKeys.Modifiers(native.ModifierFlags);
            long stamp = IosPointers.Stamp(press.Timestamp);

            if (mapped is { } key)
            {
                handled = true;
                if (down && held.Add(key))
                {
                    // The clipboard belongs to this exact paste, read here on the main thread the way a desktop's is
                    InputEvent change = Change(key, true, modifiers, stamp);
                    if (key == Key.V && (modifiers & (KeyModifiers.Control | KeyModifiers.Super)) != 0) change = change with { ClipboardText = ClipboardText };
                    Publish(change);
                }
                else if (!down && held.Remove(key)) Publish(Change(key, false, modifiers, stamp));
            }

            // Text from the keys themselves, unless the view is taking text, which has it come through insertText
            if (down && !textActive && (modifiers & (KeyModifiers.Control | KeyModifiers.Super)) == 0 && Printable(native.Characters) is { } text)
            {
                handled = true;
                Publish(InputEvent.Typed(text) with { Timestamp = stamp });
            }
        }

        if (handled) host.HeardInput();
        return handled;
    }

    /// <summary>
    /// Everything held let go of, fingers cancelled and keys up. For the app losing focus or going to the background,
    /// the ends would never come. Main thread.
    /// </summary>
    public void CancelAll()
    {
        pointers.CancelAll();

        long stamp = Stopwatch.GetTimestamp();
        foreach (Key key in held) Publish(InputEvent.KeyChange(key, false) with { Timestamp = stamp, Modifiers = KeyModifiers.None, HasModifierSnapshot = true });
        held.Clear();

        foreach (IosGamepads feed in gamepads) feed.ReleaseAll();
    }

    /// <summary>The soft keyboard up or away, on the main thread whichever thread asks (the UI's is the simulation's).</summary>
    public void SetTextInputActive(bool active) => host.Post(() =>
    {
        textActive = active;
        HorizonMetalView.Current?.SetTextActive(active);
    });

    /// <summary>Text the soft keyboard (or a hardware one while the view takes text) typed. Main thread.</summary>
    internal void TypeFromKeyboard(string text)
    {
        if (text.Length == 0) return;

        // Return comes as a line break, which the UI wants as Enter
        if (text == "\n")
        {
            Tap(Key.Enter);
            return;
        }

        Publish(InputEvent.Typed(text));
        host.HeardInput();
    }

    /// <summary>Has a key pressed and let go of at once, for the soft keyboard's Backspace. Main thread.</summary>
    public void Tap(Key key)
    {
        long stamp = Stopwatch.GetTimestamp();
        Publish(InputEvent.KeyChange(key, true) with { Timestamp = stamp, HasModifierSnapshot = true });
        Publish(InputEvent.KeyChange(key, false) with { Timestamp = stamp, HasModifierSnapshot = true });
        host.HeardInput();
    }

    // Characters that are text, not a control key's (Return, Tab, Escape, the arrows' private use characters)
    private static string? Printable(string? characters)
    {
        if (string.IsNullOrEmpty(characters)) return null;
        foreach (char c in characters)
        {
            if (char.IsControl(c) || c is >= '' and <= '') return null;
        }
        return characters;
    }

    private static InputEvent Change(Key key, bool down, KeyModifiers modifiers, long stamp) =>
        InputEvent.KeyChange(key, down) with { Timestamp = stamp, Modifiers = modifiers, HasModifierSnapshot = true };

    private void Publish(InputEvent input) => EventReceived?.Invoke(input);

    /// <summary>The host is going, every feed stops and nobody listens any more. Main thread.</summary>
    internal void Close()
    {
        foreach (IosGamepads feed in gamepads) feed.Dispose();
        gamepads.Clear();
        EventReceived = null;
    }
}
