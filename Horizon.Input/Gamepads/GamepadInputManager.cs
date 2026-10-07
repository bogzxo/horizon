using System.Collections.Concurrent;
using Horizon.Core;
using Horizon.HIDL;
using Horizon.HIDL.Runtime;
using Silk.NET.Input;

namespace Horizon.Input;

/// <summary>
/// Keeps track of every gamepad that is plugged in, and of the bindings each one is played with.
/// The engine has one of these already (<c>Engine.Input.Gamepads</c>), a game hardly ever needs to make its own.
/// Every gamepad gets a <see cref="Gamepad.Slot"/> that it keeps when it is unplugged and plugged back in, and
/// bindings of its own that start out as a copy of <see cref="DefaultBindings"/>. Games ask a gamepad for actions
/// ("jump") rather than buttons, and change what an action is bound to per gamepad:
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

    private IInputContext? _inputContext;
    private readonly List<Gamepad> _gamepads = [];
    private readonly List<string> _actionScratch = [];

    // Devices come and go on the thread of the window, the gamepads are only ever touched during an update.
    private readonly ConcurrentQueue<(IGamepad Device, bool Connected)> _connectionChanges = new();

    /// <summary>
    /// Every gamepad there has been a use for so far, by slot: the ones plugged in, the ones that were, and the
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

    // Event driven callbacks, raised during the update
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
        if (_inputContext is not null) return;

        for (Entity? at = Parent; at is not null; at = at.Parent)
        {
            if (at.GetComponent<WindowManager>() is not { Input: { } context }) continue;

            Attach(context);
            return;
        }

        throw new InvalidOperationException("A gamepad manager has to be added somewhere under the engine, it reads the gamepads of its window.");
    }

    /// <summary>
    /// Helper method to start listening to the gamepads of a window. Once is enough.
    /// </summary>
    internal void Attach(IInputContext context)
    {
        if (_inputContext is not null) return;

        _inputContext = context;
        _inputContext.ConnectionChanged += OnConnectionChanged;

        // The ones that were plugged in before we got here
        foreach (var gamepad in _inputContext.Gamepads)
        {
            if (gamepad.IsConnected)
                _connectionChanges.Enqueue((gamepad, true));
        }
    }

    public override void UpdateState(float dt)
    {
        while (_connectionChanges.TryDequeue(out var change))
        {
            if (change.Connected)
                Attach(change.Device);
            else
                Detach(change.Device);
        }

        foreach (var gamepad in _gamepads)
        {
            // Virtual gamepads are moved on by whoever drives them.
            if (!gamepad.IsVirtual)
                gamepad.Poll();
        }

        foreach (var gamepad in _gamepads)
        {
            if (gamepad.IsConnected && gamepad.Pressed is not null)
                _lastUsed = gamepad;
        }

        if (OnActionPressed is not null || OnActionReleased is not null)
            RaiseActionEvents();

        base.UpdateState(dt);
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
    /// Adds a gamepad that isn't a device: it takes a slot and has bindings like any other, and reads as whatever
    /// the game hands to <see cref="Gamepad.Update(in GamepadSnapshot)"/>. For replays, for letting an AI play
    /// through the same code a player does, and for tests.
    /// </summary>
    public Gamepad AddVirtualGamepad(string name = "Virtual Gamepad")
    {
        var gamepad = new Gamepad(_gamepads.Count, Sanitize(name), DefaultBindings.Clone(), isVirtual: true);
        _gamepads.Add(gamepad);
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
    /// Everything there is to save as a HIDL object: the default bindings, and the bindings of every slot
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

    /// <summary>Saves the bindings of every gamepad to a HIDL file.</summary>
    public void Save(string path) => File.WriteAllText(path, ToText());

    /// <summary>
    /// Loads what <see cref="Save"/> wrote, or a file somebody wrote by hand. Nothing changes if the file
    /// isn't there or can't be evaluated; a file that only gets some things wrong is taken over for the rest.
    /// </summary>
    /// <param name="problems">What was wrong with the file, if anything. Empty if it loaded cleanly.</param>
    /// <returns>Whether the file was there and declared the gamepads.</returns>
    public bool Load(string path, out List<string> problems)
    {
        if (!File.Exists(path))
        {
            problems = [$"There is no file at '{path}'."];
            return false;
        }

        return LoadText(File.ReadAllText(path), out problems);
    }

    /// <summary>Loads the bindings of every gamepad from the text of a HIDL file, see <see cref="Load"/>.</summary>
    public bool LoadText(string text, out List<string> problems)
    {
        problems = [];

        // A runtime of its own: nothing a bindings file declares has any business in another script's scope.
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

    private void OnConnectionChanged(IInputDevice device, bool isConnected)
    {
        if (device is IGamepad gamepad)
            _connectionChanges.Enqueue((gamepad, isConnected));
    }

    private void Attach(IGamepad device)
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

        OnGamepadConnected?.Invoke(gamepad);
    }

    private void Detach(IGamepad device)
    {
        foreach (var gamepad in _gamepads)
        {
            if (!ReferenceEquals(gamepad.Device, device))
                continue;

            // The slot stays, and reads as nothing held from the next update on.
            gamepad.Device = null;
            OnGamepadDisconnected?.Invoke(gamepad);
            return;
        }
    }

    /// <summary>
    /// The slot a device that was just plugged in goes to: the lowest one that is waiting for a device of that
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
        if (_inputContext != null)
        {
            _inputContext.ConnectionChanged -= OnConnectionChanged;
        }

        foreach (var gamepad in _gamepads)
            gamepad.Device = null;
        _gamepads.Clear();

        base.DisposeOther();
    }
}
