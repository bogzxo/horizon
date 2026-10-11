using Silk.NET.Input;

namespace Horizon.Input;

/// <summary>
/// The keyboard as one more gamepad (<see cref="GamepadInputManager.AddKeyboardGamepad"/>). It takes a slot, has
/// bindings and reads through everything a gamepad is read through, so a game written for gamepads (its menus, its
/// actions, its "press any button to join") is played on a keyboard without a line of it knowing. Its
/// <see cref="Gamepad.Kind"/> is <see cref="GamepadKind.Keyboard"/>, which is how a game shows keys in its prompts
/// and not the buttons of a gamepad (a text that starts with [icons:keyboard], see the skin's icon_sets).
/// <code>
/// engine.Input.Gamepads.AddKeyboardGamepad(engine.Input.Keyboard);
/// </code>
/// </summary>
public sealed class KeyboardGamepad
{
    /// <summary>
    /// Which key is which input when nobody says otherwise, the same as the keyboard icons of the Dead Revolver skin so
    /// what is on screen is what is pressed. WASD is the d-pad and the arrows the left stick, space the bottom button
    /// and escape the right one (yes and no in about every menu there is), enter is start.
    /// </summary>
    public static IReadOnlyList<(Key Key, GamepadInput Input)> DefaultKeys { get; } =
    [
        (Key.W, GamepadInput.DPadUp), (Key.S, GamepadInput.DPadDown), (Key.A, GamepadInput.DPadLeft), (Key.D, GamepadInput.DPadRight),
        (Key.Up, GamepadInput.LeftStickUp), (Key.Down, GamepadInput.LeftStickDown), (Key.Left, GamepadInput.LeftStickLeft), (Key.Right, GamepadInput.LeftStickRight),
        (Key.Space, GamepadInput.A), (Key.Escape, GamepadInput.B), (Key.E, GamepadInput.X), (Key.Tab, GamepadInput.Y),
        (Key.Q, GamepadInput.LeftBumper), (Key.R, GamepadInput.RightBumper),
        (Key.Z, GamepadInput.LeftTrigger), (Key.C, GamepadInput.RightTrigger),
        (Key.ShiftLeft, GamepadInput.LeftStick), (Key.ControlLeft, GamepadInput.RightStick),
        (Key.Backspace, GamepadInput.Back), (Key.Enter, GamepadInput.Start)
    ];

    private readonly Keyboard _keyboard;
    private readonly (Key Key, GamepadInput Input)[] _keys;
    private readonly GamepadInput[] _held;

    /// <summary>The gamepad the keys are read as.</summary>
    public Gamepad Gamepad { get; }

    internal KeyboardGamepad(Gamepad gamepad, Keyboard keyboard, IReadOnlyList<(Key Key, GamepadInput Input)> keys)
    {
        Gamepad = gamepad;
        _keyboard = keyboard;
        _keys = [.. keys];
        _held = new GamepadInput[_keys.Length];
    }

    /// <summary>
    /// Moves the gamepad on to what the keys are doing. The manager does this every update, before anybody reads a gamepad.
    /// </summary>
    internal void Update()
    {
        int count = 0;
        foreach (var (key, input) in _keys)
        {
            if (_keyboard.IsDown(key)) _held[count++] = input;
        }

        Gamepad.Update(GamepadSnapshot.Holding(_held.AsSpan(0, count)));
    }
}
