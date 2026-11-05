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

    public DesktopInput(DesktopHost host, IInputContext context, Func<Vector2, Vector2> toPixels, nint windowHandle = 0)
    {
        this.host = host;
        this.context = context;
        this.toPixels = toPixels;
        source = new SilkInputSource(context, toPixels);
        if (OperatingSystem.IsWindows() && windowHandle != 0) touch = new WindowsTouch(windowHandle, toPixels);
    }

    public event Action<InputEvent>? EventReceived
    {
        add
        {
            source.EventReceived += value;
            if (touch is not null) touch.EventReceived += value;
        }
        remove
        {
            source.EventReceived -= value;
            if (touch is not null) touch.EventReceived -= value;
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

    /// <summary>How many fingers are on a Windows touch screen right now, null where they aren't heard. For a check.</summary>
    public int? TouchContacts => touch?.Installed == true ? touch.Contacts : null;

    public void Dispose()
    {
        source.Dispose();
        if (OperatingSystem.IsWindows()) touch?.Dispose();
    }
}
