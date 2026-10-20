using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;

using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Horizon.Core;

/// <summary>Which edges of the window a drag takes with it, for resizing a window that has no frame of the system's.</summary>
[Flags]
public enum WindowEdges
{
    None = 0,
    Left = 1,
    Right = 2,
    Top = 4,
    Bottom = 8
}

// A window without the system's frame (WindowManagerConfiguration.CustomFrame), whose title bar and buttons the UI draws
// itself (TitleBar). The UI asks for what the frame would have done, minimise, maximise, close, and moving and resizing
// the window by dragging, and the thread of the window does it, the only one GLFW lets. On Windows the drag is handed to
// Windows, which snaps and maximises the way it does for any window (see WindowManager.Frame.Windows). Anywhere else it
// is followed on that thread from where the cursor is on the screen, a thousand times a second, so the window keeps up
// with the hand whatever the simulation is busy with and the cursor moving with the window doesn't throw the sums out.
public partial class WindowManager
{
    // The least a window is dragged down to, so its own title bar and buttons are never squeezed out of it
    private static readonly Vector2D<int> MinimumSize = new(480, 320);

    /// <summary>Whether the window was made without the system's frame, and the UI is to draw a title bar of its own.</summary>
    public bool CustomFrame { get; }

    /// <summary>Whether the window fills the screen it is on (the room the taskbar leaves). Kept up on the window's thread, read from any.</summary>
    public bool IsMaximized => maximized;

    private volatile bool maximized;

    // A window without a frame that was asked to start maximised, which on Windows is done once Windows has its frame
    // (and once it is centred, which is where it comes back to)
    private readonly bool maximizeWhenMade;

    // What is being dragged, the window about or an edge of it, and where it all was when the drag started
    private enum Drag { None, Move, Resize }
    private Drag drag;
    private WindowEdges dragEdges;
    private Vector2 dragCursor;
    private Vector2D<int> dragPosition, dragSize;
    private float grabAcross, grabDown;

    /// <summary>Puts the window down to the taskbar. From any thread.</summary>
    public void Minimize() => PostToWindow(() => _window.WindowState = WindowState.Minimized);

    /// <summary>Has the window fill the screen it is on, or go back to how it was. From any thread.</summary>
    public void ToggleMaximized() => PostToWindow(() => SetMaximized(!IsWindowMaximized()));

    /// <summary>
    /// Starts moving the window with the pointer, for as long as the left button is held, the way dragging a title bar
    /// does. A maximised window comes back to its size first, under the pointer where it was grabbed. From any thread.
    /// </summary>
    public void BeginMove() => PostToWindow(StartMove);

    /// <summary>Starts dragging edges of the window with the pointer, for as long as the left button is held. From any thread.</summary>
    public void BeginResize(WindowEdges edges) => PostToWindow(() => StartResize(edges));

    /// <summary>
    /// Where the maximise button the UI draws is, in the window's pixels, empty for nowhere. On Windows 11 the pointer
    /// resting on it brings up Snap Layouts, the way it does over the system's own. The title bar keeps it up. From any thread.
    /// </summary>
    public RectangleF MaximizeButton
    {
        get => maximizeButton?.Value ?? RectangleF.Empty;
        set
        {
            if (value != MaximizeButton)
                maximizeButton = value.IsEmpty ? null : new StrongBox<RectangleF>(value);
        }
    }

    // Swapped whole, so the window's thread never reads half of one
    private volatile StrongBox<RectangleF>? maximizeButton;

    private IMouse? FrameMouse => _input is { Mice.Count: > 0 } input ? input.Mice[0] : null;

    // Where the cursor is on the screen, which is where the window is and where in it the cursor is
    private Vector2 CursorOnScreen(IMouse mouse) => new Vector2(_window.Position.X, _window.Position.Y) + mouse.Position;

    private void StartMove()
    {
        // SDL's window was moved by the desktop as the button went down, see WindowManager.HitTest
        if (hitTesting)
            return;

        if (SystemFrame)
        {
            SystemDrag(HTCAPTION);
            return;
        }

        if (FrameMouse is not { } mouse || !mouse.IsButtonPressed(MouseButton.Left))
            return;

        // Grabbed at a share of the way across, so a maximised window that comes back smaller is still held where it was
        Vector2D<int> size = _window.Size;
        grabAcross = size.X > 0 ? mouse.Position.X / size.X : 0.5f;
        grabDown = mouse.Position.Y;

        if (IsWindowMaximized())
            SetMaximized(false);

        drag = Drag.Move;
        dragCursor = CursorOnScreen(mouse);
    }

    private void StartResize(WindowEdges edges)
    {
        if (hitTesting)
            return;

        if (SystemFrame && edges != WindowEdges.None && !IsWindowMaximized())
        {
            SystemDrag(HitTestOf(edges));
            return;
        }

        if (FrameMouse is not { } mouse || !mouse.IsButtonPressed(MouseButton.Left) || edges == WindowEdges.None || IsWindowMaximized())
            return;

        drag = Drag.Resize;
        dragEdges = edges;
        dragCursor = CursorOnScreen(mouse);
        dragPosition = _window.Position;
        dragSize = _window.Size;
    }

    /// <summary>Helper method to follow a drag of the window, and to keep up whether it is maximised. Thread of the window, every time it hears the system.</summary>
    private void TrackFrame()
    {
        maximized = IsWindowMaximized();

        if (drag == Drag.None || FrameMouse is not { } mouse)
            return;

        if (!mouse.IsButtonPressed(MouseButton.Left))
        {
            drag = Drag.None;
            return;
        }

        Vector2 cursor = CursorOnScreen(mouse);

        if (drag == Drag.Move)
        {
            Vector2D<int> size = _window.Size;
            var position = new Vector2D<int>((int)MathF.Round(cursor.X - grabAcross * size.X), (int)MathF.Round(cursor.Y - grabDown));
            if (position != _window.Position)
                _window.Position = position;
            return;
        }

        // An edge, the window grows or shrinks on that side and the other side stays where it is
        Vector2 moved = cursor - dragCursor;
        int left = dragPosition.X, top = dragPosition.Y, width = dragSize.X, height = dragSize.Y;

        if ((dragEdges & WindowEdges.Right) != 0)
            width = Math.Max(MinimumSize.X, dragSize.X + (int)moved.X);
        if ((dragEdges & WindowEdges.Bottom) != 0)
            height = Math.Max(MinimumSize.Y, dragSize.Y + (int)moved.Y);
        if ((dragEdges & WindowEdges.Left) != 0)
        {
            width = Math.Max(MinimumSize.X, dragSize.X - (int)moved.X);
            left = dragPosition.X + dragSize.X - width;
        }
        if ((dragEdges & WindowEdges.Top) != 0)
        {
            height = Math.Max(MinimumSize.Y, dragSize.Y - (int)moved.Y);
            top = dragPosition.Y + dragSize.Y - height;
        }

        var bounds = new Vector2D<int>(width, height);
        if (_window.Position != new Vector2D<int>(left, top))
            _window.Position = new Vector2D<int>(left, top);
        if (_window.Size != bounds)
            _window.Size = bounds;
    }

    /// <summary>
    /// Helper method to set up the frame of a window once it is made, handed to Windows there, and maximised if it was
    /// asked to start out that way. Thread of the window.
    /// </summary>
    private void SetUpFrame()
    {
        if (CustomFrame && OperatingSystem.IsWindows())
            HandFrameToWindows();
        else if (CustomFrame && UsesSdl)
            StartHitTesting();

        if (maximizeWhenMade)
            SetMaximized(true);
    }

    private bool IsWindowMaximized() => _window.WindowState == WindowState.Maximized;

    private void SetMaximized(bool maximize) =>
        _window.WindowState = maximize ? WindowState.Maximized : WindowState.Normal;
}
