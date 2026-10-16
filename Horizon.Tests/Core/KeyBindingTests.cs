using Horizon.HIDL.Runtime;
using Horizon.Input;

using Silk.NET.Input;

namespace Horizon.Tests.Core;

/// <summary>
/// A keyboard's actions bound to its keys straight, next to the buttons of a gamepad in the same bindings.
/// </summary>
public class KeyBindingTests
{
    private static Gamepad KeyboardPad(GamepadBindings bindings, Keyboard keyboard) =>
        new(0, "Keyboard", bindings, isVirtual: true) { Kind = GamepadKind.Keyboard, Keyboard = keyboard };

    [Fact]
    public void AKeyHoldsItsAction()
    {
        var keyboard = new Keyboard();
        var bindings = new GamepadBindings().BindKeys("kick", Key.J);
        var pad = KeyboardPad(bindings, keyboard);

        keyboard.Hear(Key.J, true);
        keyboard.Update();

        Assert.True(pad.IsDown("kick"));
        Assert.True(pad.WasPressed("kick"));

        keyboard.Update();
        Assert.True(pad.IsDown("kick"));
        Assert.False(pad.WasPressed("kick"));

        keyboard.Hear(Key.J, false);
        keyboard.Update();
        Assert.False(pad.IsDown("kick"));
        Assert.True(pad.WasReleased("kick"));
    }

    [Fact]
    public void AKeyComboWantsAllOfItsKeys()
    {
        var keyboard = new Keyboard();
        var bindings = new GamepadBindings().AddKeyCombination("dash", Key.ShiftLeft, Key.D);
        var pad = KeyboardPad(bindings, keyboard);

        keyboard.Hear(Key.D, true);
        keyboard.Update();
        Assert.False(pad.IsDown("dash"));

        keyboard.Hear(Key.ShiftLeft, true);
        keyboard.Update();
        Assert.True(pad.WasPressed("dash"));
    }

    [Fact]
    public void KeysAreSavedAndReadBackWithTheButtons()
    {
        var bindings = new GamepadBindings().Bind("jump", GamepadInput.DPadUp).BindKeys("jump", Key.W, Key.Space).AddKeyCombination("dash", Key.ShiftLeft, Key.D);

        Assert.Equal("key:W, key:Space, DPadUp", bindings.Describe("jump"));

        var read = new GamepadBindings();
        var problems = new List<string>();
        read.Apply(bindings.ToValue(), problems);

        Assert.Empty(problems);
        Assert.True(read.IsBound("jump", GamepadInput.DPadUp));
        Assert.Equal(2, read.KeyCombinationsOf("jump").Count);
        Assert.Equal([Key.ShiftLeft, Key.D], read.KeyCombinationsOf("dash")[0]);
        Assert.True(read.HasKeys);
    }

    [Fact]
    public void AKeyThatIsNoKeyIsReportedAndSkipped()
    {
        var value = new ObjectValue(new Dictionary<string, IRuntimeValue>
        {
            ["actions"] = new ObjectValue(new Dictionary<string, IRuntimeValue> { ["kick"] = new StringValue("key:J, key:Nonsense, key:J + A") })
        });

        var read = new GamepadBindings();
        var problems = new List<string>();
        read.Apply(value, problems);

        Assert.Single(read.KeyCombinationsOf("kick"));
        Assert.Equal(2, problems.Count);
    }

    [Fact]
    public void RebindingAKeyTakesItOffWhoeverHadIt()
    {
        var bindings = new GamepadBindings().BindKeys("kick", Key.J).BindKeys("punch", Key.U);

        bindings.RebindKeys("punch", Key.J);

        Assert.Empty(bindings.KeyCombinationsOf("kick"));
        Assert.Equal([Key.J], bindings.KeyCombinationsOf("punch")[0]);
    }

    [Fact]
    public void AKeyboardOfAnOldFileGetsTheKeyDefaults()
    {
        var manager = new GamepadInputManager();
        var keyboard = new Keyboard();

        // The keyboard as a file from before keys had it, bound to the buttons its keys stood for
        var old = new GamepadBindings().Bind("kick", GamepadInput.A);
        var file = new ObjectValue(new Dictionary<string, IRuntimeValue>
        {
            ["pads"] = new ObjectValue(new Dictionary<string, IRuntimeValue>
            {
                ["pad0"] = new ObjectValue(new Dictionary<string, IRuntimeValue> { ["name"] = new StringValue("Keyboard"), ["bindings"] = old.ToValue() })
            })
        });
        manager.Apply(file);

        var pad = manager.AddKeyboardGamepad(keyboard, bindings: new GamepadBindings().BindKeys("kick", Key.J));

        Assert.Equal(0, pad.Slot);
        Assert.True(pad.Bindings.HasKeys);
        Assert.False(pad.Bindings.IsBound("kick", GamepadInput.A));
    }
}
