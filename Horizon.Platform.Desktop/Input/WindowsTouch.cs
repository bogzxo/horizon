using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Horizon.Input;

namespace Horizon.Platform.Desktop;

/// <summary>
/// Fingers on a Windows touch screen, as the engine's touch events. GLFW only knows the mouse, and Windows turns a
/// finger into one for a window that doesn't say otherwise, which would have every tap be a click of the mouse and every
/// swipe a drag of it. So the window is subclassed (SetWindowSubclass, which sits in front of whatever procedure it has,
/// GLFW's or the custom frame's) and hears WM_POINTER itself. A finger's messages are taken and never go on, which is what
/// keeps Windows from making a mouse of them, and the mouse, the touchpad and a pen go on as they always did.
/// <para>
/// Windows hands out pointer ids again as soon as a finger lifts, so every contact gets one of its own that's never used
/// again, the way the Android host does. Positions come in screen pixels and go out in the pixels drawn into, the same
/// way the mouse's do. A cancelled contact (WM_POINTERCAPTURECHANGED, a cancelled up, the window losing the focus) is a
/// cancel, never an up, so nothing it held is clicked.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsTouch : IInputSource, IDisposable
{
    private const uint WM_POINTERUPDATE = 0x0245, WM_POINTERDOWN = 0x0246, WM_POINTERUP = 0x0247, WM_POINTERCAPTURECHANGED = 0x024C;
    private const uint WM_KILLFOCUS = 0x0008, WM_ACTIVATEAPP = 0x001C, WM_CANCELMODE = 0x001F;
    private const int PT_TOUCH = 2;
    private const int POINTER_MESSAGE_FLAG_CANCELED = 0x8000;

    // The id the subclass is known by to comctl32, anything that's ours
    private static readonly nuint SubclassId = 0x48_5A_54_43; // "HZTC"

    private readonly nint window;
    private readonly Func<Vector2, Vector2> toPixels;
    private readonly SubclassProcedure procedure;
    private readonly Dictionary<uint, (int Id, Vector2 Position)> contacts = [];
    private int nextId = 1;
    private bool installed;

    public event Action<InputEvent>? EventReceived;

    /// <summary>How many fingers are down right now, for a check.</summary>
    public int Contacts => contacts.Count;

    private delegate nint SubclassProcedure(nint window, uint message, nint wParam, nint lParam, nuint id, nuint data);

    /// <param name="window">The window's handle. Thread of the window, which is the only one its messages come on.</param>
    /// <param name="toPixels">From the window's points to the pixels drawn into, the mouse's conversion.</param>
    public WindowsTouch(nint window, Func<Vector2, Vector2> toPixels)
    {
        this.window = window;
        this.toPixels = toPixels;
        procedure = Procedure;
        installed = window != 0 && SetWindowSubclass(window, Marshal.GetFunctionPointerForDelegate(procedure), SubclassId, 0);
    }

    /// <summary>Whether the window is heard, false where Windows wouldn't have the subclass.</summary>
    public bool Installed => installed;

    private nint Procedure(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        switch (message)
        {
            case WM_POINTERDOWN or WM_POINTERUPDATE or WM_POINTERUP or WM_POINTERCAPTURECHANGED:
                uint pointer = (uint)(wParam & 0xFFFF);
                if (!GetPointerType(pointer, out int type) || type != PT_TOUCH)
                    break;

                Heard(message, pointer, (int)((wParam >> 16) & 0xFFFF), lParam);
                return 0;

            // The window lost the keyboard or the app went to the back, the ups would never come
            case WM_KILLFOCUS:
            case WM_ACTIVATEAPP when wParam == 0:
            case WM_CANCELMODE:
                CancelAll();
                break;
        }

        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private void Heard(uint message, uint pointer, int flags, nint lParam)
    {
        long stamp = Stopwatch.GetTimestamp();
        if (message == WM_POINTERCAPTURECHANGED)
        {
            End(pointer, cancelled: true, null, stamp);
            return;
        }

        Vector2 position = ClientPixels(lParam);
        switch (message)
        {
            case WM_POINTERDOWN:
                // A down for an id still held means its up got lost, it's ended first
                if (contacts.ContainsKey(pointer)) End(pointer, cancelled: true, position, stamp);
                int contact = nextId++;
                if (nextId == int.MaxValue) nextId = 1;
                contacts[pointer] = (contact, position);
                Publish(InputEventKind.PointerDown, position, contact, stamp);
                break;

            case WM_POINTERUPDATE:
                if (!contacts.TryGetValue(pointer, out var moved) || moved.Position == position) break;
                contacts[pointer] = (moved.Id, position);
                Publish(InputEventKind.PointerMove, position, moved.Id, stamp);
                break;

            case WM_POINTERUP:
                End(pointer, (flags & POINTER_MESSAGE_FLAG_CANCELED) != 0, position, stamp);
                break;
        }
    }

    private void End(uint pointer, bool cancelled, Vector2? position, long stamp)
    {
        if (!contacts.Remove(pointer, out var contact)) return;
        Publish(cancelled ? InputEventKind.PointerCancel : InputEventKind.PointerUp, position ?? contact.Position, contact.Id, stamp);
    }

    /// <summary>Every finger cancelled. Thread of the window.</summary>
    public void CancelAll()
    {
        long stamp = Stopwatch.GetTimestamp();
        foreach (uint pointer in contacts.Keys.ToArray())
            End(pointer, cancelled: true, null, stamp);
    }

    private void Publish(InputEventKind kind, Vector2 position, int id, long stamp) =>
        EventReceived?.Invoke(InputEvent.Pointer(kind, position, PointerKind.Touch, id) with { Timestamp = stamp });

    // A pointer message says where in screen pixels, which is turned into the window's and then the pixels drawn into
    private Vector2 ClientPixels(nint lParam)
    {
        var at = new NativePoint { X = (short)(lParam & 0xFFFF), Y = (short)((lParam >> 16) & 0xFFFF) };
        ScreenToClient(window, ref at);
        return toPixels(new Vector2(at.X, at.Y));
    }

    /// <summary>Lets go of the window. Thread of the window.</summary>
    public void Dispose()
    {
        CancelAll();
        if (installed) RemoveWindowSubclass(window, Marshal.GetFunctionPointerForDelegate(procedure), SubclassId);
        installed = false;
        EventReceived = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }

    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window, nint procedure, nuint id, nuint data);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window, nint procedure, nuint id);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern nint DefSubclassProc(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPointerType(uint pointer, out int type);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(nint window, ref NativePoint point);
}
