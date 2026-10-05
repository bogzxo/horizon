using System.Numerics;
using Silk.NET.Input;

namespace Horizon.Input2;

/// <summary>
/// Everything on a gamepad that can be held down, and so everything an action can be bound to.
/// The triggers and the directions of the sticks count as held once they are pushed past a threshold,
/// which lets an action sit on a stick or a trigger the same way it sits on a button.
/// The names are what bindings are saved under, so renaming one breaks the files that mention it.
/// </summary>
public enum GamepadInput : byte
{
    A,
    B,
    X,
    Y,
    LeftBumper,
    RightBumper,
    Back,
    Start,
    Home,
    LeftStick,
    RightStick,
    DPadUp,
    DPadRight,
    DPadDown,
    DPadLeft,

    LeftTrigger,
    RightTrigger,

    LeftStickUp,
    LeftStickRight,
    LeftStickDown,
    LeftStickLeft,
    RightStickUp,
    RightStickRight,
    RightStickDown,
    RightStickLeft
}

/// <summary>
/// Whose buttons a gamepad has printed on it. The inputs are the same either way and named after where they are
/// on an Xbox gamepad (<see cref="GamepadInput.A"/> is the bottom one of the four, the cross on a PlayStation
/// gamepad), this is for showing a player the right pictures.
/// </summary>
public enum GamepadKind
{
    Xbox,
    PlayStation
}

/// <summary>
/// What a gamepad reads as at one moment, before any deadzone or binding is applied to it.
/// This stores the buttons that are down, the sticks from -1 to 1 with up and right positive, and the triggers from 0 to 1.
/// A real gamepad is read into one of these every update; a gamepad that isn't a device at all (a replay, an AI,
/// a test) is driven by handing them to <see cref="Gamepad.Update(in GamepadSnapshot)"/>.
/// </summary>
public struct GamepadSnapshot
{
    /// <summary>One bit per button, at the position of its <see cref="GamepadInput"/>.</summary>
    public uint Buttons;

    public Vector2 LeftStick;
    public Vector2 RightStick;
    public float LeftTrigger;
    public float RightTrigger;

    /// <summary>
    /// A snapshot with some inputs fully held and everything else at rest. A trigger or a direction of
    /// a stick is pushed all the way.
    /// </summary>
    public static GamepadSnapshot Holding(params ReadOnlySpan<GamepadInput> inputs)
    {
        GamepadSnapshot snapshot = default;

        foreach (var input in inputs)
        {
            switch (input)
            {
                case GamepadInput.LeftTrigger: snapshot.LeftTrigger = 1.0f; break;
                case GamepadInput.RightTrigger: snapshot.RightTrigger = 1.0f; break;
                case GamepadInput.LeftStickUp: snapshot.LeftStick.Y = 1.0f; break;
                case GamepadInput.LeftStickDown: snapshot.LeftStick.Y = -1.0f; break;
                case GamepadInput.LeftStickRight: snapshot.LeftStick.X = 1.0f; break;
                case GamepadInput.LeftStickLeft: snapshot.LeftStick.X = -1.0f; break;
                case GamepadInput.RightStickUp: snapshot.RightStick.Y = 1.0f; break;
                case GamepadInput.RightStickDown: snapshot.RightStick.Y = -1.0f; break;
                case GamepadInput.RightStickRight: snapshot.RightStick.X = 1.0f; break;
                case GamepadInput.RightStickLeft: snapshot.RightStick.X = -1.0f; break;
                default: snapshot.Buttons |= GamepadInputs.Bit(input); break;
            }
        }

        return snapshot;
    }
}

/// <summary>
/// Helpers for going between <see cref="GamepadInput"/>, its bit in a mask of held inputs, and its name.
/// </summary>
public static class GamepadInputs
{
    /// <summary>How many inputs there are.</summary>
    public const int Count = (int)GamepadInput.RightStickLeft + 1;

    /// <summary>The bits of the inputs that are real buttons, the ones a snapshot carries in its mask.</summary>
    public const uint ButtonMask = (1u << ((int)GamepadInput.DPadLeft + 1)) - 1;

    public static uint Bit(GamepadInput input) => 1u << (int)input;

    // What the gamepads of a PlayStation call themselves, depending on the model and on who is asking.
    private static readonly string[] PlayStationNames =
        ["playstation", "dualsense", "dualshock", "sony", "ps3", "ps4", "ps5", "wireless controller"];

    /// <summary>
    /// Works out what kind of gamepad something is from what it calls itself. Anything that doesn't say it is
    /// from a PlayStation is taken to have the buttons of an Xbox gamepad, which is what most others copy.
    /// </summary>
    public static GamepadKind KindOf(string? name)
    {
        if (name is not null)
        {
            foreach (string known in PlayStationNames)
            {
                if (name.Contains(known, StringComparison.OrdinalIgnoreCase))
                    return GamepadKind.PlayStation;
            }
        }

        return GamepadKind.Xbox;
    }

    /// <summary>The bits of several inputs together, which is what a combination of them is kept as.</summary>
    public static uint Mask(params ReadOnlySpan<GamepadInput> inputs)
    {
        uint mask = 0;
        foreach (var input in inputs)
            mask |= Bit(input);
        return mask;
    }

    /// <summary>The inputs whose bits are set in a mask, in the order of the enum.</summary>
    public static GamepadInput[] FromMask(uint mask)
    {
        var inputs = new GamepadInput[BitOperations.PopCount(mask)];

        for (int i = 0; mask != 0; mask &= mask - 1)
            inputs[i++] = (GamepadInput)BitOperations.TrailingZeroCount(mask);

        return inputs;
    }

    /// <summary>
    /// Finds an input by its name, whatever the case. Numbers aren't names.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> name, out GamepadInput input) =>
        Enum.TryParse(name.Trim(), ignoreCase: true, out input)
        && Enum.IsDefined(input)
        && !char.IsAsciiDigit(name.Trim()[0]);

    /// <summary>
    /// The input a button of the device is, if it is one this system knows.
    /// </summary>
    public static bool TryFromButton(ButtonName button, out GamepadInput input)
    {
        GamepadInput? found = button switch
        {
            ButtonName.A => GamepadInput.A,
            ButtonName.B => GamepadInput.B,
            ButtonName.X => GamepadInput.X,
            ButtonName.Y => GamepadInput.Y,
            ButtonName.LeftBumper => GamepadInput.LeftBumper,
            ButtonName.RightBumper => GamepadInput.RightBumper,
            ButtonName.Back => GamepadInput.Back,
            ButtonName.Start => GamepadInput.Start,
            ButtonName.Home => GamepadInput.Home,
            ButtonName.LeftStick => GamepadInput.LeftStick,
            ButtonName.RightStick => GamepadInput.RightStick,
            ButtonName.DPadUp => GamepadInput.DPadUp,
            ButtonName.DPadRight => GamepadInput.DPadRight,
            ButtonName.DPadDown => GamepadInput.DPadDown,
            ButtonName.DPadLeft => GamepadInput.DPadLeft,
            _ => null
        };

        input = found ?? default;
        return found is not null;
    }
}
