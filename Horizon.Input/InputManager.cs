using Bogz.Logging;

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

    // The script that is being played, the gamepad it is played on and how far into it we are
    private InputScript? _script;
    private Gamepad? _scriptPad;
    private float _scriptTime;

    public InputManager()
    {
        Name = "Input Manager";
    }

    /// <summary>
    /// Plays a script of button presses on a gamepad of its own from now on, see <see cref="InputScript"/>. Null stops the one that is playing.
    /// </summary>
    public void Play(InputScript? script)
    {
        _script = script;
        _scriptTime = 0.0f;

        if (script is not null) _scriptPad ??= Gamepads.AddVirtualGamepad("Script");
        else _scriptPad?.Update(default);
    }

    /// <summary>
    /// Helper method to play the script somebody named in the environment, if they did.
    /// </summary>
    private void PlayScriptOfEnvironment()
    {
        if (Environment.GetEnvironmentVariable(InputScript.ENVIRONMENT_VARIABLE) is not { Length: > 0 } path) return;

        if (!File.Exists(path))
        {
            Log.Warning($"[{Name}] There is no input script at '{path}'.");
            return;
        }

        InputScript script = InputScript.Load(path, out List<string> problems);
        foreach (string problem in problems)
            Log.Warning($"[{Name}] {path}: {problem}");

        Log.Info($"[{Name}] Playing the input script '{path}', {script.Length:0.0} seconds of it.");
        Play(script);
    }

    /// <summary>
    /// Helper method to move the script along and have its gamepad hold whatever it says is held right now.
    /// </summary>
    private void UpdateScript(float dt)
    {
        if (_script is null || _scriptPad is null) return;

        _scriptTime += dt;
        _scriptPad.Update(_script.At(_scriptTime));

        if (_script.WantsQuit(_scriptTime))
        {
            _script = null;
            Parent.GetComponent<WindowManager>()?.Close();
        }
    }

    public override void Initialize()
    {
        WindowManager window = Parent.GetComponent<WindowManager>()
            ?? throw new InvalidOperationException("The input manager goes on the entity that has the window manager, which is the engine.");
        IInputContext context = window.Input;

        // The devices are refreshed on the thread of the window, and looked at there, every time it has heard from the system
        window.EventsProcessed += Gamepads.SampleDevices;

        Native = context;

        Keyboard.Attach(context);
        Mouse.Attach(context);

        // Before the gamepads of the window are let in, so the one of a script is the first there is
        PlayScriptOfEnvironment();
        Gamepads.Attach(context);
    }

    public override void UpdateState(float dt)
    {
        // Switched off, nothing is read and whatever was pressed stays where it was (development: "InputManager listens to Enabled")
        if (!Enabled) return;

        Keyboard.Update();
        Mouse.Update();
        UpdateScript(dt);
        Gamepads.UpdateState(dt);
    }
}
