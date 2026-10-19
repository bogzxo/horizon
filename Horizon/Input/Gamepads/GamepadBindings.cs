using System.Numerics;
using System.Text;

using Horizon.HIDL;
using Horizon.HIDL.Runtime;

using Silk.NET.Input;

namespace Horizon.Input;

/// <summary>
/// Which inputs of a gamepad trigger which actions, and how far a stick or a trigger has to be pushed to count.
/// Actions are whatever the game calls them ("jump", "attack"). An action is bound to any number of combinations,
/// each of them one input or several that have to be held together (A, or A + B), and is held while any one of
/// them is. A combination takes over from the smaller ones inside of it. With A + B bound to something, holding
/// both is that and neither what A nor what B is bound to by itself.
/// Every gamepad has bindings of its own, so two players can play with different ones.
/// Bindings are saved as a HIDL object, with the actions by name and their inputs by name.
/// <code>
/// {
///     deadzone: 0.2,
///     stick_threshold: 0.5,
///     trigger_threshold: 0.5,
///     actions: {
///         jump: "A, DPadUp",
///         attack: "X",
///         grab: "A + B"
///     }
/// }
/// </code>
/// An action name has to be an identifier (letters, digits and underscores) to be a key in there.
/// <para>
/// A gamepad that is the keyboard (<see cref="GamepadInputManager.AddKeyboardGamepad"/>) binds its actions to keys,
/// straight, written <c>key:</c> and the name of the key (<c>jump: "key:W, key:Space"</c>, <c>dash: "key:ShiftLeft + key:D"</c>),
/// next to the inputs of a gamepad in the same bindings. A combination of keys is held while all of its keys are, and
/// doesn't take over from a smaller one the way one of a gamepad's does, nobody chords a keyboard for that.
/// </para>
/// </summary>
public sealed class GamepadBindings
{
    private const string KEY_DEADZONE = "deadzone";
    private const string KEY_STICK_THRESHOLD = "stick_threshold";
    private const string KEY_TRIGGER_THRESHOLD = "trigger_threshold";
    private const string KEY_ACTIONS = "actions";

    // The combinations of every action, each as the bits of its inputs.
    private readonly Dictionary<string, List<uint>> actions = [];

    // The combinations of keys of every action that has any, each its keys in the order they were given. Only a keyboard reads these.
    private readonly Dictionary<string, List<Key[]>> keyActions = [];

    private const string KEY_PREFIX = "key:";

    // Every combination of more than one input that anything is bound to, these are the ones that can take over
    // from a smaller one. Worked out again when the bindings change, null until then.
    private uint[]? combinations;

    private float deadzone = 0.2f;
    private float stickThreshold = 0.5f;
    private float triggerThreshold = 0.5f;

    /// <summary>
    /// How far a stick has to be off its centre before it reads as anything at all, from 0 to just under 1.
    /// What is left of its travel is stretched back out, so it still reaches 1 at the edge.
    /// </summary>
    public float Deadzone
    {
        get => deadzone;
        set => deadzone = Math.Clamp(value, 0.0f, 0.95f);
    }

    /// <summary>How far a stick has to be pushed in a direction for that direction to count as held.</summary>
    public float StickThreshold
    {
        get => stickThreshold;
        set => stickThreshold = Math.Clamp(value, 0.05f, 1.0f);
    }

    /// <summary>How far a trigger has to be pulled to count as held.</summary>
    public float TriggerThreshold
    {
        get => triggerThreshold;
        set => triggerThreshold = Math.Clamp(value, 0.05f, 1.0f);
    }

    /// <summary>The actions that have bindings, including the ones bound to nothing.</summary>
    public IReadOnlyCollection<string> Actions => actions.Keys;

    /// <summary>Whether any action is bound to a key at all, which a keyboard's bindings from before there were keys aren't.</summary>
    public bool HasKeys
    {
        get
        {
            foreach (var bound in keyActions.Values)
            {
                if (bound.Count > 0) return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Puts an action on some keys, each of which triggers it by itself, replacing the keys it was on. What it is bound
    /// to on a gamepad stays.
    /// </summary>
    public GamepadBindings BindKeys(string action, params ReadOnlySpan<Key> keys)
    {
        List<Key[]> bound = ResetKeys(action);

        foreach (Key key in keys)
        {
            if (key != Key.Unknown && IndexOf(bound, [key]) < 0)
                bound.Add([key]);
        }

        return Changed();
    }

    /// <summary>Gives an action one more combination of keys, held together (a single key is a combination of one).</summary>
    public GamepadBindings AddKeyCombination(string action, params ReadOnlySpan<Key> keys)
    {
        Validate(action);
        if (keys.Length == 0) return this;

        if (!actions.ContainsKey(action)) actions[action] = [];
        if (!keyActions.TryGetValue(action, out var bound)) keyActions[action] = bound = [];

        Key[] combination = keys.ToArray();
        if (IndexOf(bound, combination) < 0) bound.Add(combination);

        return Changed();
    }

    /// <summary>
    /// Moves an action onto one combination of keys and nothing else on the keyboard, the way a "press the key for jump"
    /// menu does. Any other action on exactly those keys loses them.
    /// </summary>
    public GamepadBindings RebindKeys(string action, params ReadOnlySpan<Key> keys)
    {
        List<Key[]> bound = ResetKeys(action);
        Key[] combination = keys.ToArray();

        foreach (var other in keyActions.Values)
        {
            int at = IndexOf(other, combination);
            if (at >= 0) other.RemoveAt(at);
        }

        if (combination.Length > 0) bound.Add(combination);
        return Changed();
    }

    /// <summary>The combinations of keys an action is bound to, none if it isn't on any.</summary>
    public IReadOnlyList<Key[]> KeyCombinationsOf(string action) =>
        keyActions.TryGetValue(action, out var bound) ? bound : [];

    /// <summary>
    /// Whether an action is held on a keyboard, all the keys of one of its combinations are down.
    /// </summary>
    /// <param name="before">Whether to ask about the update before this one, for what started and what ended.</param>
    public bool AreKeysActive(string action, Keyboard keyboard, bool before = false)
    {
        if (!keyActions.TryGetValue(action, out var bound)) return false;

        foreach (Key[] combination in bound)
        {
            bool all = combination.Length > 0;
            foreach (Key key in combination)
            {
                // Down the update before is down now and not new, or let go of just now
                bool down = before ? (keyboard.IsDown(key) && !keyboard.WasPressed(key)) || keyboard.WasReleased(key) : keyboard.IsDown(key);
                if (!down)
                {
                    all = false;
                    break;
                }
            }

            if (all) return true;
        }

        return false;
    }

    /// <summary>
    /// Puts an action on some inputs, each of which triggers it by itself, replacing what it was bound to.
    /// With no inputs the action is still known, it just can't be triggered.
    /// See <see cref="AddCombination"/> for inputs that have to be held together.
    /// </summary>
    /// <exception cref="ArgumentException">The name of the action isn't an identifier.</exception>
    public GamepadBindings Bind(string action, params ReadOnlySpan<GamepadInput> inputs)
    {
        List<uint> bound = Reset(action);

        foreach (var input in inputs)
        {
            if (!bound.Contains(GamepadInputs.Bit(input)))
                bound.Add(GamepadInputs.Bit(input));
        }

        return Changed();
    }

    /// <summary>Gives an action one more input that triggers it, next to what it is bound to already.</summary>
    public GamepadBindings Add(string action, GamepadInput input) => AddCombination(action, input);

    /// <summary>
    /// Gives an action one more combination, inputs that trigger it when they are all held together
    /// (a single input is a combination of one).
    /// </summary>
    public GamepadBindings AddCombination(string action, params ReadOnlySpan<GamepadInput> inputs) =>
        AddCombination(action, GamepadInputs.Mask(inputs));

    /// <summary>Gives an action one more combination, as the bits of its inputs.</summary>
    public GamepadBindings AddCombination(string action, uint mask)
    {
        Validate(action);

        if (!actions.TryGetValue(action, out var bound))
            actions[action] = bound = [];

        if (mask != 0 && !bound.Contains(mask))
            bound.Add(mask);

        return Changed();
    }

    /// <summary>
    /// Moves an action onto one combination and nothing else, the way a "press the buttons for jump" menu does.
    /// Any other action that was on exactly that combination loses it.
    /// </summary>
    public GamepadBindings Rebind(string action, params ReadOnlySpan<GamepadInput> inputs) =>
        Rebind(action, GamepadInputs.Mask(inputs));

    /// <summary>Moves an action onto one combination and nothing else, as the bits of its inputs.</summary>
    public GamepadBindings Rebind(string action, uint mask)
    {
        List<uint> bound = Reset(action);

        foreach (var other in actions.Values)
            other.Remove(mask);

        if (mask != 0)
            bound.Add(mask);

        return Changed();
    }

    /// <summary>Takes one combination away from an action.</summary>
    public bool Remove(string action, params ReadOnlySpan<GamepadInput> inputs)
    {
        if (!actions.TryGetValue(action, out var bound) || !bound.Remove(GamepadInputs.Mask(inputs)))
            return false;

        Changed();
        return true;
    }

    /// <summary>Forgets an action altogether.</summary>
    public bool Unbind(string action)
    {
        Changed();
        keyActions.Remove(action);
        return actions.Remove(action);
    }

    /// <summary>Forgets every action. The thresholds stay as they are.</summary>
    public void Clear()
    {
        actions.Clear();
        keyActions.Clear();
        Changed();
    }

    /// <summary>
    /// The combinations an action is bound to, each as the bits of its inputs (see <see cref="GamepadInputs.FromMask"/>).
    /// None if it isn't known.
    /// </summary>
    public IReadOnlyList<uint> CombinationsOf(string action) =>
        actions.TryGetValue(action, out var bound) ? bound : [];

    /// <summary>Whether an input by itself triggers an action.</summary>
    public bool IsBound(string action, GamepadInput input) =>
        actions.TryGetValue(action, out var bound) && bound.Contains(GamepadInputs.Bit(input));

    /// <summary>What an action is bound to as text, e.g. "A, DPadUp" or "A + B", for showing or saving.</summary>
    public string Describe(string action) => Describe(CombinationsOf(action), KeyCombinationsOf(action));

    /// <summary>
    /// Whether an action is triggered by a set of held inputs, one of its combinations is held in full
    /// and no bigger combination that contains it is.
    /// </summary>
    /// <param name="held">The bits of everything that is held, see <see cref="Gamepad.HeldMask"/>.</param>
    public bool IsActive(string action, uint held)
    {
        if (held == 0 || !actions.TryGetValue(action, out var bound))
            return false;

        foreach (uint mask in bound)
        {
            if ((held & mask) == mask && !IsTakenOver(mask, held))
                return true;
        }

        return false;
    }

    /// <summary>A copy that can be changed without changing this one, to give to another gamepad.</summary>
    public GamepadBindings Clone()
    {
        var copy = new GamepadBindings();
        copy.CopyFrom(this);
        return copy;
    }

    /// <summary>Makes these bindings the same as others.</summary>
    public void CopyFrom(GamepadBindings other)
    {
        if (ReferenceEquals(other, this))
            return;

        deadzone = other.deadzone;
        stickThreshold = other.stickThreshold;
        triggerThreshold = other.triggerThreshold;

        actions.Clear();
        foreach (var (action, bound) in other.actions)
            actions[action] = [.. bound];

        keyActions.Clear();
        foreach (var (action, bound) in other.keyActions)
            keyActions[action] = [.. bound];

        Changed();
    }

    /// <summary>
    /// These bindings as a HIDL object, to be written with <see cref="HIDLWriter"/> by itself or as part
    /// of something bigger, such as all the settings of a game.
    /// </summary>
    public ObjectValue ToValue()
    {
        Dictionary<string, IRuntimeValue> saved = [];
        foreach (var (action, bound) in actions)
            saved[action] = new StringValue(Describe(bound, KeyCombinationsOf(action)));

        return new ObjectValue(new Dictionary<string, IRuntimeValue>
        {
            [KEY_DEADZONE] = new NumberValue(deadzone),
            [KEY_STICK_THRESHOLD] = new NumberValue(stickThreshold),
            [KEY_TRIGGER_THRESHOLD] = new NumberValue(triggerThreshold),
            [KEY_ACTIONS] = new ObjectValue(saved)
        });
    }

    /// <summary>
    /// Takes over what a HIDL object says, see the summary of the class for what one looks like. What it doesn't
    /// mention stays as it is, an action the file has never heard of (added to the game after it was saved) keeps
    /// what it is bound to here. Inputs that go by a name nobody knows are reported, and the combination they
    /// are part of is skipped.
    /// </summary>
    /// <param name="problems">Gets a line for everything in the object that couldn't be made sense of.</param>
    public void Apply(ObjectValue value, List<string>? problems = null)
    {
        if (value.Properties is null)
            return;

        if (value.Properties.TryGetValue(KEY_DEADZONE, out var dead) && dead is NumberValue deadNumber)
            Deadzone = deadNumber.Value;
        if (value.Properties.TryGetValue(KEY_STICK_THRESHOLD, out var stick) && stick is NumberValue stickNumber)
            StickThreshold = stickNumber.Value;
        if (value.Properties.TryGetValue(KEY_TRIGGER_THRESHOLD, out var trigger) && trigger is NumberValue triggerNumber)
            TriggerThreshold = triggerNumber.Value;

        if (!value.Properties.TryGetValue(KEY_ACTIONS, out var boundValue) || boundValue is not ObjectValue { Properties: { } saved })
            return;

        const StringSplitOptions tidy = StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries;

        foreach (var (action, inputs) in saved)
        {
            if (inputs is not StringValue names)
            {
                problems?.Add($"The inputs of '{action}' aren't a text.");
                continue;
            }

            List<uint> bound = actions[action] = [];
            List<Key[]> boundKeys = keyActions[action] = [];

            // Combinations are separated by commas, the inputs of one by plus signs.
            foreach (string combination in (names.Value ?? string.Empty).Split(',', tidy))
            {
                uint mask = 0;
                List<Key> keys = [];
                bool known = true;

                foreach (string part in combination.Split('+', tidy))
                {
                    if (part.StartsWith(KEY_PREFIX, StringComparison.OrdinalIgnoreCase))
                    {
                        if (Enum.TryParse(part[KEY_PREFIX.Length..], ignoreCase: true, out Key key) && key != Key.Unknown)
                        {
                            keys.Add(key);
                            continue;
                        }

                        problems?.Add($"'{action}' is bound to '{part}', which isn't a key.");
                        known = false;
                        continue;
                    }

                    if (GamepadInputs.TryParse(part, out var input))
                    {
                        mask |= GamepadInputs.Bit(input);
                        continue;
                    }

                    problems?.Add($"'{action}' is bound to '{part}', which isn't an input of a gamepad.");
                    known = false;
                }

                if (!known) continue;

                // A combination is the buttons of a gamepad or the keys of a keyboard, not a bit of both
                if (keys.Count > 0 && mask != 0)
                {
                    problems?.Add($"'{action}' has '{combination}', which mixes keys with the buttons of a gamepad.");
                    continue;
                }

                if (keys.Count > 0 && IndexOf(boundKeys, [.. keys]) < 0)
                    boundKeys.Add([.. keys]);
                else if (mask != 0 && !bound.Contains(mask))
                    bound.Add(mask);
            }
        }

        Changed();
    }

    /// <summary>
    /// Whether a combination that is held doesn't count, because a bigger one that contains it is held as well.
    /// </summary>
    private bool IsTakenOver(uint mask, uint held)
    {
        combinations ??= CollectCombinations();

        foreach (uint bigger in combinations)
        {
            if (bigger != mask && (bigger & mask) == mask && (held & bigger) == bigger)
                return true;
        }

        return false;
    }

    private uint[] CollectCombinations()
    {
        List<uint> found = [];

        foreach (var bound in actions.Values)
        {
            foreach (uint mask in bound)
            {
                if (BitOperations.PopCount(mask) > 1 && !found.Contains(mask))
                    found.Add(mask);
            }
        }

        return [.. found];
    }

    /// <summary>Makes sure an action is known, and bound to nothing.</summary>
    private List<uint> Reset(string action)
    {
        Validate(action);

        if (actions.TryGetValue(action, out var bound))
            bound.Clear();
        else
            actions[action] = bound = [];

        return bound;
    }

    /// <summary>Makes sure an action is known, and on no keys.</summary>
    private List<Key[]> ResetKeys(string action)
    {
        Validate(action);

        if (!actions.ContainsKey(action)) actions[action] = [];

        if (keyActions.TryGetValue(action, out var bound))
            bound.Clear();
        else
            keyActions[action] = bound = [];

        return bound;
    }

    private static int IndexOf(List<Key[]> bound, Key[] combination)
    {
        for (int i = 0; i < bound.Count; i++)
        {
            if (bound[i].AsSpan().SequenceEqual(combination)) return i;
        }

        return -1;
    }

    private GamepadBindings Changed()
    {
        combinations = null;
        return this;
    }

    private static string Describe(IReadOnlyList<uint> bound, IReadOnlyList<Key[]> keys)
    {
        StringBuilder text = new();

        foreach (Key[] combination in keys)
        {
            if (text.Length > 0)
                text.Append(", ");

            for (int i = 0; i < combination.Length; i++)
            {
                if (i > 0)
                    text.Append(" + ");
                text.Append(KEY_PREFIX).Append(combination[i]);
            }
        }

        foreach (uint combination in bound)
        {
            if (text.Length > 0)
                text.Append(", ");

            bool first = true;
            for (uint mask = combination; mask != 0; mask &= mask - 1)
            {
                if (!first)
                    text.Append(" + ");
                text.Append((GamepadInput)BitOperations.TrailingZeroCount(mask));
                first = false;
            }
        }

        return text.ToString();
    }

    private static void Validate(string action)
    {
        // Anything else couldn't be saved, actions are the keys of an object in the file.
        if (!HIDLWriter.IsIdentifier(action))
            throw new ArgumentException($"'{action}' can't be the name of an action, it has to be an identifier.", nameof(action));
    }
}
