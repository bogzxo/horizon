using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Silk.NET.SDL;

using Point = System.Drawing.Point;

namespace Horizon.Core;

// The frame of an SDL window that has none of the system's (WindowManagerConfiguration.CustomFrame). A Wayland window
// can't move itself, it has no idea where it is: the compositor moves it, and only when asked as the button goes down.
// So SDL is told what each point of the window is (SDL_SetWindowHitTest), and the title bar the UI draws says where it is
// and what is on it as it paints (SetDragArea). A press on the bar moves the window, on an edge resizes it, both by the
// desktop the way its own windows go (snapping, the edges of the screen, a maximised window coming back to its size), on
// X11 and a Mac as well. A double click on the bar is the UI's, it maximises the window or brings it back.
public partial class WindowManager
{
    // Where the title bar is and what on it isn't the bar, swapped whole so the thread that asks never reads half of one
    private sealed record FrameArea(RectangleF Bar, RectangleF[] Holes);

    private volatile FrameArea? frameArea;

    // The one window SDL asks about, its callback is static
    private static WindowManager? hitTested;

    // Whether SDL asks what the window is where it's pressed, which is then what moves and resizes it
    private bool hitTesting;

    // The last press on the bar (Environment.TickCount64) and where, and whether the button was down when last asked
    private long barPressedAt;
    private Point barPressedWhere;
    private bool buttonWasDown;

    // A second press on the bar this soon (and near) after the first is a double click
    private const int DOUBLE_CLICK_MS = 450, DOUBLE_CLICK_SLOP = 4;

    /// <summary>
    /// Says where the title bar the UI draws is, in the window's pixels (the pointer's), and what on it isn't the bar (its
    /// menus, its buttons), which a press goes to the UI on. Empty for no title bar. The title bar keeps it up as it paints.
    /// From any thread.
    /// </summary>
    public void SetDragArea(RectangleF bar, ReadOnlySpan<RectangleF> holes)
    {
        FrameArea? now = frameArea;
        if (bar.IsEmpty)
        {
            frameArea = null;
            return;
        }

        // Painted every frame and the same nearly every time, kept unless it moved, nothing made a frame
        if (now is not null && now.Bar == bar && now.Holes.AsSpan().SequenceEqual(holes))
            return;

        frameArea = new FrameArea(bar, holes.ToArray());
    }

    /// <summary>Helper method to have SDL ask what of the window a press is on. Thread of the window, once it is made.</summary>
    private unsafe void StartHitTesting()
    {
        if (!CustomFrame || SdlWindow is var window && window == null)
            return;

        hitTested = this;
        hitTesting = SdlProvider.SDL.Value.SetWindowHitTest(window, new PfnHitTest(&HitTest), null) == 0;
        if (!hitTesting)
            Logging.Log.Warning($"[{Name}] SDL can't be asked to move the window, it is moved by hand.");

        // Resizable as far as SDL's concerned, which a window with no frame isn't made as. SDL holds one that isn't to the
        // size it is, its least and most both that on Wayland and X11, and takes no other size the compositor offers, so
        // the edges said to resize it and did nothing at all
        SdlProvider.SDL.Value.SetWindowResizable(window, SdlBool.True);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe HitTestResult HitTest(Silk.NET.SDL.Window* window, Silk.NET.SDL.Point* point, void* data)
    {
        try
        {
            return hitTested?.WhatIsAt(new Point(point->X, point->Y)) ?? HitTestResult.HittestNormal;
        }
        catch (Exception)
        {
            return HitTestResult.HittestNormal;
        }
    }

    // What of the window a point is, an edge (unless it's maximised), the bar, or the UI's
    private unsafe HitTestResult WhatIsAt(Point at)
    {
        // On Wayland and X11 SDL asks as a button goes down and at no other time, before it has the button down itself, so
        // every time is a press. A Mac asks as the pointer moves as well, and is asked whether its button is down
        bool pressed = true;
        if (OperatingSystem.IsMacOS())
        {
            bool down = (SdlProvider.SDL.Value.GetGlobalMouseState((int*)null, (int*)null) & 1) != 0;
            pressed = down && !buttonWasDown;
            buttonWasDown = down;
        }

        var size = _window.Size;
        if (!maximized && !IsFullscreen())
        {
            int edge = (int)MathF.Ceiling(WINDOW_EDGE);
            bool left = at.X < edge, right = at.X >= size.X - edge, top = at.Y < edge, bottom = at.Y >= size.Y - edge;
            if (top && left) return HitTestResult.HittestResizeTopleft;
            if (top && right) return HitTestResult.HittestResizeTopright;
            if (bottom && left) return HitTestResult.HittestResizeBottomleft;
            if (bottom && right) return HitTestResult.HittestResizeBottomright;
            if (top) return HitTestResult.HittestResizeTop;
            if (bottom) return HitTestResult.HittestResizeBottom;
            if (left) return HitTestResult.HittestResizeLeft;
            if (right) return HitTestResult.HittestResizeRight;
        }

        if (frameArea is not { } area || !area.Bar.Contains(at))
            return HitTestResult.HittestNormal;

        foreach (RectangleF hole in area.Holes)
        {
            if (hole.Contains(at))
                return HitTestResult.HittestNormal;
        }

        // The second press of a double click goes to the UI's title bar, which maximises the window or brings it back
        if (pressed)
        {
            long now = Environment.TickCount64;
            bool again = now - barPressedAt < DOUBLE_CLICK_MS
                && Math.Abs(at.X - barPressedWhere.X) <= DOUBLE_CLICK_SLOP && Math.Abs(at.Y - barPressedWhere.Y) <= DOUBLE_CLICK_SLOP;
            barPressedAt = again ? 0 : now;
            barPressedWhere = at;
            if (again)
            {
                ToggleMaximized();
                return HitTestResult.HittestNormal;
            }
        }

        return HitTestResult.HittestDraggable;
    }

    // How near the edge of the window a press resizes it, the compositor's UIEdge
    private const float WINDOW_EDGE = 6.0f;
}
