using System.Drawing;

using Horizon.Core;

namespace Horizon.Platform;

/// <summary>
/// The window a desktop platform shows the game in, what the engine and the UI ask of it. Engine and BCL types only,
/// the native handle belongs on the platform's own concrete class. Everything is from any thread and done on the
/// platform's thread when it gets there, unless it says otherwise. A host without a window (a phone) has none of these
/// and says so in <see cref="IPlatformHost.Window"/> being null.
/// </summary>
public interface IPlatformWindow
{
    /// <summary>Changes what the title bar says.</summary>
    void SetTitle(string title);

    /// <summary>Whether the window fills the screen it is on (the room the taskbar leaves). From any thread.</summary>
    bool IsMaximized { get; }

    /// <summary>Puts the window down to the taskbar.</summary>
    void Minimize();

    /// <summary>Has the window fill the screen it is on, or go back to how it was.</summary>
    void ToggleMaximized();

    /// <summary>Starts moving the window with the pointer, for as long as the left button is held.</summary>
    void BeginMove();

    /// <summary>Starts dragging edges of the window with the pointer, for as long as the left button is held.</summary>
    void BeginResize(WindowEdges edges);

    /// <summary>Where the maximise button the UI draws is, in the window's pixels, empty for nowhere.</summary>
    RectangleF MaximizeButton { get; set; }

    /// <summary>Where the title bar the UI draws is and what on it isn't the bar, in the window's pixels.</summary>
    void SetDragArea(RectangleF bar, ReadOnlySpan<RectangleF> holes);

    /// <summary>Clips a window without the system's frame to rounded corners, in logical display units.</summary>
    void SetCornerRadius(float radius);

    /// <summary>Registers a key heard whatever window is in front, its id or -1 where there are no such keys.</summary>
    int RegisterHotKey(string shortcut);

    /// <summary>Lets go of a key registered with <see cref="RegisterHotKey"/>.</summary>
    void UnregisterHotKey(int id);

    /// <summary>Whether a registered key was pressed since it was last asked, once a press.</summary>
    bool TakeHotKey(int id);

    /// <summary>Whether the window is down from the top of the screen, as far as the last slide went.</summary>
    bool IsDropped { get; }

    /// <summary>The window slid down from the top of the screen, or back up out of sight, in so many seconds.</summary>
    void Drop(bool down, float share, float seconds);

    /// <summary>The window shown, back from the taskbar if it was down there, and in front.</summary>
    void BringToFront();

    /// <summary>
    /// Shows a window that was made hidden, once there's a picture in it. Platform thread, nothing for one that was
    /// shown all along.
    /// </summary>
    void Show();

    /// <summary>Makes the window fullscreen or the size it was given. Platform thread, the engine waits for it.</summary>
    void ApplyDisplay(DisplaySettings settings);
}
