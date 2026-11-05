namespace Horizon.Platform;

/// <summary>
/// What a platform has to offer beyond a surface to draw into, so whatever would put up a button for it (a title bar,
/// an edge to drag, a window setting, a file picker) asks first. A phone has none of the window's, and a UI that drew
/// a maximise button there would be lying.
/// </summary>
[Flags]
public enum PlatformFeatures
{
    None = 0,

    /// <summary>A window of the desktop's, with a title, that can be shown, minimised and brought to the front.</summary>
    Window = 1 << 0,

    /// <summary>The window can be moved, resized and maximised by a title bar the UI draws (a window made without the system's frame).</summary>
    WindowFrame = 1 << 1,

    /// <summary>The desktop moves the window by an area the title bar says is its own, see <see cref="IPlatformWindow.SetDragArea"/>.</summary>
    DragArea = 1 << 2,

    /// <summary>Keys heard whatever window is in front, see <see cref="IPlatformWindow.RegisterHotKey"/>.</summary>
    HotKeys = 1 << 3,

    /// <summary>The window slid down from the top of the screen and back, see <see cref="IPlatformWindow.Drop"/>.</summary>
    DropDown = 1 << 4,

    /// <summary>Rounded corners on a window without the system's frame.</summary>
    CornerRadius = 1 << 5,

    /// <summary>The window goes fullscreen and back and takes a size it's given, see <see cref="Core.DisplaySettings"/>.</summary>
    DisplaySettings = 1 << 6,

    /// <summary>The pointer can be locked to the window and hidden, see <c>Mouse.Captured</c>.</summary>
    PointerCapture = 1 << 7,

    /// <summary>A clipboard with text on it.</summary>
    Clipboard = 1 << 8,

    /// <summary>Controllers plugged into the machine are read.</summary>
    Gamepads = 1 << 9,

    /// <summary>Folders and files can be browsed by their paths, which a file picker of the UI's needs.</summary>
    FileSystem = 1 << 10,

    /// <summary>
    /// The scene is drawn at 720p (its short edge 720 pixels, the window's shape kept) unless the game says otherwise, see
    /// <c>RenderScalePolicy.Automatic</c>. Every phone's and tablet's, where the screen has more pixels than the GPU can fill.
    /// </summary>
    AutomaticRenderScale = 1 << 11,
}
