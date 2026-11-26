using Horizon.Logging;

using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Platform;

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
public sealed partial class InputManager : GameComponent, IDisposable
{
    /// <summary>
    /// The platform's input, for whoever needs something of the devices themselves (the clipboard). Null until the
    /// engine is up, and for a manager that was never handed one.
    /// </summary>
    internal IPlatformInput? Platform { get; private set; }

    public Keyboard Keyboard { get; } = new();

    public Mouse Mouse { get; } = new();

    public Touch Touch { get; } = new();

    /// <summary>
    /// Every gamepad that is plugged in, and the bindings each one is played with. It is there from the start, whether
    /// or not <see cref="ReadsGamepads"/> is on, so a game can hand out bindings and virtual pads before the engine is up.
    /// </summary>
    public GamepadInputManager Gamepads { get; } = new();

    /// <summary>
    /// Whether the gamepads plugged into the machine are listened for, see <see cref="Engine.GameEngineConfiguration.Gamepads"/>.
    /// Off, <see cref="Gamepads"/> is never shown the window's devices or sampled, so it only ever has the keyboard,
    /// virtual and scripted ones in it, and those still update.
    /// </summary>
    public bool ReadsGamepads { get; init; } = true;

    // The script that is being played, the gamepad it is played on and how far into it we are
    private InputScript? _script;
    private Gamepad? _scriptPad;
    private float _scriptTime;
    private IPlatformGamepads? _gamepadSource;
    private WindowManager? _window;

    public InputManager()
    {
        Name = "Input Manager";
        Keyboard.EventSink = Post;
    }

    /// <summary>
    /// Plays a script of button presses on a gamepad of its own from now on, see <see cref="InputScript"/>. Null stops the one that is playing.
    /// </summary>
    public void Play(InputScript? script)
    {
        foreach (var key in _scriptKeys.Keys) Post(InputEvent.KeyChange(key, false));
        _scriptKeys.Clear();
        _script = script;
        _scriptTime = 0.0f;
        _scriptKeysStarted = false;

        // In the first slot nothing is plugged into, so it is player one whatever a bindings file had saved there
        if (script is { HasButtons: true }) _scriptPad ??= Gamepads.AddVirtualGamepad("Script", takeFreeSlot: true);
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
        if (_script is null || !float.IsFinite(dt) || dt < 0.0f) return;

        float from = _scriptTime;
        _scriptTime += dt;
        _scriptPad?.Update(_script.At(_scriptTime));
        PlayScriptKeys(_scriptKeysStarted ? from : float.NegativeInfinity, _scriptTime);
        _scriptKeysStarted = true;

        if (_script.WantsQuit(_scriptTime))
        {
            Play(null);
            Parent?.GetComponent<WindowManager>()?.Close();
        }
    }

    // The keys the script holds down on the keyboard, by how many of its steps hold each, the keys going down and up
    // since the last update in the order they do, and whether an update has played any of it yet (one at the very start
    // of the script goes down on the first)
    private readonly Dictionary<Key, int> _scriptKeys = [];
    private readonly List<(float Time, Key Key, bool Down)> _scriptEdges = [];
    private bool _scriptKeysStarted;

    /// <summary>
    /// Helper method to play the keys of the script on the real keyboard, every one that went down or came up since the
    /// last update in the order the script has them, modifiers down first and up last where they go at once, so a chord
    /// keeps its meaning. Not just the keys held now: an idle program updates four times a second, and a key tapped
    /// between two of them (the F of a Ctrl+F, held 0.08 s) was never pressed at all. Down and up in the one update is a
    /// tap, which the keyboard has pressed for that update and let go of at the next.
    /// </summary>
    private void PlayScriptKeys(float after, float upTo)
    {
        _script!.KeyEdges(after, upTo, _scriptEdges);
        if (_scriptEdges.Count == 0) return;
        if (_scriptEdges.Count > 1)
            _scriptEdges.Sort(static (a, b) => a.Time != b.Time ? a.Time.CompareTo(b.Time) : Order(a).CompareTo(Order(b)));

        foreach (var (_, key, down) in _scriptEdges)
        {
            int holds = _scriptKeys.GetValueOrDefault(key);
            if (down)
            {
                _scriptKeys[key] = holds + 1;
                if (holds == 0) Keyboard.Hear(key, true);
            }
            else if (holds > 0)
            {
                if (holds == 1)
                {
                    _scriptKeys.Remove(key);
                    Keyboard.Hear(key, false);
                }
                else _scriptKeys[key] = holds - 1;
            }
        }

        // At the same moment, what comes up before what goes down (a key held from when another is let go of isn't
        // pressed with it), modifiers up after the other keys and down before them
        static int Order((float Time, Key Key, bool Down) edge) => edge.Down ? (IsModifier(edge.Key) ? 2 : 3) : (IsModifier(edge.Key) ? 1 : 0);
    }

    public override void Initialize()
    {
        WindowManager window = Parent.GetComponent<WindowManager>()
            ?? throw new InvalidOperationException("The input manager goes on the entity that has the window manager, which is the engine.");
        IPlatformInput input = window.Host.Input;
        _window = window;

        Platform = input;

        Mouse.Attach(input, window.PostToWindow);
        if (_source is null) AttachSource(input);

        // Before the gamepads of the platform are let in, so the one of a script is the first there is
        PlayScriptOfEnvironment();

        // Off, the platform's devices are left alone entirely, asking it for its gamepads is what wakes the controller
        // drivers up and a broken one can stall startup. The manager itself is just a list, so it stays
        if (!ReadsGamepads) return;

        // The devices are refreshed on the platform's thread, and looked at there, every time it has heard from the system
        _gamepadSource = input.OpenGamepads();
        if (_gamepadSource is null) return;
        _gamepadSource.EventReceived += Post;
        _gamepadSource.Start();
    }

    public override void UpdateState(float dt)
    {
        // Switched off, nothing is read and whatever was pressed stays where it was (development said "InputManager listens to Enabled", so it bloody well does)
        if (!Enabled) return;

        UpdateScript(dt);
        DrainEvents();
        Keyboard.Update();
        Mouse.Update();

        // Always, the keyboard, virtual and scripted pads move on here even when no real one is read
        Gamepads.UpdateState(dt);
    }

    public void Dispose()
    {
        // The platform's input is the host's to let go of, a manager only stops listening to it
        if (_source is not null) _source.EventReceived -= Post;
        if (_gamepadSource is not null)
        {
            _gamepadSource.EventReceived -= Post;
            _gamepadSource.Dispose();
        }
        Gamepads.Dispose();
        _source = null;
        _gamepadSource = null;
        _window = null;
        Platform = null;
        EventReceived = null;
    }
}
