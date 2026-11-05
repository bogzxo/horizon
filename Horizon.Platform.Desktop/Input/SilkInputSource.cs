using System.Diagnostics;
using System.Numerics;
using Silk.NET.Input;

using Horizon.Input;
using Key = Horizon.Input.Key;
using MouseButton = Horizon.Input.MouseButton;
using Keyboard = Horizon.Input.Keyboard;

namespace Horizon.Platform.Desktop;

/// <summary>The desktop keyboard and mouse translated at the platform boundary.</summary>
internal sealed class SilkInputSource : IInputSource, IDisposable
{
    private readonly IKeyboard[] keyboards;
    private readonly IMouse[] mice;
    private readonly Func<Vector2, Vector2> toPixels;
    public event Action<InputEvent>? EventReceived;

    public SilkInputSource(IInputContext context, Func<Vector2, Vector2> toPixels)
    {
        this.toPixels = toPixels;
        keyboards = [.. context.Keyboards];
        mice = [.. context.Mice];
        foreach (var keyboard in keyboards)
        {
            keyboard.KeyDown += OnKeyDown;
            keyboard.KeyUp += OnKeyUp;
            keyboard.KeyChar += OnCharacter;
        }
        foreach (var mouse in mice)
        {
            mouse.MouseMove += OnMove;
            mouse.MouseDown += OnMouseDown;
            mouse.MouseUp += OnMouseUp;
            mouse.Scroll += OnScroll;
        }
    }

    private void OnKeyDown(IKeyboard device, Silk.NET.Input.Key key, int code)
    {
        var input = InputEvent.KeyChange((Key)key, true) with { Modifiers = ModifiersOf(device), HasModifierSnapshot = true };
        if ((Key)key == Key.V && PrimaryHeld(device))
        {
            // Clipboard access belongs to the window thread, and its text belongs to this exact key event.
            string text;
            try { text = device.ClipboardText ?? string.Empty; }
            catch { text = string.Empty; }
            input = input with { ClipboardText = text };
        }
        EventReceived?.Invoke(input);
    }

    private void OnKeyUp(IKeyboard device, Silk.NET.Input.Key key, int code) => EventReceived?.Invoke(
        InputEvent.KeyChange((Key)key, false) with { Modifiers = ModifiersOf(device), HasModifierSnapshot = true });
    private void OnCharacter(IKeyboard device, char character) => EventReceived?.Invoke(InputEvent.Typed(character.ToString()));
    private void OnMove(IMouse device, Vector2 position) => EventReceived?.Invoke(InputEvent.Pointer(InputEventKind.PointerMove, toPixels(position)));
    private void OnMouseDown(IMouse device, Silk.NET.Input.MouseButton button) => EventReceived?.Invoke(InputEvent.Pointer(InputEventKind.PointerDown, toPixels(device.Position), button: (MouseButton)button));
    private void OnMouseUp(IMouse device, Silk.NET.Input.MouseButton button) => EventReceived?.Invoke(InputEvent.Pointer(InputEventKind.PointerUp, toPixels(device.Position), button: (MouseButton)button));
    private void OnScroll(IMouse device, ScrollWheel wheel) => EventReceived?.Invoke(new InputEvent
    {
        Kind = InputEventKind.Scroll, ScrollY = wheel.Y, Position = toPixels(device.Position), Timestamp = Stopwatch.GetTimestamp()
    });

    public void Dispose()
    {
        foreach (var keyboard in keyboards)
        {
            keyboard.KeyDown -= OnKeyDown;
            keyboard.KeyUp -= OnKeyUp;
            keyboard.KeyChar -= OnCharacter;
        }
        foreach (var mouse in mice)
        {
            mouse.MouseMove -= OnMove;
            mouse.MouseDown -= OnMouseDown;
            mouse.MouseUp -= OnMouseUp;
            mouse.Scroll -= OnScroll;
        }
    }

    private static bool PrimaryHeld(IKeyboard keyboard) => Keyboard.PrimaryIsCommand
        ? keyboard.IsKeyPressed(Silk.NET.Input.Key.SuperLeft) || keyboard.IsKeyPressed(Silk.NET.Input.Key.SuperRight)
        : keyboard.IsKeyPressed(Silk.NET.Input.Key.ControlLeft) || keyboard.IsKeyPressed(Silk.NET.Input.Key.ControlRight);

    private static KeyModifiers ModifiersOf(IKeyboard keyboard) =>
        (Held(keyboard, Silk.NET.Input.Key.ControlLeft, Silk.NET.Input.Key.ControlRight) ? KeyModifiers.Control : 0) |
        (Held(keyboard, Silk.NET.Input.Key.ShiftLeft, Silk.NET.Input.Key.ShiftRight) ? KeyModifiers.Shift : 0) |
        (Held(keyboard, Silk.NET.Input.Key.AltLeft, Silk.NET.Input.Key.AltRight) ? KeyModifiers.Alt : 0) |
        (Held(keyboard, Silk.NET.Input.Key.SuperLeft, Silk.NET.Input.Key.SuperRight) ? KeyModifiers.Super : 0);

    private static bool Held(IKeyboard keyboard, Silk.NET.Input.Key left, Silk.NET.Input.Key right) =>
        keyboard.IsKeyPressed(left) || keyboard.IsKeyPressed(right);
}
