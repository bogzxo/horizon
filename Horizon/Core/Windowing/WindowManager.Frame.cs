using System.Drawing;
using System.Numerics;

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
// the window by dragging, and the platform does it on its thread, the only one that may. How is the platform's, a
// desktop's is in Horizon.Platform.Desktop (DesktopWindow.Frame and the rest). Where there's no window none of it does
// anything, and CustomFrame says so to whoever would have drawn the buttons.
public partial class WindowManager
{
    /// <summary>The least a window is dragged down to, so its own title bar and buttons are never squeezed out of it.</summary>
    public static readonly Vector2 MinimumSize = new(480, 320);

    /// <summary>How near an edge of a frameless window a press resizes it, in the window's pixels at a scale of 100%.</summary>
    public const float RESIZE_EDGE = 8.0f;

    /// <summary>How far along an edge from a corner the corner reaches, so a diagonal resize isn't a needle to thread.</summary>
    public const float RESIZE_CORNER = 18.0f;

    /// <summary>
    /// Which edges of a window of a size a point is on, none when it's inside of them or off the window. A corner reaches
    /// further along each edge than an edge reaches in, the way the system's own frames are, so the diagonal is easy to
    /// get hold of. The point and the size are in the same pixels, and scale is how many of them a pixel at 100% is.
    /// </summary>
    public static WindowEdges EdgesAt(Vector2 at, Vector2 size, float scale)
    {
        if (at.X < 0.0f || at.Y < 0.0f || at.X > size.X || at.Y > size.Y)
            return WindowEdges.None;

        float edge = RESIZE_EDGE * scale, corner = RESIZE_CORNER * scale;
        bool left = at.X < edge, right = at.X >= size.X - edge, top = at.Y < edge, bottom = at.Y >= size.Y - edge;
        if (!left && !right && !top && !bottom)
            return WindowEdges.None;

        // On a side edge near the top or bottom is that corner, on the top or bottom edge near a side is that corner
        bool side = left || right, end = top || bottom;
        var edges = WindowEdges.None;
        if (left || (end && at.X < corner)) edges |= WindowEdges.Left;
        else if (right || (end && at.X >= size.X - corner)) edges |= WindowEdges.Right;
        if (top || (side && at.Y < corner)) edges |= WindowEdges.Top;
        else if (bottom || (side && at.Y >= size.Y - corner)) edges |= WindowEdges.Bottom;
        return edges;
    }

    /// <summary>
    /// Whether the window was made without the system's frame and the UI is to draw a title bar of its own. Never where
    /// the platform has no window frame to offer.
    /// </summary>
    public bool CustomFrame { get; }

    /// <summary>Whether the window fills the screen it is on (the room the taskbar leaves). From any thread.</summary>
    public bool IsMaximized => host.Window?.IsMaximized ?? false;

    /// <summary>Puts the window down to the taskbar. From any thread.</summary>
    public void Minimize() => host.Window?.Minimize();

    /// <summary>Has the window fill the screen it is on, or go back to how it was. From any thread.</summary>
    public void ToggleMaximized() => host.Window?.ToggleMaximized();

    /// <summary>
    /// Starts moving the window with the pointer, for as long as the left button is held, the way dragging a title bar
    /// does. A maximised window comes back to its size first, under the pointer where it was grabbed. From any thread.
    /// </summary>
    public void BeginMove() => host.Window?.BeginMove();

    /// <summary>Starts dragging edges of the window with the pointer, for as long as the left button is held. From any thread.</summary>
    public void BeginResize(WindowEdges edges) => host.Window?.BeginResize(edges);

    /// <summary>
    /// Where the maximise button the UI draws is, in the window's pixels, empty for nowhere. On Windows 11 the pointer
    /// resting on it brings up Snap Layouts, the way it does over the system's own. The title bar keeps it up. From any thread.
    /// </summary>
    public RectangleF MaximizeButton
    {
        get => host.Window?.MaximizeButton ?? RectangleF.Empty;
        set
        {
            if (host.Window is { } window)
                window.MaximizeButton = value;
        }
    }

    /// <summary>
    /// Says where the title bar the UI draws is, in the window's pixels (the pointer's), and what on it isn't the bar (its
    /// menus, its buttons), which a press goes to the UI on. Empty for no title bar. Only where the platform moves the
    /// window by it, see <see cref="Horizon.Platform.PlatformFeatures.DragArea"/>. From any thread.
    /// </summary>
    public void SetDragArea(RectangleF bar, ReadOnlySpan<RectangleF> holes) => host.Window?.SetDragArea(bar, holes);

    /// <summary>Clips a custom window to rounded corners, in logical display units. Maximized windows stay square.</summary>
    public void SetCornerRadius(float radius) => host.Window?.SetCornerRadius(radius);
}
