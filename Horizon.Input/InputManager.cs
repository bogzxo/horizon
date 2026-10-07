using Horizon.Core;
using Horizon.Core.Components;

using Silk.NET.Input;

namespace Horizon.Input;

/// <summary>
/// Everything the player can press, in one place. The engine has one of these, which games get at through <c>Engine.Input</c>.
/// <code>
/// if (Engine.Input.Keyboard.WasPressed(Key.Escape)) Pause();
/// Vector2 aim = Engine.Input.Mouse.Position;
/// if (Engine.Input.Gamepads.WasPressed("jump")) Jump();
/// </code>
/// All of it moves on once an update, before anything else of the game is updated. So everything that reads it in the same
/// update sees the same thing, and <see cref="Keyboard.WasPressed"/> and its like are true for exactly one of them.
/// </summary>
public sealed class InputManager : GameComponent
{
    /// <summary>
    /// The input context of the window underneath, for whoever needs something raw (typed text, the clipboard).
    /// </summary>
    public IInputContext? Native { get; private set; }

    public Keyboard Keyboard { get; } = new();

    public Mouse Mouse { get; } = new();

    /// <summary>
    /// Every gamepad that is plugged in, and the bindings each one is played with.
    /// </summary>
    public GamepadInputManager Gamepads { get; } = new();

    public InputManager()
    {
        Name = "Input Manager";
    }

    public override void Initialize()
    {
        IInputContext context = Parent.GetComponent<WindowManager>()?.Input
            ?? throw new InvalidOperationException("The input manager goes on the entity that has the window manager, which is the engine.");

        Native = context;

        Keyboard.Attach(context);
        Mouse.Attach(context);
        Gamepads.Attach(context);
    }

    public override void UpdateState(float dt)
    {
        Keyboard.Update();
        Mouse.Update();
        Gamepads.UpdateState(dt);
    }
}
