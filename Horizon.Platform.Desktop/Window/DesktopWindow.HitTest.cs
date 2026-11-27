using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Silk.NET.SDL;

using Point = System.Drawing.Point;

using Horizon.Core;

namespace Horizon.Platform.Desktop;

// The frame of an SDL window that has none of the system's (WindowManagerConfiguration.CustomFrame). A Wayland window
// can't move itself, it has no idea where it is: the compositor moves it, and only when asked as the button goes down.
// So SDL is told what each point of the window is (SDL_SetWindowHitTest), and the title bar the UI draws says where it is
// and what is on it as it paints (SetDragArea). A press on the bar moves the window, on an edge resizes it, both by the
// desktop the way its own windows go (snapping, the edges of the screen, a maximised window coming back to its size), on
// X11 and a Mac as well. A double click on the bar is the UI's, it maximises the window or brings it back.

/// <summary>
/// Tracks title-bar hit-test candidates and double-click timing for SDL custom-frame windows.
/// On Wayland, SDL invokes the hit-test callback on every pointer enter/motion event as well as on button presses;
/// motion events immediately push <c>SDL_MOUSEMOTION</c> through the event watch, which cancels the pending candidate
/// before the event pump finishes.
/// </summary>
internal struct SdlBarClickTracker
{
    internal const int DoubleClickMs = 450;
    internal const int DoubleClickSlop = 4;

    private bool hasBarPress;
    private long barPressedAt;
    private Point barPressedWhere;

    private bool pending;
    private long pendingAt;
    private Point pendingWhere;

    /// <summary>Whether a title-bar hit-test candidate is currently awaiting the end of the SDL event pump.</summary>
    public readonly bool HasPending => pending;

    /// <summary>Records a Wayland title-bar hit-test callback as a pending press candidate.</summary>
    public void NoteBarHitTest(long now, Point at)
    {
        pending = true;
        pendingAt = now;
        pendingWhere = at;
    }

    /// <summary>Cancels any pending hit-test candidate because SDL followed it with a pointer-motion event.</summary>
    public void CancelPendingMotion() => pending = false;

    /// <summary>Clears any pending candidate and prior click state (e.g. after a window move or resize).</summary>
    public void Reset()
    {
        pending = false;
        hasBarPress = false;
        barPressedAt = 0;
        barPressedWhere = default;
    }

    /// <summary>
    /// Commits a pending hit-test candidate that was not cancelled by pointer motion.
    /// Returns <see langword="true"/> if it completes a double-click on the title bar.
    /// </summary>
    public bool FlushPending()
    {
        if (!pending)
            return false;

        pending = false;
        return RecordPress(pendingAt, pendingWhere);
    }

    /// <summary>
    /// Records a confirmed left-button press on the title bar.
    /// Returns <see langword="true"/> if it completes a double-click within <see cref="DoubleClickMs"/> and <see cref="DoubleClickSlop"/>.
    /// </summary>
    public bool RecordPress(long now, Point at)
    {
        long elapsed = now - barPressedAt;
        bool again = hasBarPress
            && elapsed >= 0
            && elapsed < DoubleClickMs
            && Math.Abs(at.X - barPressedWhere.X) <= DoubleClickSlop
            && Math.Abs(at.Y - barPressedWhere.Y) <= DoubleClickSlop;

        if (again)
        {
            hasBarPress = false;
            barPressedAt = 0;
            barPressedWhere = default;
            return true;
        }

        hasBarPress = true;
        barPressedAt = now;
        barPressedWhere = at;
        return false;
    }
}

public sealed partial class DesktopWindow
{
    private const uint SDL_WINDOWEVENT = 0x200;
    private const uint SDL_MOUSEMOTION = 0x400;
    private const byte SDL_WINDOWEVENT_MOVED = 4;
    private const byte SDL_WINDOWEVENT_RESIZED = 5;
    private const byte SDL_WINDOWEVENT_SIZE_CHANGED = 6;

    // Where the title bar is and what on it isn't the bar, swapped whole so the thread that asks never reads half of one
    private sealed record FrameArea(RectangleF Bar, RectangleF[] Holes);

    private volatile FrameArea? frameArea;

    // The one window SDL asks about, its callback is static
    private static DesktopWindow? hitTested;

    // Cocoa's SDL hit test consumes presses and relies on background dragging of its view.
    // Use the UI titlebar press and desktop drag tracker on our Metal-backed window.
    internal static bool UsesSdlDragArea => UsesSdl && !OperatingSystem.IsMacOS();

    // Whether SDL asks what the window is where it's pressed, which is then what moves and resizes it
    private bool hitTesting;
    private bool hitTestUsesWayland;
    private bool hitTestEventWatchInstalled;

    // Tracks confirmed title-bar clicks (and pending Wayland hit-test candidates) for double-click maximize
    private SdlBarClickTracker barClickTracker;
    private bool buttonWasDown;

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

        // A borderless SDL window starts out fixed-size, including on the UI-driven resize path.
        SdlProvider.SDL.Value.SetWindowResizable(window, SdlBool.True);
        if (!UsesSdlDragArea)
            return;

        hitTested = this;
        hitTestUsesWayland = !OperatingSystem.IsMacOS() && !CanPlaceWindow;
        barClickTracker.Reset();
        hitTesting = SdlProvider.SDL.Value.SetWindowHitTest(window, new PfnHitTest(&HitTest), null) == 0;
        if (hitTesting)
        {
            SdlProvider.SDL.Value.AddEventWatch(new PfnEventFilter(&HitTestEventWatch), null);
            hitTestEventWatchInstalled = true;
        }
        else
        {
            Logging.Log.Warning($"[{Name}] SDL can't be asked to move the window, it is moved by hand.");
        }
    }

    /// <summary>Removes the SDL hit-test event watch and clears hit-test state. Thread of the window.</summary>
    private unsafe void ReleaseHitTesting()
    {
        if (hitTestEventWatchInstalled)
        {
            hitTestEventWatchInstalled = false;
            SdlProvider.SDL.Value.DelEventWatch(new PfnEventFilter(&HitTestEventWatch), null);
        }

        barClickTracker.Reset();
        hitTesting = false;
        hitTestUsesWayland = false;
        if (ReferenceEquals(hitTested, this))
            hitTested = null;
    }

    /// <summary>
    /// Commits any Wayland title-bar hit-test candidate that survived the SDL event pump without an accompanying
    /// pointer-motion event, toggling maximize when it completes a double-click. Thread of the window.
    /// </summary>
    private void FlushHitTestPress()
    {
        if (hitTesting && hitTestUsesWayland && barClickTracker.FlushPending())
            ToggleMaximized();
    }

    /// <summary>
    /// Whether a point of the window, in its pixels, is on the title bar and not on anything of the bar's own (a menu, a
    /// button), which is what moves the window.
    /// </summary>
    internal static bool OnBar(RectangleF bar, ReadOnlySpan<RectangleF> holes, System.Numerics.Vector2 pixel)
    {
        var at = new PointF(pixel.X, pixel.Y);
        if (bar.IsEmpty || !bar.Contains(at))
            return false;

        foreach (RectangleF hole in holes)
        {
            if (hole.Contains(at))
                return false;
        }

        return true;
    }

    private static bool OnBar(FrameArea? area, System.Numerics.Vector2 pixel) => area is not null && OnBar(area.Bar, area.Holes, pixel);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe HitTestResult HitTest(Silk.NET.SDL.Window* window, Silk.NET.SDL.Point* point, void* data)
    {
        try
        {
            return hitTested?.WhatIsAt(new Point(point->X, point->Y)) ?? HitTestResult.Normal;
        }
        catch (Exception)
        {
            return HitTestResult.Normal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int HitTestEventWatch(void* userdata, Event* e)
    {
        try
        {
            if (hitTested is not { } window || e == null)
                return 1;

            if (e->Type == SDL_MOUSEMOTION)
                window.barClickTracker.CancelPendingMotion();
            else if (e->Type == SDL_WINDOWEVENT
                && e->Window.Event is SDL_WINDOWEVENT_MOVED or SDL_WINDOWEVENT_RESIZED or SDL_WINDOWEVENT_SIZE_CHANGED)
                window.barClickTracker.Reset();
        }
        catch (Exception)
        {
            // Never let a managed exception unwind back into SDL's C event loop.
        }

        return 1;
    }

    // What of the window a point is, an edge (unless it's maximised), the bar, or the UI's
    private unsafe HitTestResult WhatIsAt(Point at)
    {
        // On X11 (and macOS), SDL knows the current global mouse button state when the hit-test runs, so we can check
        // directly whether the left button just went down. On Wayland, SDL invokes the hit-test callback during both
        // pointer motion/enter and button presses before updating button state; pointer motion immediately pushes
        // SDL_MOUSEMOTION through HitTestEventWatch to cancel the pending candidate, whereas a real left-button press
        // survives to FlushHitTestPress after DoEvents().
        long asked = Environment.TickCount64;
        bool pressed = false;
        if (!hitTestUsesWayland)
        {
            bool down = (SdlProvider.SDL.Value.GetMouseState((int*)null, (int*)null) & 1) != 0;
            pressed = down && !buttonWasDown;
            buttonWasDown = down;
        }

        var size = _window.Size;
        if (!maximized && !IsFullscreen())
        {
            WindowEdges edges = EdgesAt(new System.Numerics.Vector2(at.X, at.Y), new System.Numerics.Vector2(size.X, size.Y), ScalesToDisplay ? WindowUnitScale : 1.0f);
            switch (edges)
            {
                case WindowEdges.Top | WindowEdges.Left: return HitTestResult.ResizeTopleft;
                case WindowEdges.Top | WindowEdges.Right: return HitTestResult.ResizeTopright;
                case WindowEdges.Bottom | WindowEdges.Left: return HitTestResult.ResizeBottomleft;
                case WindowEdges.Bottom | WindowEdges.Right: return HitTestResult.ResizeBottomright;
                case WindowEdges.Top: return HitTestResult.ResizeTop;
                case WindowEdges.Bottom: return HitTestResult.ResizeBottom;
                case WindowEdges.Left: return HitTestResult.ResizeLeft;
                case WindowEdges.Right: return HitTestResult.ResizeRight;
            }
        }

        // SDL says where in the window's points, the title bar said where it is in its pixels (the pointer's), two of them
        // to a point each way on a Retina screen. Compared as they came, the bar reached twice as far down and across as
        // it is drawn and its menus and buttons were looked for at twice where they are: a press on a menu moved the
        // window, a press on the bar between them didn't, and the top of what's under the bar couldn't be clicked
        if (!OnBar(frameArea, PointerToPixels(new System.Numerics.Vector2(at.X, at.Y))))
            return HitTestResult.Normal;

        if (hitTestUsesWayland)
        {
            // If an earlier bar hit-test in this same pump was never cancelled by SDL_MOUSEMOTION, commit it first.
            if (barClickTracker.HasPending && barClickTracker.FlushPending())
                ToggleMaximized();

            barClickTracker.NoteBarHitTest(asked, at);
            WakeSdl();
            return HitTestResult.Draggable;
        }

        // The second press of a double click maximises the window or brings it back
        if (pressed && barClickTracker.RecordPress(asked, at))
        {
            ToggleMaximized();
            return HitTestResult.Normal;
        }

        return HitTestResult.Draggable;
    }
}
