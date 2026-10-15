using System.Runtime.InteropServices;

namespace Horizon.Core;

// A window without a frame on Windows has its moving, resizing and maximising done by Windows itself, the same as a
// window with the system's frame. Only then does it get what Windows does with a title bar, drag it to the top of the
// screen and it maximises, to a side and it takes half, the snap bar of Windows 11 comes down while it is dragged,
// a maximised one dragged down comes back under the pointer, and Snap Layouts comes up over the maximise button the UI
// draws (see MaximizeButton). Windows only does any of it for a window it thinks
// has a frame, so the window gets the styles of one (a caption, a sizing border, the buttons) and the frame is taken
// away again by saying all of the window is the inside of it (WM_NCCALCSIZE). A press on the title bar is then handed
// to Windows as a press on the caption, and its own move loop takes over until the button comes up.
public partial class WindowManager
{
    // What the window is told it is, on top of the WS_POPUP of GLFW's undecorated window
    private const long FRAME_STYLES = WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;

    // The window, the procedure GLFW gave it that everything this one doesn't want goes on to, and this one, held on
    // to so the collector leaves it be while Windows has a pointer to it
    private nint frameHandle, glfwProcedure;
    private WindowProcedure? frameProcedure;

    private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);

    // Whether Windows does the frame, a frameless window on Windows once it is set up
    private bool SystemFrame => frameHandle != 0;

    /// <summary>
    /// Helper method to hand the frame of a frameless window to Windows, its procedure in front of GLFW's and the styles
    /// of a window with a frame. Thread of the window, once it is made and before it is shown.
    /// </summary>
    private void HandFrameToWindows()
    {
        if (_window.Native?.Win32 is not { } win32 || win32.Hwnd == 0)
            return;

        frameHandle = win32.Hwnd;
        frameProcedure = FrameProcedure;
        glfwProcedure = SetWindowLongPtr(frameHandle, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(frameProcedure));
        AddFrameStyles();
    }

    /// <summary>
    /// Helper method to give the window the styles of one with a frame (again, going fullscreen and back has GLFW put
    /// back the styles it thinks the window has). Thread of the window.
    /// </summary>
    private void AddFrameStyles()
    {
        if (!SystemFrame)
            return;

        nint style = GetWindowLongPtr(frameHandle, GWL_STYLE);
        if ((style & FRAME_STYLES) == FRAME_STYLES)
            return;

        SetWindowLongPtr(frameHandle, GWL_STYLE, style | (nint)FRAME_STYLES);
        SetWindowPos(frameHandle, 0, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    /// <summary>Helper method to give the window GLFW's procedure back before it is gone.</summary>
    private void ReleaseFrame()
    {
        if (!SystemFrame)
            return;

        SetWindowLongPtr(frameHandle, GWLP_WNDPROC, glfwProcedure);
        frameHandle = 0;
    }

    /// <summary>
    /// Helper method to have Windows drag the window by its caption or an edge (a hit test code), for as long as the
    /// button is held. Thread of the window, which sits in Windows' move loop until it is let go. Frames are drawn on
    /// their own thread meanwhile, only what is posted to this one waits for the drag to end.
    /// </summary>
    private void SystemDrag(int hit)
    {
        // Let go of already (a click quicker than the tick that heard it), and Windows would start a drag nobody is doing
        int button = GetSystemMetrics(SM_SWAPBUTTON) != 0 ? VK_RBUTTON : VK_LBUTTON;
        if (GetAsyncKeyState(button) >= 0 || !GetCursorPos(out NativePoint cursor))
            return;

        // GLFW captured the mouse for the press, and Windows doesn't start a drag for a window that has it
        ReleaseCapture();
        SendMessage(frameHandle, WM_NCLBUTTONDOWN, hit, PackPoint(cursor.X, cursor.Y));

        // The move loop ate the release, so GLFW still has the button down. It's told the button came up, and where the
        // pointer is now that the window moved under it
        if (GetCursorPos(out cursor) && ScreenToClient(frameHandle, ref cursor))
        {
            nint at = PackPoint(cursor.X, cursor.Y);
            PostMessage(frameHandle, WM_MOUSEMOVE, 0, at);
            PostMessage(frameHandle, WM_LBUTTONUP, 0, at);
        }
    }

    // The hit test codes of the edges and corners, which is how Windows is told what to size the window by
    private static int HitTestOf(WindowEdges edges) => edges switch
    {
        WindowEdges.Left => HTLEFT,
        WindowEdges.Right => HTRIGHT,
        WindowEdges.Top => HTTOP,
        WindowEdges.Bottom => HTBOTTOM,
        WindowEdges.Left | WindowEdges.Top => HTTOPLEFT,
        WindowEdges.Right | WindowEdges.Top => HTTOPRIGHT,
        WindowEdges.Left | WindowEdges.Bottom => HTBOTTOMLEFT,
        WindowEdges.Right | WindowEdges.Bottom => HTBOTTOMRIGHT,
        _ => HTCAPTION
    };

    private unsafe nint FrameProcedure(nint window, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            // All of the window is the inside of it, there is no frame to draw. But a maximised window can come out bigger
            // than the room the taskbar leaves (by the frame Windows thinks it has, or the whole of a screen bigger than
            // the main one), so its inside is held to that room and the taskbar stays in sight
            case WM_NCCALCSIZE:
                if (wParam != 0 && IsZoomed(window))
                {
                    var proposed = (NativeRect*)lParam;
                    if (WorkAreaOf(*proposed) is { } work)
                        *proposed = work;
                }
                return 0;

            // Maximised over the room the taskbar leaves, and never dragged smaller than its own title bar and buttons fit in
            case WM_GETMINMAXINFO:
                {
                    nint result = CallWindowProc(glfwProcedure, window, message, wParam, lParam);
                    var info = (MinMaxInfo*)lParam;
                    info->MinTrackSize = new NativePoint { X = MinimumSize.X, Y = MinimumSize.Y };

                    nint monitor = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
                    var screen = new MonitorInfo { Size = sizeof(MonitorInfo) };
                    if (monitor != 0 && GetMonitorInfo(monitor, ref screen))
                    {
                        info->MaxPosition = new NativePoint { X = screen.Work.Left - screen.Monitor.Left, Y = screen.Work.Top - screen.Monitor.Top };
                        info->MaxSize = new NativePoint { X = screen.Work.Right - screen.Work.Left, Y = screen.Work.Bottom - screen.Work.Top };
                    }
                    return result;
                }

            // The move loop has the mouse and the window hears none of it, so an application gone quiet is woken to lay
            // itself out for every size the window is dragged through
            case WM_SIZE:
                NoteInput();
                break;

            // The UI's maximise button is the frame's as far as Windows knows, which is what has Snap Layouts come up
            // over it. That makes the pointer over it the frame's as well, which GLFW never hears of, so it is told
            // the pointer moved there and the button went down and up there itself, and the UI's button works the same
            case WM_NCHITTEST:
                if (OverMaximizeButton(window, lParam))
                    return HTMAXBUTTON;
                break;

            case WM_NCMOUSEMOVE when wParam == HTMAXBUTTON:
                ToClient(window, WM_MOUSEMOVE, 0, lParam);
                break;

            case WM_NCLBUTTONDOWN or WM_NCLBUTTONDBLCLK when wParam == HTMAXBUTTON:
                ToClient(window, WM_LBUTTONDOWN, MK_LBUTTON, lParam);
                return 0;

            case WM_NCLBUTTONUP when wParam == HTMAXBUTTON:
                ToClient(window, WM_LBUTTONUP, 0, lParam);
                return 0;
        }

        return CallWindowProc(glfwProcedure, window, message, wParam, lParam);
    }

    private bool OverMaximizeButton(nint window, nint lParam)
    {
        if (maximizeButton?.Value is not { } button)
            return false;

        var at = UnpackPoint(lParam);
        return ScreenToClient(window, ref at) && button.Contains(at.X, at.Y);
    }

    /// <summary>Helper method to hand GLFW a message about the pointer over the frame as one about the inside of the window.</summary>
    private void ToClient(nint window, uint message, nint wParam, nint lParam)
    {
        var at = UnpackPoint(lParam);
        if (ScreenToClient(window, ref at))
            CallWindowProc(glfwProcedure, window, message, wParam, PackPoint(at.X, at.Y));
    }

    /// <summary>The room the taskbar leaves on the screen that has the most of a rectangle, null if Windows won't say.</summary>
    private static unsafe NativeRect? WorkAreaOf(in NativeRect rect)
    {
        nint monitor = MonitorFromRect(rect, MONITOR_DEFAULTTONEAREST);
        var info = new MonitorInfo { Size = sizeof(MonitorInfo) };
        return monitor != 0 && GetMonitorInfo(monitor, ref info) ? info.Work : null;
    }

    private static nint PackPoint(int x, int y) => ((y & 0xFFFF) << 16) | (x & 0xFFFF);

    // Signed, a screen left of or above the main one has its pointer at less than nothing
    private static NativePoint UnpackPoint(nint lParam) => new() { X = (short)(lParam & 0xFFFF), Y = (short)((lParam >> 16) & 0xFFFF) };

    private const int GWL_STYLE = -16, GWLP_WNDPROC = -4;
    private const long WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_SYSMENU = 0x00080000, WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;
    private const uint WM_SIZE = 0x0005, WM_GETMINMAXINFO = 0x0024, WM_NCCALCSIZE = 0x0083, WM_NCHITTEST = 0x0084, WM_NCMOUSEMOVE = 0x00A0, WM_NCLBUTTONDOWN = 0x00A1,
        WM_NCLBUTTONUP = 0x00A2, WM_NCLBUTTONDBLCLK = 0x00A3, WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    private const int MK_LBUTTON = 0x1, HTMAXBUTTON = 9, HTCAPTION = 2,HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    private const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, SM_SWAPBUTTON = 23;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern nint CallWindowProc(nint procedure, nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(nint window, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(nint window);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromRect(in NativeRect rect, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
