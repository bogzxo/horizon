using System.Numerics;

using Horizon.Input;

namespace Horizon.Platform;

/// <summary>
/// The keys, the pointer and the text of a platform, as events in the order they happened (see <see cref="IInputSource"/>),
/// plus the few things that are asked of the devices themselves. The host owns the subscriptions and lets go of them
/// when it is disposed, whoever listens only listens.
/// </summary>
public interface IPlatformInput : IInputSource
{
    /// <summary>Where the pointer is right now in the pixels that are drawn into, for the mouse to start out there.</summary>
    Vector2 PointerPosition { get; }

    /// <summary>Locks the pointer to the window and hides it, raw where the system has that, or lets it go. Platform thread.</summary>
    void SetPointerCaptured(bool captured);

    /// <summary>
    /// What is on the clipboard as text, empty when nothing or something else. Platform thread only, the thread that
    /// raises <see cref="IPlatformEvents.EventsProcessed"/>, which is where the UI reads and writes it.
    /// </summary>
    string ClipboardText { get; set; }

    /// <summary>
    /// Opens the controllers plugged into the machine, null where none are read. Each one opened is a feed of its own,
    /// sampled by the host on its thread once it is started and until it is disposed.
    /// </summary>
    IPlatformGamepads? OpenGamepads();

    /// <summary>
    /// Puts the platform's soft keyboard (and its IME) up while something that takes text has a focus a finger gave
    /// it, or takes it down again. What it types comes back as text and key events like a real keyboard's. Nothing
    /// where the keyboard is a real one (a desktop). From any thread, done on the platform's.
    /// </summary>
    void SetTextInputActive(bool active) { }
}

/// <summary>
/// Controllers coming and going and what they hold, published as events. Nothing is heard until <see cref="Start"/>,
/// so whoever listens can have something claim the first slot before the first real one turns up (a script's pad).
/// </summary>
public interface IPlatformGamepads : IInputSource, IDisposable
{
    /// <summary>Starts listening, on the platform's thread once it gets there. From any thread.</summary>
    void Start();
}
