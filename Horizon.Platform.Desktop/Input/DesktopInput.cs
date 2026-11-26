using System.Numerics;

using Horizon.Input;

using Silk.NET.Input;

namespace Horizon.Platform.Desktop;

/// <summary>
/// The keyboard, the mouse and the clipboard of a desktop window, as the engine's input. The events come from
/// <see cref="SilkInputSource"/>, the capture and the clipboard are asked of the window's devices on its thread.
/// </summary>
internal sealed class DesktopInput : IPlatformInput, IDisposable
{
    private readonly DesktopHost host;
    private readonly IInputContext context;
    private readonly Func<Vector2, Vector2> toPixels;
    private readonly SilkInputSource source;

    // Fingers on a Windows touch screen, which GLFW would only ever hand over as the mouse
    private readonly WindowsTouch? touch;

    // Native Linux laptop touchpad scroll/phase and gesture monitors (Wayland wl_pointer + zwp_pointer_gestures_v1, or X11 XI 2.4)
    private readonly WaylandTouchpad? waylandTouchpad;
    private readonly X11Touchpad? x11Touchpad;

    // Last IME candidate rect sent to SDL, so unchanged caret frames allocate nothing
    private int lastImeX = int.MinValue, lastImeY, lastImeW, lastImeH;

    public DesktopInput(DesktopHost host, IInputContext context, Func<Vector2, Vector2> toPixels, nint windowHandle = 0, nint cocoaWindow = 0, nint sdlWindow = 0)
    {
        this.host = host;
        this.context = context;
        this.toPixels = toPixels;
        source = new SilkInputSource(context, toPixels);
        if (OperatingSystem.IsMacOS() && DesktopWindow.UsesSdl && cocoaWindow != 0)
        {
            MacGestures.Install(cocoaWindow, input => source.Raise(input));
            // Heard as input, as the wheel is, which wakes an application gone quiet. The fingers coming off a stretch
            // held still say nothing to SDL, and without it the spring back waited for the next slow tick and leapt
            MacScrollWatch.Monitor(cocoaWindow, input =>
            {
                source.Raise(input);
                host.Events.InputHeard();
            });
        }
        if (OperatingSystem.IsLinux() && DesktopWindow.UsesSdl && sdlWindow != 0)
        {
            Action<InputEvent> onLinuxTouchpad = input =>
            {
                source.Raise(input);
                host.Events.InputHeard();
            };
            waylandTouchpad = WaylandTouchpad.TryCreate(sdlWindow, onLinuxTouchpad);
            if (waylandTouchpad is null)
                x11Touchpad = X11Touchpad.TryCreate(sdlWindow, onLinuxTouchpad);
        }
        if (OperatingSystem.IsWindows() && windowHandle != 0) touch = new WindowsTouch(windowHandle, toPixels);
    }

    public event Action<InputEvent>? EventReceived
    {
        add
        {
            source.EventReceived += value;
            if (touch is not null && OperatingSystem.IsWindows()) touch.EventReceived += value;
        }
        remove
        {
            source.EventReceived -= value;
            if (touch is not null && OperatingSystem.IsWindows()) touch.EventReceived -= value;
        }
    }

    // A Mac says where the pointer is in points, everything in the engine goes by the pixels that are drawn into
    public Vector2 PointerPosition => context.Mice.Count > 0 ? toPixels(context.Mice[0].Position) : Vector2.Zero;

    /// <summary>Locks the pointer or lets it go, raw where the system has that and hidden in the window where not. Thread of the window.</summary>
    public void SetPointerCaptured(bool captured)
    {
        foreach (IMouse mouse in context.Mice)
        {
            ICursor cursor = mouse.Cursor;
            CursorMode mode = !captured ? CursorMode.Normal
                : cursor.IsSupported(CursorMode.Raw) ? CursorMode.Raw
                : CursorMode.Disabled;

            if (cursor.IsSupported(mode)) cursor.CursorMode = mode;
        }
    }

    public string ClipboardText
    {
        get => context.Keyboards.FirstOrDefault()?.ClipboardText ?? string.Empty;
        set
        {
            if (context.Keyboards.FirstOrDefault() is { } keyboard) keyboard.ClipboardText = value;
        }
    }

    public IPlatformGamepads? OpenGamepads() => host.OpenGamepads(context);

    public void SetTextInputRect(int x, int y, int width, int height)
    {
        if (!DesktopWindow.UsesSdl)
            return;

        if (x == lastImeX && y == lastImeY && width == lastImeW && height == lastImeH)
            return;

        lastImeX = x;
        lastImeY = y;
        lastImeW = width;
        lastImeH = height;

        if (host.IsPlatformThread)
            SdlTextEditing.SetTextInputRect(x, y, width, height);
        else
            host.Post(() => SdlTextEditing.SetTextInputRect(x, y, width, height));
    }

    /// <summary>How many fingers are on a Windows touch screen right now, null where they aren't heard. For a check.</summary>
    public int? TouchContacts => OperatingSystem.IsWindows() && touch?.Installed == true ? touch.Contacts : null;

    public void Dispose()
    {
        if (OperatingSystem.IsLinux())
        {
            waylandTouchpad?.Dispose();
            x11Touchpad?.Dispose();
        }
        source.Dispose();
        if (OperatingSystem.IsWindows()) touch?.Dispose();
    }
}
