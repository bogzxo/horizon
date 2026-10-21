using Horizon.Input;

namespace Horizon.Tests.Core;

/// <summary>
/// With the real gamepads switched off the input manager still has a gamepad manager, so virtual and scripted pads work.
/// </summary>
public class InputManagerGamepadsTests
{
    [Fact]
    public void GamepadsAreThereWithoutReadingAny()
    {
        var input = new InputManager { ReadsGamepads = false };

        Assert.NotNull(input.Gamepads);
    }

    [Fact]
    public void AVirtualPadWorksWithoutReadingAny()
    {
        var input = new InputManager { ReadsGamepads = false };
        input.Gamepads.DefaultBindings.Bind("jump", GamepadInput.A);

        Gamepad pad = input.Gamepads.AddVirtualGamepad("Test", takeFreeSlot: true);
        Assert.Equal(1, input.Gamepads.Count);

        pad.Update(GamepadSnapshot.Holding(GamepadInput.A));
        Assert.True(pad.IsDown("jump"));
    }
}
