using System.Collections.Concurrent;
using Horizon.Core;
using Horizon.HIDL;
using Horizon.HIDL.Runtime;
using Horizon.Logging;
using Horizon.Platform;

namespace Horizon.Input;

/// <summary>
/// Keeps track of every gamepad that is plugged in, and of the bindings each one is played with.
/// The engine has one of these already (<c>Engine.Input.Gamepads</c>), a game hardly ever needs to make its own.
/// Every gamepad gets a <see cref="Gamepad.Slot"/> that it keeps when it is unplugged and plugged back in, and
/// bindings of its own that start out as a copy of <see cref="DefaultBindings"/>. Games ask a gamepad for actions
/// ("jump") rather than buttons, and change what an action is bound to per gamepad.
/// <code>
/// var gamepads = Engine.Input.Gamepads;
/// gamepads.DefaultBindings.Bind("jump", GamepadInput.A).Bind("attack", GamepadInput.X, GamepadInput.RightTrigger);
/// ...
/// if (gamepads[0].WasPressed("jump")) Jump();
/// gamepads[1].Bindings.Rebind("jump", GamepadInput.B);
/// gamepads.Save("gamepads.hor");
/// </code>
/// All of it is saved to and loaded from a HIDL file, see <see cref="ToValue"/> for what one looks like.
/// The state of the gamepads moves on once per <see cref="UpdateState"/>, and is meant to be read (and the
/// bindings changed) from the thread that calls it.
/// </summary>
public class GamepadInputManager : Entity
{
    /// <summary>The variable a saved file declares.</summary>
    public const string FILE_VARIABLE = "gamepads";

    private const string KEY_DEFAULTS = "defaults";
    private const string KEY_PADS = "pads";
    private const string KEY_NAME = "name";
    private const string KEY_BINDINGS = "bindings";
    private const string SLOT_PREFIX = "pad";

    private Horizon.Platform.IPlatformGamepads? _source;
    private WindowManager? _window;
    private readonly List<Gamepad> _gamepads = [];

    // The keyboards that are read as gamepads, moved on at the start of every update
    private readonly List<KeyboardGamepad> _keyboards = [];
    private readonly List<string> _actionScratch = [];

    // Devices come and go on the thread of the window, the gamepads are only ever touched during an update.
    private readonly ConcurrentQueue<InputEvent> _events = new();

    /// <summary>
    /// Every gamepad there has been a use for so far, by slot. The ones plugged in, the ones that were, and the
    /// ones a loaded file had bindings for. See <see cref="Gamepad.IsConnected"/> for which is which.
    /// </summary>
    public GamepadList Gamepads => new(_gamepads);

    /// <summary>
    /// The gamepad in a slot. See <see cref="TryGet"/> for a slot that may not have one.
    /// </summary>
    public Gamepad this[int slot] => _gamepads[slot];

    /// <summary>
    /// How many slots there are, plugged in or not.
    /// </summary>
    public int Count => _gamepads.Count;

    /// <summary>
    /// So the manager can be walked with a foreach, which gives every gamepad there is a slot for.
    /// </summary>
    public List<Gamepad>.Enumerator GetEnumerator() => _gamepads.GetEnumerator();

    /// <summary>
    /// The bindings a gamepad starts out with when it takes a new slot. Changing them afterwards changes nothing
    /// about the gamepads that are already there, see <see cref="ResetBindings"/> for that.
    /// </summary>
    public GamepadBindings DefaultBindings { get; } = new();

    private Gamepad? _lastUsed;

    // HORIZON_INPUT_LOG=gamepads (or on) writes every press and release on every pad to the log, so a run on a phone
    // shows what a pad did and which one. Capped, a stuck pad would fill the file otherwise
    private static readonly bool LogsInputs = System.Environment.GetEnvironmentVariable("HORIZON_INPUT_LOG") is { } wanted
        && (wanted.Equals("gamepads", StringComparison.OrdinalIgnoreCase) || wanted.Equals("on", StringComparison.OrdinalIgnoreCase) || wanted == "1");
    private const int LOG_LINES_AT_MOST = 600;
    private int _logged;

    /// <summary>
    /// The gamepad something was pressed on last, or the first one there is if nothing has been yet.
    /// For things that aren't about one player in particular, like which buttons a menu shows in its hints.
    /// Null if there is no gamepad at all.
    /// </summary>
    public Gamepad? LastUsed => _lastUsed is { IsConnected: true } ? _lastUsed : First;

    /// <summary>How many gamepads can be read right now.</summary>
    public int ConnectedCount
    {
        get
        {
            int count = 0;
            foreach (var gamepad in _gamepads)
            {
                if (gamepad.IsConnected)
                    count++;
            }
            return count;
        }
    }

    // Raised during the update, when one is plugged in and when one is pulled out
    public event Action<Gamepad>? OnGamepadConnected;
    public event Action<Gamepad>? OnGamepadDisconnected;

    /// <summary>Raised when an action starts on a gamepad, see <see cref="Gamepad.WasPressed(string)"/>.</summary>
    public event Action<Gamepad, string>? OnActionPressed;

    /// <summary>Raised when an action ends on a gamepad, see <see cref="Gamepad.WasReleased(string)"/>.</summary>
    public event Action<Gamepad, string>? OnActionReleased;

    public GamepadInputManager()
    {
        Name = "Gamepad Input Manager";
    }

    public override void Initialize()
    {
        base.Initialize();

        // The one of the engine is handed its window. One that somebody made themselves finds it above where it was added
        if (_source is not null) return;

        for (Entity? at = Parent; at is not null; at = at.Parent)
        {
            if (at.GetComponent<WindowManager>() is not { } window) continue;
            _window = window;

            // Sampled by the platform on its thread from the start on, every time it has heard from the system
            _source = window.Host.Input.OpenGamepads();
            if (_source is null) return;
            _source.EventReceived += Hear;
            _source.Start();
            return;
        }

        throw new InvalidOperationException("A gamepad manager has to be added somewhere under the engine, it reads the gamepads of its window.");
    }

    /// <summary>
    /// Queues one host event for the next simulation update.
    /// </summary>
    internal void Hear(InputEvent input) => _events.Enqueue(input);

    public override void UpdateState(float dt)
    {
        while (_events.TryDequeue(out var input))
        {
            if (input.Gamepad is not { } device) continue;
            switch (input.Kind)
            {
                case InputEventKind.GamepadConnected:
                    Attach(device);
                    break;
                case InputEventKind.GamepadDisconnected:
                    Detach(device);
                    break;
                case InputEventKind.GamepadState:
                    foreach (var gamepad in _gamepads)
                    {
                        if (ReferenceEquals(gamepad.Device, device))
                        {
                            gamepad.Sample(input.GamepadState);
                            break;
                        }
                    }
                    break;
            }
        }

        foreach (var gamepad in _gamepads)
        {
            // Virtual gamepads are moved on by whoever drives them.
            if (!gamepad.IsVirtual)
                gamepad.Poll();
        }

        // Which for the keyboard is us, off the keys as the update before this one left them
        foreach (KeyboardGamepad keyboard in _keyboards)
            keyboard.Update();

        if (LogsInputs) LogChanges();

        foreach (var gamepad in _gamepads)
        {
            if (gamepad.IsConnected && gamepad.Pressed is not null)
                _lastUsed = gamepad;
        }

        if (OnActionPressed is not null || OnActionReleased is not null)
            RaiseActionEvents();

        base.UpdateState(dt);
    }

    // What went down and what came up this update on every pad, with where the sticks and the triggers are, one line
    // each. A virtual pad (a script's) and the keyboard say so, so a run can tell a script's presses from a real pad's
    private void LogChanges()
    {
        foreach (var gamepad in _gamepads)
        {
            if (!gamepad.IsConnected) continue;
            uint changed = gamepad.HeldMask ^ gamepad.HeldBeforeMask;
            if (changed == 0) continue;

            if (_logged >= LOG_LINES_AT_MOST)
            {
                if (_logged == LOG_LINES_AT_MOST) Log.Info($"[Gamepads] {LOG_LINES_AT_MOST} changes logged, no more of these this run.");
                _logged++;
                return;
            }

            for (int bit = 0; bit < 32 && changed != 0; bit++, changed >>= 1)
            {
                if ((changed & 1) == 0) continue;
                var input = (GamepadInput)bit;
                string what = gamepad.IsDown(input) ? "down" : "up";
                string virtually = gamepad.Kind == GamepadKind.Keyboard ? " (keyboard)" : gamepad.IsVirtual ? " (virtual)" : string.Empty;
                Log.Info($"[Gamepads] slot {gamepad.Slot} '{gamepad.Name}'{virtually} {input} {what}, left stick {gamepad.LeftStick.X:0.00},{gamepad.LeftStick.Y:0.00}, right {gamepad.RightStick.X:0.00},{gamepad.RightStick.Y:0.00}, triggers {gamepad.LeftTrigger:0.00}/{gamepad.RightTrigger:0.00}");
                _logged++;
            }
        }
    }

    /// <summary>The gamepad in a slot, if there is one.</summary>
    public bool TryGet(int slot, out Gamepad gamepad)
    {
        gamepad = (uint)slot < (uint)_gamepads.Count ? _gamepads[slot] : null!;
        return gamepad is not null;
    }

    /// <summary>The connected gamepad in the lowest slot, or null if there is none.</summary>
    public Gamepad? First
    {
        get
        {
            foreach (var gamepad in _gamepads)
            {
                if (gamepad.IsConnected)
                    return gamepad;
            }
            return null;
        }
    }

    /// <summary>Whether an action is held on any gamepad, for things like menus that anyone may drive.</summary>
    public bool IsDown(string action)
    {
        foreach (var gamepad in _gamepads)
        {
            if (gamepad.IsDown(action))
                return true;
        }
        return false;
    }

    /// <summary>Whether an action started on any gamepad this update.</summary>
    public bool WasPressed(string action) => WasPressed(action, out _);

    /// <summary>Whether an action started on any gamepad this update, and on which.</summary>
    public bool WasPressed(string action, out Gamepad by)
    {
        foreach (var gamepad in _gamepads)
        {
            if (gamepad.WasPressed(action))
            {
                by = gamepad;
                return true;
            }
        }

        by = null!;
        return false;
    }

    /// <summary>
    /// Adds a gamepad that isn't a device. It takes a slot and has bindings like any other, and reads as whatever
    /// the game hands to <see cref="Gamepad.Update(in GamepadSnapshot)"/>. For replays, for letting an AI play
    /// through the same code a player does, and for tests.
    /// </summary>
    /// <param name="takeFreeSlot">
    /// Whether to take the lowest slot nothing is plugged into (one a bindings file loaded for a gamepad that isn't
    /// there right now), with that slot's bindings, rather than a new slot at the end. An input script wants that, it
    /// is meant to be player one, and with a saved file it used to land in slot two behind a controller that wasn't
    /// even plugged in. A device of that name plugged in later takes a new slot.
    /// </param>
    public Gamepad AddVirtualGamepad(string name = "Virtual Gamepad", bool takeFreeSlot = false)
    {
        Gamepad gamepad;
        if (takeFreeSlot && _gamepads.Find(known => !known.IsConnected) is { } waiting)
        {
            gamepad = new Gamepad(waiting.Slot, Sanitize(name), waiting.Bindings.Clone(), isVirtual: true);
            _gamepads[waiting.Slot] = gamepad;
        }
        else
        {
            gamepad = new Gamepad(_gamepads.Count, Sanitize(name), DefaultBindings.Clone(), isVirtual: true);
            _gamepads.Add(gamepad);
        }

        OnGamepadConnected?.Invoke(gamepad);
        return gamepad;
    }

    /// <summary>
    /// Adds the keyboard as a gamepad, see <see cref="KeyboardGamepad"/>. It takes the slot a loaded file had a
    /// gamepad of that name in, with the bindings it was saved with, or a new one. Call it after the bindings are
    /// loaded for that reason.
    /// </summary>
    /// <param name="keys">Which key is which input, <see cref="KeyboardGamepad.DefaultKeys"/> if null. That is what a menu
    /// reads (its yes, its no, its directions), the same keys as the keyboard icons of the skins so a hint shows what is pressed.</param>
    /// <param name="bindings">What the actions are on for the keyboard, keys straight (<see cref="GamepadBindings.BindKeys"/>),
    /// for a keyboard nobody has bound yet. The keyboard of a file saved before there were keys gets these too. Null for
    /// <see cref="DefaultBindings"/>, the actions then only go by the inputs the keys stand in for.</param>
    public Gamepad AddKeyboardGamepad(Keyboard keyboard, IReadOnlyList<(Key Key, GamepadInput Input)>? keys = null, string name = "Keyboard", GamepadBindings? bindings = null)
    {
        name = Sanitize(name);

        // A slot that is waiting for a device of this name is the keyboard of the last run. One saved with no keys in it
        // is from before a keyboard had keys of its own, which played a fight through the buttons its keys stood for
        int waiting = _gamepads.FindIndex(known => !known.IsConnected && known.Name == name);
        GamepadBindings fresh = bindings ?? DefaultBindings;
        var saved = waiting >= 0 ? _gamepads[waiting].Bindings : null;
        var chosen = saved is not null && (bindings is null || saved.HasKeys) ? saved.Clone() : fresh.Clone();
        var gamepad = new Gamepad(waiting >= 0 ? waiting : _gamepads.Count, name, chosen, isVirtual: true) { Kind = GamepadKind.Keyboard, Keyboard = keyboard };

        if (waiting >= 0) _gamepads[waiting] = gamepad;
        else _gamepads.Add(gamepad);

        _keyboards.Add(new KeyboardGamepad(gamepad, keyboard, keys ?? KeyboardGamepad.DefaultKeys));
        OnGamepadConnected?.Invoke(gamepad);
        return gamepad;
    }

    /// <summary>Gives every gamepad a fresh copy of <see cref="DefaultBindings"/>.</summary>
    public void ResetBindings()
    {
        foreach (var gamepad in _gamepads)
            gamepad.Bindings.CopyFrom(DefaultBindings);
    }

    /// <summary>
    /// Everything there is to save as a HIDL object, the default bindings, and the bindings of every slot
    /// together with the name of the device that was last in it.
    /// <code>
    /// {
    ///     defaults: { deadzone: 0.2, ..., actions: { jump: "A" } },
    ///     pads: {
    ///         pad0: { name: "Xbox Controller", bindings: { deadzone: 0.2, ..., actions: { jump: "B" } } }
    ///     }
    /// }
    /// </code>
    /// </summary>
    public ObjectValue ToValue()
    {
        Dictionary<string, IRuntimeValue> pads = [];
        foreach (var gamepad in _gamepads)
        {
            pads[SLOT_PREFIX + gamepad.Slot] = new ObjectValue(new Dictionary<string, IRuntimeValue>
            {
                [KEY_NAME] = new StringValue(gamepad.Name),
                [KEY_BINDINGS] = gamepad.Bindings.ToValue()
            });
        }

        return new ObjectValue(new Dictionary<string, IRuntimeValue>
        {
            [KEY_DEFAULTS] = DefaultBindings.ToValue(),
            [KEY_PADS] = new ObjectValue(pads)
        });
    }

    /// <summary>
    /// Takes over what <see cref="ToValue"/> gave at some point. Slots the object has bindings for but that no
    /// gamepad has taken yet are made, so the bindings are there when one is plugged in. Whatever the object
    /// doesn't mention stays as it is, an action added to the game since it was saved keeps its default.
    /// </summary>
    /// <param name="problems">Gets a line for everything in the object that couldn't be made sense of.</param>
    public void Apply(ObjectValue value, List<string>? problems = null)
    {
        if (value.Properties is null)
            return;

        if (value.Properties.TryGetValue(KEY_DEFAULTS, out var defaults) && defaults is ObjectValue defaultBindings)
            DefaultBindings.Apply(defaultBindings, problems);

        if (!value.Properties.TryGetValue(KEY_PADS, out var pads) || pads is not ObjectValue { Properties: { } saved })
            return;

        foreach (var (key, pad) in saved)
        {
            if (!key.StartsWith(SLOT_PREFIX) || !int.TryParse(key.AsSpan(SLOT_PREFIX.Length), out int slot) || slot < 0 || slot > 64
                || pad is not ObjectValue { Properties: { } properties })
            {
                problems?.Add($"'{key}' isn't the slot of a gamepad.");
                continue;
            }

            // Slots are their place in the list, so the ones in between have to be there too.
            while (_gamepads.Count <= slot)
                _gamepads.Add(new Gamepad(_gamepads.Count, string.Empty, DefaultBindings.Clone(), isVirtual: false));

            Gamepad gamepad = _gamepads[slot];

            // A gamepad that is plugged in knows its name better than a file does.
            if (!gamepad.IsConnected && properties.TryGetValue(KEY_NAME, out var name) && name is StringValue savedName)
                gamepad.Name = Sanitize(savedName.Value);

            if (properties.TryGetValue(KEY_BINDINGS, out var bindings) && bindings is ObjectValue savedBindings)
                gamepad.Bindings.Apply(savedBindings, problems);
        }
    }

    /// <summary>Everything there is to save as the text of a HIDL file, see <see cref="ToValue"/>.</summary>
    public string ToText() => HIDLWriter.WriteDeclaration(FILE_VARIABLE, ToValue()) + System.Environment.NewLine;

    /// <summary>Saves the bindings of every gamepad to a HIDL file in platform storage, the old one stays if the write fails.</summary>
    public void Save(string path) => PlatformStorage.WriteAllText(path, ToText());

    /// <summary>
    /// Loads what <see cref="Save"/> wrote, or a file somebody wrote by hand. Nothing changes if the file
    /// isn't there or can't be evaluated, and a file that only gets some things wrong is taken over for the rest.
    /// </summary>
    /// <param name="problems">What was wrong with the file, if anything. Empty if it loaded cleanly.</param>
    /// <returns>Whether the file was there and declared the gamepads.</returns>
    public bool Load(string path, out List<string> problems)
    {
        if (!PlatformStorage.Exists(path))
        {
            problems = [$"There is no file at '{path}'."];
            return false;
        }

        return LoadText(PlatformStorage.ReadAllText(path), out problems);
    }

    /// <summary>Loads the bindings of every gamepad from the text of a HIDL file, see <see cref="Load"/>.</summary>
    public bool LoadText(string text, out List<string> problems)
    {
        problems = [];

        // A runtime of its own, nothing a bindings file declares has any business in another script's scope.
        HIDLRuntime runtime = new();
        var (success, result) = runtime.Evaluate(text);

        if (!success)
        {
            problems.Add($"The file couldn't be evaluated: {result}");
            return false;
        }

        if (runtime.UserScope.Lookup(FILE_VARIABLE) is not ObjectValue value)
        {
            problems.Add($"The file doesn't declare '{FILE_VARIABLE}'.");
            return false;
        }

        Apply(value, problems);
        return true;
    }

    private void Attach(GamepadDevice device)
    {
        foreach (var known in _gamepads)
        {
            if (ReferenceEquals(known.Device, device))
                return;
        }

        string name = Sanitize(device.Name);
        Gamepad gamepad = FindFreeSlot(name);

        gamepad.Device = device;
        gamepad.Name = name;
        gamepad.ResetDevice();
        Log.Info($"[Gamepads] '{name}' is in slot {gamepad.Slot} ({gamepad.Kind}).");

        OnGamepadConnected?.Invoke(gamepad);
    }

    private void Detach(GamepadDevice device)
    {
        foreach (var gamepad in _gamepads)
        {
            if (!ReferenceEquals(gamepad.Device, device))
                continue;

            // The slot stays, and reads as nothing held from the next update on.
            gamepad.Device = null;
            gamepad.ResetDevice();
            Log.Info($"[Gamepads] '{gamepad.Name}' left slot {gamepad.Slot}, which holds nothing now and waits for it.");
            OnGamepadDisconnected?.Invoke(gamepad);
            return;
        }
    }

    /// <summary>
    /// The slot a device that was just plugged in goes to. The lowest one that is waiting for a device of that
    /// name (the same gamepad coming back, or the one a file was saved with), failing that the lowest one that
    /// is waiting at all, failing that a new one.
    /// </summary>
    private Gamepad FindFreeSlot(string name)
    {
        Gamepad? free = null;

        foreach (var gamepad in _gamepads)
        {
            if (gamepad.IsConnected)
                continue;

            if (gamepad.Name == name)
                return gamepad;

            free ??= gamepad;
        }

        if (free is not null)
            return free;

        var added = new Gamepad(_gamepads.Count, name, DefaultBindings.Clone(), isVirtual: false);
        _gamepads.Add(added);
        return added;
    }

    private void RaiseActionEvents()
    {
        foreach (var gamepad in _gamepads)
        {
            // A copy, so a handler is free to change the bindings (a menu that rebinds on the next press).
            _actionScratch.Clear();
            _actionScratch.AddRange(gamepad.Bindings.Actions);

            foreach (string action in _actionScratch)
            {
                if (gamepad.WasPressed(action))
                    OnActionPressed?.Invoke(gamepad, action);
                else if (gamepad.WasReleased(action))
                    OnActionReleased?.Invoke(gamepad, action);
            }
        }
    }

    // A HIDL string has no way of holding a quote, and the name ends up in one when it is saved.
    private static string Sanitize(string? name) => (name ?? string.Empty).Replace('"', '\'');

    protected override void DisposeOther()
    {
        if (_source is not null) _source.EventReceived -= Hear;
        _source?.Dispose();
        _source = null;
        _window = null;
        _events.Clear();

        foreach (var gamepad in _gamepads)
            gamepad.Device = null;
        _gamepads.Clear();

        base.DisposeOther();
    }
}
