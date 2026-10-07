using System.Numerics;
using Silk.NET.Input;

namespace Horizon.Input;

/// <summary>
/// One gamepad as the game sees it: a slot that stays the same for as long as the game runs, the bindings of whoever
/// plays on it, and what is held this update and what was held the one before.
/// The device behind it can come and go. When it is unplugged the slot stays, with its bindings, and reads as
/// nothing held until a device is plugged in again, so player two is still player two afterwards.
/// Everything here is as of the last update of the <see cref="GamepadInputManager"/>, and meant to be read
/// from the thread that updates it.
/// </summary>
public sealed class Gamepad
{
    private uint held, heldBefore;
    private GamepadBindings bindings;

    // Some drivers rest their triggers at -1 rather than at 0. There is no asking which, so a trigger is
    // taken to be one of those from the first time it reads below zero.
    private bool leftTriggerFromMinusOne, rightTriggerFromMinusOne;

    /// <summary>Which gamepad this is, counted from 0. The first player is on slot 0.</summary>
    public int Slot { get; }

    private string name = string.Empty;

    /// <summary>What the device calls itself, or called itself if it is gone.</summary>
    public string Name
    {
        get => name;
        internal set
        {
            name = value;
            Kind = GamepadInputs.KindOf(value);
        }
    }

    /// <summary>
    /// Whose buttons the gamepad has printed on it, worked out from its name whenever that changes.
    /// Set it to overrule the guess.
    /// </summary>
    public GamepadKind Kind { get; set; }

    /// <summary>The device behind this gamepad. Null while it is unplugged, and always for a virtual gamepad.</summary>
    public IGamepad? Device { get; internal set; }

    /// <summary>
    /// Whether this gamepad isn't a device at all but is driven by the game through <see cref="Update(in GamepadSnapshot)"/>.
    /// </summary>
    public bool IsVirtual { get; }

    /// <summary>Whether there is anything to read: a device is plugged in, or the gamepad is virtual.</summary>
    public bool IsConnected => IsVirtual || Device is not null;

    /// <summary>
    /// The bindings of this gamepad, its own from the start. Change them in place, or hand it other ones.
    /// </summary>
    public GamepadBindings Bindings
    {
        get => bindings;
        set => bindings = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The left stick with the deadzone applied, up and right are positive.</summary>
    public Vector2 LeftStick { get; private set; }

    /// <summary>The right stick with the deadzone applied, up and right are positive.</summary>
    public Vector2 RightStick { get; private set; }

    /// <summary>How far the left trigger is pulled, from 0 to 1.</summary>
    public float LeftTrigger { get; private set; }

    /// <summary>How far the right trigger is pulled, from 0 to 1.</summary>
    public float RightTrigger { get; private set; }

    /// <summary>
    /// An input that went down this update, if any did. This is what a menu for changing bindings waits for:
    /// <c>if (pad.Pressed is { } input) pad.Bindings.Rebind("jump", input);</c>
    /// </summary>
    public GamepadInput? Pressed
    {
        get
        {
            uint pressed = held & ~heldBefore;
            return pressed == 0 ? null : (GamepadInput)BitOperations.TrailingZeroCount(pressed);
        }
    }

    /// <summary>Whether anything at all is held.</summary>
    public bool AnyDown => held != 0;

    /// <summary>
    /// Everything that is held, one bit per input (see <see cref="GamepadInputs.Bit"/>). A menu that binds
    /// a combination of buttons collects these until they are all let go of again.
    /// </summary>
    public uint HeldMask => held;

    internal Gamepad(int slot, string name, GamepadBindings bindings, bool isVirtual)
    {
        Slot = slot;
        Name = name;
        IsVirtual = isVirtual;
        this.bindings = bindings;
    }

    /// <summary>Whether an input is held.</summary>
    public bool IsDown(GamepadInput input) => (held & GamepadInputs.Bit(input)) != 0;

    /// <summary>Whether an input went down this update.</summary>
    public bool WasPressed(GamepadInput input) => (held & ~heldBefore & GamepadInputs.Bit(input)) != 0;

    /// <summary>Whether an input was let go of this update.</summary>
    public bool WasReleased(GamepadInput input) => (heldBefore & ~held & GamepadInputs.Bit(input)) != 0;

    /// <summary>
    /// Whether an action is held: every input of one of the combinations it is bound to is down, and no bigger
    /// combination that contains it is (with A + B bound to something, holding both isn't A and isn't B).
    /// </summary>
    public bool IsDown(string action) => bindings.IsActive(action, held);

    /// <summary>Whether an action started this update: it is held and wasn't before.</summary>
    public bool WasPressed(string action) => bindings.IsActive(action, held) && !bindings.IsActive(action, heldBefore);

    /// <summary>Whether an action ended this update: it was held and isn't any more.</summary>
    public bool WasReleased(string action) => !bindings.IsActive(action, held) && bindings.IsActive(action, heldBefore);

    /// <summary>
    /// A direction out of four actions, for moving with whatever they are bound to (a stick, the d-pad or both).
    /// Never longer than 1.
    /// </summary>
    public Vector2 Direction(string left, string right, string down, string up)
    {
        Vector2 direction = new(
            (IsDown(right) ? 1.0f : 0.0f) - (IsDown(left) ? 1.0f : 0.0f),
            (IsDown(up) ? 1.0f : 0.0f) - (IsDown(down) ? 1.0f : 0.0f));

        return direction.LengthSquared() > 1.0f ? Vector2.Normalize(direction) : direction;
    }

    /// <summary>
    /// Runs the rumble motors of the device, from 0 to 1 each. Does nothing if there is no device or it has none.
    /// </summary>
    public void Vibrate(float left, float right)
    {
        if (Device is not { } device)
            return;

        var motors = device.VibrationMotors;
        if (motors.Count > 0)
            motors[0].Speed = Math.Clamp(left, 0.0f, 1.0f);
        if (motors.Count > 1)
            motors[1].Speed = Math.Clamp(right, 0.0f, 1.0f);
    }

    /// <summary>
    /// Moves this gamepad on by one update to what a snapshot says. The manager does this for real devices;
    /// for a virtual gamepad it is the game's to call, once per update.
    /// </summary>
    public void Update(in GamepadSnapshot raw)
    {
        heldBefore = held;

        LeftStick = ApplyDeadzone(raw.LeftStick, bindings.Deadzone);
        RightStick = ApplyDeadzone(raw.RightStick, bindings.Deadzone);
        LeftTrigger = Math.Clamp(raw.LeftTrigger, 0.0f, 1.0f);
        RightTrigger = Math.Clamp(raw.RightTrigger, 0.0f, 1.0f);

        uint now = raw.Buttons & GamepadInputs.ButtonMask;

        if (LeftTrigger >= bindings.TriggerThreshold) now |= GamepadInputs.Bit(GamepadInput.LeftTrigger);
        if (RightTrigger >= bindings.TriggerThreshold) now |= GamepadInputs.Bit(GamepadInput.RightTrigger);

        float threshold = bindings.StickThreshold;
        if (LeftStick.Y >= threshold) now |= GamepadInputs.Bit(GamepadInput.LeftStickUp);
        if (LeftStick.Y <= -threshold) now |= GamepadInputs.Bit(GamepadInput.LeftStickDown);
        if (LeftStick.X >= threshold) now |= GamepadInputs.Bit(GamepadInput.LeftStickRight);
        if (LeftStick.X <= -threshold) now |= GamepadInputs.Bit(GamepadInput.LeftStickLeft);
        if (RightStick.Y >= threshold) now |= GamepadInputs.Bit(GamepadInput.RightStickUp);
        if (RightStick.Y <= -threshold) now |= GamepadInputs.Bit(GamepadInput.RightStickDown);
        if (RightStick.X >= threshold) now |= GamepadInputs.Bit(GamepadInput.RightStickRight);
        if (RightStick.X <= -threshold) now |= GamepadInputs.Bit(GamepadInput.RightStickLeft);

        held = now;
    }

    /// <summary>
    /// Reads the device and moves on by one update. A gamepad without a device reads as nothing held.
    /// </summary>
    internal void Poll()
    {
        if (Device is not { IsConnected: true } device)
        {
            Update(default);
            return;
        }

        GamepadSnapshot raw = default;

        foreach (var button in device.Buttons)
        {
            if (button.Pressed && GamepadInputs.TryFromButton(button.Name, out var input))
                raw.Buttons |= GamepadInputs.Bit(input);
        }

        // Devices say down is positive, everything else in the engine says up is.
        var sticks = device.Thumbsticks;
        if (sticks.Count > 0) raw.LeftStick = new Vector2(sticks[0].X, -sticks[0].Y);
        if (sticks.Count > 1) raw.RightStick = new Vector2(sticks[1].X, -sticks[1].Y);

        var triggers = device.Triggers;
        if (triggers.Count > 0) raw.LeftTrigger = ReadTrigger(triggers[0].Position, ref leftTriggerFromMinusOne);
        if (triggers.Count > 1) raw.RightTrigger = ReadTrigger(triggers[1].Position, ref rightTriggerFromMinusOne);

        Update(raw);
    }

    /// <summary>Forgets what the device before this one was like.</summary>
    internal void ResetDevice()
    {
        leftTriggerFromMinusOne = rightTriggerFromMinusOne = false;
    }

    private static float ReadTrigger(float position, ref bool fromMinusOne)
    {
        if (position < 0.0f)
            fromMinusOne = true;

        return fromMinusOne ? (position + 1.0f) * 0.5f : position;
    }

    /// <summary>
    /// Cuts the middle out of a stick, by how far it is off centre and not per axis, so diagonals aren't
    /// favoured. The rest is stretched to start at 0 again.
    /// </summary>
    private static Vector2 ApplyDeadzone(Vector2 stick, float deadzone)
    {
        float length = stick.Length();
        if (length <= deadzone || length <= 0.0f)
            return Vector2.Zero;

        float stretched = MathF.Min(1.0f, (length - deadzone) / (1.0f - deadzone));
        return stick / length * stretched;
    }
}
