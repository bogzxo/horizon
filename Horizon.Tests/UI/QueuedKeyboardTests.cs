using Horizon.Input;
using Horizon.UI;

namespace Horizon.Tests.UI;

[Collection("UI focus restoration")]
public class QueuedKeyboardTests
{
    [Fact]
    public void Multiple_pastes_keep_their_payloads_and_order_relative_to_text()
    {
        var input = new InputManager();
        UIKeyboard.Hook(input);
        while (UIKeyboard.TryRead(out _)) { }
        while (UIKeyboard.TryTakePaste(out _)) { }
        Key primary = Keyboard.PrimaryIsCommand ? Key.SuperLeft : Key.ControlLeft;
        input.Post(InputEvent.KeyChange(primary, true));
        input.Post(InputEvent.KeyChange(Key.V, true) with { ClipboardText = "first" });
        input.Post(InputEvent.KeyChange(Key.V, false));
        input.Post(InputEvent.KeyChange(primary, false));
        input.Post(InputEvent.Typed("x"));
        input.Post(InputEvent.KeyChange(primary, true));
        input.Post(InputEvent.KeyChange(Key.V, true) with { ClipboardText = "second" });
        input.Post(InputEvent.KeyChange(Key.V, false));
        input.Post(InputEvent.KeyChange(primary, false));
        input.UpdateState(0.01f);
        Assert.True(UIKeyboard.TryRead(out char first));
        Assert.Equal(UIKeyboard.PASTE, first);
        Assert.True(UIKeyboard.TryTakePaste(out string firstText));
        Assert.Equal("first", firstText);
        Assert.True(UIKeyboard.TryRead(out char text));
        Assert.Equal('x', text);
        Assert.True(UIKeyboard.TryRead(out char second));
        Assert.Equal(UIKeyboard.PASTE, second);
        Assert.True(UIKeyboard.TryTakePaste(out string secondText));
        Assert.Equal("second", secondText);
        Assert.False(UIKeyboard.TryRead(out _));
    }

    [Fact]
    public void Scripted_editing_keys_reach_ui_without_a_native_keyboard()
    {
        var input = new InputManager();
        UIKeyboard.Hook(input);
        while (UIKeyboard.TryRead(out _)) { }
        input.Play(new InputScript().TapKey(0, Key.Down));
        input.UpdateState(0.01f);
        Assert.True(UIKeyboard.TryRead(out char character));
        Assert.Equal(UIKeyboard.DOWN, character);
        Assert.False(UIKeyboard.TryRead(out _));
        input.Play(null);
        input.UpdateState(0.01f);
    }

    [Fact]
    public void Queued_chords_keep_modifiers_and_text_stays_in_order()
    {
        var input = new InputManager();
        UIKeyboard.Hook(input);
        while (UIKeyboard.TryRead(out _)) { }
        input.Keyboard.Tap([Key.ShiftLeft, Key.Left]);
        input.Post(InputEvent.Typed("é🙂"));
        input.UpdateState(0.01f);
        Assert.True(UIKeyboard.TryRead(out char command));
        Assert.Equal(UIKeyboard.SELECT_LEFT, command);
        var text = new List<char>();
        while (UIKeyboard.TryRead(out char character)) text.Add(character);
        Assert.Equal("é🙂", new string(text.ToArray()));
        input.UpdateState(0.01f);
    }
}
