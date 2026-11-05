using System.Numerics;
using Horizon.Input;

namespace Horizon.Tests.Core;

public class PlatformInputTests
{
    private sealed class Source : IInputSource
    {
        public event Action<InputEvent>? EventReceived;
        public void Send(InputEvent input) => EventReceived?.Invoke(input);
    }

    [Fact]
    public void Short_key_tap_is_held_for_one_tick_then_released()
    {
        var source = new Source();
        var input = new InputManager();
        input.AttachSource(source);
        source.Send(InputEvent.KeyChange(Key.A, true));
        source.Send(InputEvent.KeyChange(Key.A, false));
        Assert.False(input.Keyboard.IsDown(Key.A));
        input.UpdateState(0.01f);
        Assert.True(input.Keyboard.WasPressed(Key.A));
        Assert.True(input.Keyboard.IsDown(Key.A));
        input.UpdateState(0.01f);
        Assert.False(input.Keyboard.IsDown(Key.A));
        Assert.False(input.Keyboard.WasPressed(Key.A));
        Assert.True(input.Keyboard.WasReleased(Key.A));
        input.UpdateState(0.01f);
        Assert.False(input.Keyboard.WasReleased(Key.A));
    }

    [Fact]
    public void Held_keys_do_not_press_again_on_repeat_events()
    {
        var input = new InputManager();
        input.Post(InputEvent.KeyChange(Key.Space, true));
        input.UpdateState(0.01f);
        input.Post(InputEvent.KeyChange(Key.Space, true));
        input.UpdateState(0.01f);
        Assert.True(input.Keyboard.IsDown(Key.Space));
        Assert.False(input.Keyboard.WasPressed(Key.Space));
    }

    [Fact]
    public void Events_keep_order_timestamps_and_modifiers_when_a_chord_is_released_before_the_tick()
    {
        var input = new InputManager();
        var received = new List<InputEvent>();
        input.EventReceived += received.Add;
        input.Post(InputEvent.KeyChange(Key.ControlLeft, true));
        input.Post(InputEvent.KeyChange(Key.A, true) with { Timestamp = 123 });
        input.Post(InputEvent.Typed("é🙂"));
        input.Post(InputEvent.KeyChange(Key.A, false));
        input.Post(InputEvent.KeyChange(Key.ControlLeft, false));
        Assert.Empty(received);
        input.UpdateState(0.01f);
        Assert.Equal(KeyModifiers.Control, received[1].Modifiers);
        Assert.Equal(123, received[1].Timestamp);
        Assert.Equal("é🙂", received[2].Text);
        Assert.Equal(KeyModifiers.None, received[4].Modifiers);
    }

    [Fact]
    public void Mouse_live_state_updates_before_the_tick_and_short_click_survives()
    {
        var input = new InputManager();
        input.Post(InputEvent.Pointer(InputEventKind.PointerDown, new Vector2(15, 25)));
        Assert.Equal(new Vector2(15, 25), input.Mouse.LivePosition);
        Assert.True(input.Mouse.IsLiveDown(MouseButton.Left));
        Assert.False(input.Mouse.IsDown(MouseButton.Left));
        input.Post(InputEvent.Pointer(InputEventKind.PointerUp, new Vector2(17, 27)));
        input.UpdateState(0.01f);
        Assert.True(input.Mouse.WasPressed(MouseButton.Left));
        Assert.False(input.Mouse.IsLiveDown(MouseButton.Left));
        Assert.Equal(new Vector2(17, 27), input.Mouse.Position);
        input.UpdateState(0.01f);
        Assert.True(input.Mouse.WasReleased(MouseButton.Left));
    }

    [Fact]
    public void Wheel_accumulates_for_one_tick()
    {
        var input = new InputManager();
        input.Post(new InputEvent { Kind = InputEventKind.Scroll, ScrollY = 1.5f });
        input.Post(new InputEvent { Kind = InputEventKind.Scroll, ScrollY = -0.5f });
        input.UpdateState(0.01f);
        Assert.Equal(1, input.Mouse.Scroll);
        input.UpdateState(0.01f);
        Assert.Equal(0, input.Mouse.Scroll);
    }

    [Fact]
    public void Touch_ids_are_independent_and_cancelled_contacts_release_for_one_tick()
    {
        var input = new InputManager();
        input.Post(InputEvent.Pointer(InputEventKind.PointerDown, Vector2.One, PointerKind.Touch, 7));
        input.Post(InputEvent.Pointer(InputEventKind.PointerDown, Vector2.Zero, PointerKind.Touch, 42));
        input.Post(InputEvent.Pointer(InputEventKind.PointerMove, new Vector2(3, 4), PointerKind.Touch, 7));
        input.Post(InputEvent.Pointer(InputEventKind.PointerDown, Vector2.One, PointerKind.Pen, 9));
        input.UpdateState(0.01f);
        // A pen is a contact of its own kind, the mouse never moves for either
        Assert.Equal(3, input.Touch.Points.Count);
        Assert.Equal(PointerKind.Pen, input.Touch.Points[2].Kind);
        Assert.Equal(new Vector2(3, 4), input.Touch.Points[0].Position);
        Assert.True(input.Touch.Points[0].WasPressed);
        Assert.False(input.Mouse.IsDown(MouseButton.Left));
        input.Post(InputEvent.Pointer(InputEventKind.PointerCancel, Vector2.Zero, PointerKind.Touch, 42));
        input.UpdateState(0.01f);
        Assert.False(input.Touch.Points[0].WasPressed);
        Assert.True(input.Touch.Points[1].WasReleased);
        Assert.True(input.Touch.Points[1].WasCancelled);
        input.UpdateState(0.01f);
        Assert.Equal([7, 9], input.Touch.Points.Select(point => point.Id));
    }

    [Fact]
    public void Stopping_a_script_releases_its_held_keys()
    {
        var input = new InputManager();
        input.Play(new InputScript().HoldKey(0, Key.A, 10));
        input.UpdateState(0.1f);
        Assert.True(input.Keyboard.IsDown(Key.A));
        input.Play(null);
        input.UpdateState(0.1f);
        Assert.True(input.Keyboard.WasReleased(Key.A));
        Assert.False(input.Keyboard.IsDown(Key.A));
    }

    [Fact]
    public void Mouse_cancel_releases_buttons_but_preserves_a_later_press()
    {
        var input = new InputManager();
        input.Post(InputEvent.Pointer(InputEventKind.PointerDown, Vector2.Zero));
        input.UpdateState(0.01f);
        input.Post(InputEvent.Pointer(InputEventKind.PointerCancel, Vector2.Zero));
        Assert.False(input.Mouse.IsLiveDown(MouseButton.Left));
        input.UpdateState(0.01f);
        Assert.False(input.Mouse.IsDown(MouseButton.Left));
        Assert.True(input.Mouse.WasReleased(MouseButton.Left));
        input.Post(InputEvent.Pointer(InputEventKind.PointerCancel, Vector2.Zero));
        input.Post(InputEvent.Pointer(InputEventKind.PointerDown, Vector2.One));
        input.UpdateState(0.01f);
        Assert.True(input.Mouse.IsLiveDown(MouseButton.Left));
        Assert.True(input.Mouse.WasPressed(MouseButton.Left));
    }

    [Fact]
    public void Script_releases_expired_modifiers_before_pressing_the_next_key()
    {
        var input = new InputManager();
        KeyModifiers? modifiers = null;
        input.EventReceived += heard =>
        {
            if (heard.Kind == InputEventKind.KeyDown && heard.Key == Key.A) modifiers = heard.Modifiers;
        };
        input.Play(new InputScript().HoldKey(0, Key.ControlLeft, 0.1f).HoldKey(0.1f, Key.A, 0.1f));
        input.UpdateState(0.05f);
        input.UpdateState(0.06f);
        Assert.Equal(KeyModifiers.None, modifiers);
    }

    [Fact]
    public void Host_modifier_snapshot_survives_without_an_observed_modifier_press()
    {
        var input = new InputManager();
        KeyModifiers? received = null;
        input.EventReceived += heard => received = heard.Modifiers;
        input.Post(InputEvent.KeyChange(Key.C, true) with { Modifiers = KeyModifiers.Control, HasModifierSnapshot = true });
        input.UpdateState(0.01f);
        Assert.Equal(KeyModifiers.Control, received);
    }

    [Fact]
    public void Input_identifiers_keep_every_native_name_and_value()
    {
        AssertEnumParity<Key, Silk.NET.Input.Key>();
        AssertEnumParity<MouseButton, Silk.NET.Input.MouseButton>();
    }

    private static void AssertEnumParity<TEngine, TNative>() where TEngine : struct, Enum where TNative : struct, Enum
    {
        Assert.Equal(Enum.GetNames<TNative>(), Enum.GetNames<TEngine>());
        foreach (string name in Enum.GetNames<TNative>())
            Assert.Equal(Convert.ToInt32(Enum.Parse<TNative>(name)), Convert.ToInt32(Enum.Parse<TEngine>(name)));
    }
}
