using System.Runtime.InteropServices;

namespace Horizon.Core;

public partial class WindowManager
{
    /// <summary>Clips a custom window to rounded corners, in logical display units. Maximized windows stay square.
    /// Windows and X11 use a native shape; macOS clips its content layer. Wayland leaves decoration to its compositor.</summary>
    public void SetCornerRadius(float radius)
    {
        radius = float.IsFinite(radius) ? Math.Clamp(radius, 0, 200) : 0;
        PostToWindow(() => ApplyCornerRadius(radius));
    }

    private void ApplyCornerRadius(float radius)
    {
        if (!CustomFrame) return;
        if (IsWindowMaximized() || IsFullscreen()) radius = 0;
        if (OperatingSystem.IsWindows() && _window.Native?.Win32 is { Hwnd: not 0 } win32)
        {
            if (radius == 0) { CornerSetWindowRgn(win32.Hwnd, 0, true); return; }
            if (!CornerGetWindowRect(win32.Hwnd, out var rect)) return;
            int diameter = (int)MathF.Round(radius * (ScalesToDisplay ? DisplayScale : 1) * 2);
            nint region = CreateRoundRectRgn(0, 0, rect.Right - rect.Left + 1, rect.Bottom - rect.Top + 1, diameter, diameter);
            // Windows owns the region after success; only free a region it did not accept.
            if (region != 0 && CornerSetWindowRgn(win32.Hwnd, region, true) == 0) CornerDeleteObject(region);
        }
        else if (OperatingSystem.IsMacOS() && _window.Native?.Cocoa is { } cocoa && cocoa != 0)
        {
            nint view = (nint)MsgSend(cocoa, RegisterSelector("contentView"));
            CornerSendBool(view, RegisterSelector("setWantsLayer:"), true);
            nint layer = (nint)MsgSend(view, RegisterSelector("layer"));
            CornerSendDouble(layer, RegisterSelector("setCornerRadius:"), radius);
            CornerSendBool(layer, RegisterSelector("setMasksToBounds:"), radius > 0);
            CornerSendBool(cocoa, RegisterSelector("setOpaque:"), radius == 0);
            nint colour = (nint)MsgSend(CornerGetClass("NSColor"), RegisterSelector(radius > 0 ? "clearColor" : "windowBackgroundColor"));
            CornerSendObject(cocoa, RegisterSelector("setBackgroundColor:"), colour);
        }
        else if (OperatingSystem.IsLinux() && _window.Native?.X11 is { } x11)
        {
            try
            {
                var (display, window) = x11;
                if (!XShapeQueryExtension(display, out _, out _)) return;
                int width = (int)ViewportSize.X, height = (int)ViewportSize.Y;
                if (width <= 0 || height <= 0 || width > short.MaxValue || height > short.MaxValue) return;
                float r = Math.Min(radius * (ScalesToDisplay ? DisplayScale : 1), Math.Min(width, height) / 2f);
                var rows = new CornerXRectangle[height];
                for (int y = 0; y < height; y++)
                {
                    float dy = Math.Max(0, r - Math.Min(y + .5f, height - y - .5f));
                    int inset = r > 0 ? (int)MathF.Ceiling(r - MathF.Sqrt(Math.Max(0, r * r - dy * dy))) : 0;
                    rows[y] = new() { X = (short)inset, Y = (short)y, Width = (ushort)Math.Max(1, width - inset * 2), Height = 1 };
                }
                XShapeCombineRectangles(display, window, 0, 0, 0, rows, rows.Length, 0, 3);
                CornerXFlush(display);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CornerRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CornerXRectangle { public short X, Y; public ushort Width, Height; }
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CornerGetWindowRect(nint window, out CornerRect rect);
    [DllImport("user32.dll", EntryPoint = "SetWindowRgn")]
    private static extern int CornerSetWindowRgn(nint window, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("gdi32.dll", EntryPoint = "DeleteObject")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CornerDeleteObject(nint region);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void CornerSendBool(nint target, nint selector, [MarshalAs(UnmanagedType.I1)] bool value);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void CornerSendDouble(nint target, nint selector, double value);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void CornerSendObject(nint target, nint selector, nint value);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass")]
    private static extern nint CornerGetClass(string name);
    [DllImport("libXext.so.6")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool XShapeQueryExtension(nint display, out int eventBase, out int errorBase);
    [DllImport("libXext.so.6")]
    private static extern void XShapeCombineRectangles(nint display, nuint window, int kind, int x, int y, [In] CornerXRectangle[] rectangles, int count, int operation, int ordering);
    [DllImport("libX11.so.6", EntryPoint = "XFlush")]
    private static extern int CornerXFlush(nint display);
}
