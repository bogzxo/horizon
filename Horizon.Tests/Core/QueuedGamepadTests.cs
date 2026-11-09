using System.Numerics;
using Horizon.Input;

namespace Horizon.Tests.Core;

public class QueuedGamepadTests
{
    [Fact]
    public void A_short_sampled_button_press_survives_for_one_tick()
    {
        var manager = new GamepadInputManager();
        var device = new GamepadDevice("Controller");
        manager.Hear(Connection(device, true));
        manager.Hear(State(device, GamepadSnapshot.Holding(GamepadInput.A)));
        manager.Hear(State(device, default));
        Assert.Equal(0, manager.Count);

        manager.UpdateState(0.01f);
        var pad = manager[0];
        Assert.True(pad.IsConnected);
        Assert.True(pad.WasPressed(GamepadInput.A));
        Assert.True(pad.IsDown(GamepadInput.A));

        manager.UpdateState(0.01f);
        Assert.True(pad.WasReleased(GamepadInput.A));
        Assert.False(pad.IsDown(GamepadInput.A));
        manager.UpdateState(0.01f);
        Assert.False(pad.WasReleased(GamepadInput.A));
    }

    [Fact]
    public void A_held_state_stays_held_without_new_samples()
    {
        var manager = new GamepadInputManager();
        var device = new GamepadDevice("Controller");
        manager.Hear(Connection(device, true));
        manager.Hear(State(device, GamepadSnapshot.Holding(GamepadInput.X)));
        manager.UpdateState(0.01f);
        manager.UpdateState(0.01f);
        Assert.True(manager[0].IsDown(GamepadInput.X));
        Assert.False(manager[0].WasPressed(GamepadInput.X));
    }

    [Fact]
    public void Disconnect_releases_buttons_and_reconnect_reuses_bindings_without_old_samples()
    {
        var manager = new GamepadInputManager();
        var first = new GamepadDevice("Controller");
        manager.Hear(Connection(first, true));
        manager.Hear(State(first, GamepadSnapshot.Holding(GamepadInput.A)));
        manager.UpdateState(0.01f);
        var pad = manager[0];
        pad.Bindings.Bind("jump", GamepadInput.B);

        manager.Hear(Connection(first, false));
        manager.UpdateState(0.01f);
        Assert.False(pad.IsConnected);
        Assert.True(pad.WasReleased(GamepadInput.A));

        var second = new GamepadDevice("Controller");
        manager.Hear(Connection(second, true));
        manager.Hear(State(first, GamepadSnapshot.Holding(GamepadInput.A)));
        manager.UpdateState(0.01f);
        Assert.Same(pad, manager[0]);
        Assert.Equal(1, manager.Count);
        Assert.False(pad.AnyDown);
        manager.Hear(State(second, GamepadSnapshot.Holding(GamepadInput.B)));
        manager.UpdateState(0.01f);
        Assert.True(pad.WasPressed("jump"));
    }

    [Fact]
    public void Device_identity_keeps_identically_named_controllers_separate()
    {
        var manager = new GamepadInputManager();
        var first = new GamepadDevice("Controller");
        var second = new GamepadDevice("Controller");
        manager.Hear(Connection(first, true));
        manager.Hear(Connection(first, true));
        manager.Hear(Connection(second, true));
        manager.Hear(State(second, GamepadSnapshot.Holding(GamepadInput.Y)));
        manager.UpdateState(0.01f);
        Assert.Equal(2, manager.Count);
        Assert.False(manager[0].AnyDown);
        Assert.True(manager[1].IsDown(GamepadInput.Y));
    }

    [Fact]
    public void Connection_notifications_run_during_update_and_unknown_samples_are_ignored()
    {
        var manager = new GamepadInputManager();
        var device = new GamepadDevice("Controller");
        int connected = 0, disconnected = 0;
        manager.OnGamepadConnected += _ => connected++;
        manager.OnGamepadDisconnected += _ => disconnected++;
        manager.Hear(State(device, GamepadSnapshot.Holding(GamepadInput.A)));
        manager.UpdateState(0.01f);
        Assert.Equal(0, manager.Count);
        manager.Hear(Connection(device, true));
        Assert.Equal(0, connected);
        manager.UpdateState(0.01f);
        Assert.Equal(1, connected);
        manager.Hear(Connection(device, false));
        Assert.Equal(0, disconnected);
        manager.UpdateState(0.01f);
        Assert.Equal(1, disconnected);
    }

    [Fact]
    public void Latest_analog_state_wins_and_haptics_are_clamped()
    {
        var manager = new GamepadInputManager();
        (float Left, float Right) motors = default;
        var device = new GamepadDevice("Controller", (left, right) => motors = (left, right));
        manager.Hear(Connection(device, true));
        manager.Hear(State(device, new GamepadSnapshot { LeftStick = Vector2.UnitY, LeftTrigger = 1 }));
        manager.Hear(State(device, new GamepadSnapshot { LeftStick = Vector2.UnitX, LeftTrigger = 0.25f }));
        manager.UpdateState(0.01f);
        Assert.Equal(Vector2.UnitX, manager[0].LeftStick);
        Assert.Equal(0.25f, manager[0].LeftTrigger);
        manager[0].Vibrate(-1, 2);
        Assert.Equal((0.0f, 1.0f), motors);
    }

    [Fact]
    public void A_neutral_sample_lets_go_of_everything_and_the_pad_presses_again_after()
    {
        // What a host sends when the app loses focus or goes to the background, nothing held, so a button that was down
        // when the phone call came doesn't stay down for the fight
        var manager = new GamepadInputManager();
        var device = new GamepadDevice("DualSense Wireless Controller");
        manager.Hear(Connection(device, true));
        manager.Hear(State(device, new GamepadSnapshot { Buttons = GamepadInputs.Mask(GamepadInput.A, GamepadInput.DPadLeft), LeftStick = -Vector2.UnitX, RightTrigger = 1 }));
        manager.UpdateState(0.01f);
        var pad = manager[0];
        Assert.Equal(GamepadKind.PlayStation, pad.Kind);
        Assert.True(pad.IsDown(GamepadInput.A));
        Assert.True(pad.IsDown(GamepadInput.LeftStickLeft));
        Assert.True(pad.IsDown(GamepadInput.RightTrigger));

        manager.Hear(State(device, default));
        manager.UpdateState(0.01f);
        Assert.False(pad.AnyDown);
        Assert.True(pad.WasReleased(GamepadInput.A));
        Assert.Equal(Vector2.Zero, pad.LeftStick);
        Assert.Equal(0.0f, pad.RightTrigger);

        manager.UpdateState(0.01f);
        Assert.False(pad.AnyDown);

        manager.Hear(State(device, GamepadSnapshot.Holding(GamepadInput.A)));
        manager.UpdateState(0.01f);
        Assert.True(pad.WasPressed(GamepadInput.A));
    }

    [Fact]
    public void A_stick_inside_the_deadzone_presses_nothing_and_past_it_presses_a_direction()
    {
        var manager = new GamepadInputManager();
        var device = new GamepadDevice("Controller");
        manager.Hear(Connection(device, true));
        manager.Hear(State(device, new GamepadSnapshot { LeftStick = new Vector2(0.1f, -0.1f), RightStick = new Vector2(0.15f, 0.0f) }));
        manager.UpdateState(0.01f);
        var pad = manager[0];
        Assert.False(pad.AnyDown);
        Assert.Equal(Vector2.Zero, pad.LeftStick);
        Assert.Equal(Vector2.Zero, pad.RightStick);

        manager.Hear(State(device, new GamepadSnapshot { LeftStick = new Vector2(0.0f, 0.9f), RightStick = new Vector2(-0.9f, 0.0f) }));
        manager.UpdateState(0.01f);
        Assert.True(pad.WasPressed(GamepadInput.LeftStickUp));
        Assert.True(pad.WasPressed(GamepadInput.RightStickLeft));
        Assert.False(pad.IsDown(GamepadInput.LeftStickDown));
        Assert.False(pad.IsDown(GamepadInput.LeftStickLeft));
    }

    [Fact]
    public void A_pad_that_comes_back_under_a_new_connection_takes_its_old_slot_ahead_of_a_stranger()
    {
        // Bluetooth gives a pad a new device every time it connects, the slot goes by the name
        var manager = new GamepadInputManager();
        var first = new GamepadDevice("DualSense Wireless Controller");
        var other = new GamepadDevice("Xbox Wireless Controller");
        manager.Hear(Connection(first, true));
        manager.Hear(Connection(other, true));
        manager.UpdateState(0.01f);
        Assert.Equal("DualSense Wireless Controller", manager[0].Name);
        Assert.Equal("Xbox Wireless Controller", manager[1].Name);

        manager.Hear(Connection(first, false));
        manager.Hear(Connection(other, false));
        manager.UpdateState(0.01f);
        Assert.Equal(0, manager.ConnectedCount);

        var again = new GamepadDevice("DualSense Wireless Controller");
        manager.Hear(Connection(again, true));
        manager.UpdateState(0.01f);
        Assert.Equal(2, manager.Count);
        Assert.True(manager[0].IsConnected);
        Assert.False(manager[1].IsConnected);
        Assert.Same(again, manager[0].Device);
    }

    private static InputEvent Connection(GamepadDevice device, bool connected) => new()
    {
        Kind = connected ? InputEventKind.GamepadConnected : InputEventKind.GamepadDisconnected,
        Gamepad = device
    };

    private static InputEvent State(GamepadDevice device, GamepadSnapshot snapshot) => new()
    {
        Kind = InputEventKind.GamepadState, Gamepad = device, GamepadState = snapshot
    };
}
