using System.Globalization;

using Silk.NET.Input;

namespace Horizon.Input;

/// <summary>
/// A list of button presses to be played on a gamepad that isn't there, so a game can be walked through with nobody holding one.
/// For checking a menu or a level from a script, for a demo that plays itself, and for showing somebody how to reproduce a bug.
/// <code>
/// var script = new InputScript()
///     .Tap(2.0f, GamepadInput.A)
///     .Hold(3.0f, GamepadInput.DPadRight, 1.5f)
///     .Quit(8.0f);
///
/// Engine.Input.Play(script);
/// </code>
/// One can be written as a file as well, a step a line. Times are in seconds since the script was started, and anything after a hash is a comment.
/// <code>
/// 2.0 tap A
/// 3.0 hold DPadRight 1.5
/// 8.0 quit
/// </code>
/// Start any game of the engine with <see cref="ENVIRONMENT_VARIABLE"/> set to the path of such a file and it is played from the first update on.
/// The gamepad it plays on takes the first slot, so it is player one wherever that matters. A step can be a key of the
/// keyboard as well (<c>2.0 tap key:J</c>), which is played on the real keyboard, and a script of nothing but keys makes
/// no gamepad at all, so whatever reads the keyboard is driven the way a person at it would drive it. Keys held over
/// each other are held together (<c>2.0 hold key:ControlLeft 0.5</c> then <c>2.1 tap key:G</c> is Ctrl+G), and a chord
/// says a shortcut in one line the way a menu writes it, <c>2.0 chord Ctrl+Shift+G</c>, the modifiers held round a tap
/// of the last key. A key is Silk's name for it (ControlLeft, ShiftLeft), LeftControl and Ctrl are read as well.
/// </summary>
public sealed class InputScript
{
    public const string ENVIRONMENT_VARIABLE = "HORIZON_INPUT_SCRIPT";

    // How long (in seconds) a tap holds its button, long enough for every update rate to see it
    private const float TAP_TIME = 0.08f;

    private const string STEP_TAP = "tap";
    private const string STEP_HOLD = "hold";
    private const string STEP_QUIT = "quit";
    private const string STEP_CHORD = "chord";

    // How long a chord's modifiers are down before its key and after it lets go, so every update rate sees them held
    private const float CHORD_LEAD = 0.05f;

    private readonly record struct Step(float From, float Until, GamepadInput Input);
    private readonly record struct KeyStep(float From, float Until, Key Key);

    private const string KEY_PREFIX = "key:";

    private readonly List<Step> _steps = [];
    private readonly List<KeyStep> _keySteps = [];

    /// <summary>Whether any step is a button of a gamepad, a script of keys alone needs no gamepad of its own.</summary>
    public bool HasButtons => _steps.Count > 0;
    private float _quitAt = float.PositiveInfinity;

    /// <summary>
    /// When the last step of the script is over, in seconds.
    /// </summary>
    public float Length { get; private set; }

    /// <summary>
    /// Presses something and lets go of it right away.
    /// </summary>
    public InputScript Tap(float at, GamepadInput input) => Hold(at, input, TAP_TIME);

    /// <summary>
    /// Holds something down for a while.
    /// </summary>
    public InputScript Hold(float at, GamepadInput input, float seconds)
    {
        _steps.Add(new Step(at, at + MathF.Max(0.0f, seconds), input));
        Length = MathF.Max(Length, at + seconds);
        return this;
    }

    /// <summary>Presses a key of the keyboard and lets go of it right away.</summary>
    public InputScript TapKey(float at, Key key) => HoldKey(at, key, TAP_TIME);

    /// <summary>Holds a key of the keyboard down for a while.</summary>
    public InputScript HoldKey(float at, Key key, float seconds)
    {
        _keySteps.Add(new KeyStep(at, at + MathF.Max(0.0f, seconds), key));
        Length = MathF.Max(Length, at + seconds);
        return this;
    }

    /// <summary>
    /// Presses a shortcut, the modifiers held down, the last key tapped while they are, and the modifiers let go of after
    /// it (Ctrl+Shift+G is <c>Chord(2.0f, Key.ControlLeft, Key.ShiftLeft, Key.G)</c>).
    /// </summary>
    public InputScript Chord(float at, params Key[] keys)
    {
        if (keys.Length == 0)
            return this;

        float tapped = at + CHORD_LEAD;
        for (int i = 0; i < keys.Length - 1; i++)
            HoldKey(at, keys[i], CHORD_LEAD * 2.0f + TAP_TIME);
        return HoldKey(tapped, keys[^1], TAP_TIME);
    }

    /// <summary>Every key the script holds at a moment.</summary>
    internal void KeysAt(float time, HashSet<Key> into)
    {
        into.Clear();
        foreach (KeyStep step in _keySteps)
        {
            if (time >= step.From && time < step.Until) into.Add(step.Key);
        }
    }

    /// <summary>
    /// Closes the game at a moment, which is how a script that checks something ends.
    /// </summary>
    public InputScript Quit(float at)
    {
        _quitAt = at;
        Length = MathF.Max(Length, at);
        return this;
    }

    /// <summary>
    /// Whether the script wants the game closed by a moment.
    /// </summary>
    internal bool WantsQuit(float time) => time >= _quitAt;

    /// <summary>
    /// What the gamepad of the script holds at a moment.
    /// </summary>
    internal GamepadSnapshot At(float time)
    {
        var snapshot = new GamepadSnapshot();

        foreach (Step step in _steps)
        {
            if (time < step.From || time >= step.Until) continue;

            // The triggers and the sticks aren't buttons to a gamepad, it reads them off their axes and masks their bits
            // off the buttons, so a script that says "tap RightTrigger" pulls the trigger all the way instead
            switch (step.Input)
            {
                case GamepadInput.LeftTrigger: snapshot.LeftTrigger = 1.0f; break;
                case GamepadInput.RightTrigger: snapshot.RightTrigger = 1.0f; break;
                case GamepadInput.LeftStickUp: snapshot.LeftStick.Y = 1.0f; break;
                case GamepadInput.LeftStickDown: snapshot.LeftStick.Y = -1.0f; break;
                case GamepadInput.LeftStickRight: snapshot.LeftStick.X = 1.0f; break;
                case GamepadInput.LeftStickLeft: snapshot.LeftStick.X = -1.0f; break;
                case GamepadInput.RightStickUp: snapshot.RightStick.Y = 1.0f; break;
                case GamepadInput.RightStickDown: snapshot.RightStick.Y = -1.0f; break;
                case GamepadInput.RightStickRight: snapshot.RightStick.X = 1.0f; break;
                case GamepadInput.RightStickLeft: snapshot.RightStick.X = -1.0f; break;
                default: snapshot.Buttons |= GamepadInputs.Bit(step.Input); break;
            }
        }

        return snapshot;
    }

    /// <summary>
    /// Reads a script out of a file. Lines that make no sense are skipped and said so in <paramref name="problems"/>.
    /// </summary>
    public static InputScript Load(string path, out List<string> problems)
    {
        var script = new InputScript();
        problems = [];

        int number = 0;
        foreach (string raw in File.ReadLines(path))
        {
            number++;

            int comment = raw.IndexOf('#');
            string[] words = (comment >= 0 ? raw[..comment] : raw).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (words.Length == 0) continue;

            if (!script.TryRead(words))
                problems.Add($"line {number} is not a step: '{raw.Trim()}'");
        }

        return script;
    }

    /// <summary>
    /// Helper method to turn the words of a line into a step, false if they aren't one.
    /// </summary>
    private bool TryRead(string[] words)
    {
        if (words.Length < 2 || !TryNumber(words[0], out float at)) return false;

        switch (words[1].ToLowerInvariant())
        {
            case STEP_QUIT:
                Quit(at);
                return true;

            case STEP_CHORD when words.Length >= 3 && TryChord(words[2], out Key[] chord):
                Chord(at, chord);
                return true;

            case STEP_TAP when words.Length >= 3 && TryKey(words[2], out Key tappedKey):
                TapKey(at, tappedKey);
                return true;

            case STEP_HOLD when words.Length >= 4 && TryKey(words[2], out Key heldKey) && TryNumber(words[3], out float keySeconds):
                HoldKey(at, heldKey, keySeconds);
                return true;

            case STEP_TAP when words.Length >= 3 && GamepadInputs.TryParse(words[2], out GamepadInput tapped):
                Tap(at, tapped);
                return true;

            case STEP_HOLD when words.Length >= 4 && GamepadInputs.TryParse(words[2], out GamepadInput held) && TryNumber(words[3], out float seconds):
                Hold(at, held, seconds);
                return true;

            default:
                return false;
        }
    }

    private static bool TryKey(string word, out Key key)
    {
        key = Key.Unknown;
        return word.StartsWith(KEY_PREFIX, StringComparison.OrdinalIgnoreCase) && TryKeyName(word[KEY_PREFIX.Length..], out key);
    }

    /// <summary>
    /// Helper method to read a key by its name, Silk's (ControlLeft, G, Number1) or the way people write it, a side in
    /// front (LeftControl), Ctrl for Control, a digit for its key. LeftControl was a line the script skipped, and a tester
    /// holding it with shift for Ctrl+Shift+G got a G.
    /// </summary>
    private static bool TryKeyName(string name, out Key key)
    {
        key = Key.Unknown;
        if (name.Length == 0)
            return false;

        if (name.Length == 1 && char.IsAsciiDigit(name[0]))
            name = "Number" + name;

        string named = name.Replace("ctrl", "Control", StringComparison.OrdinalIgnoreCase);
        foreach (string side in new[] { "Left", "Right" })
        {
            if (named.Length > side.Length && named.StartsWith(side, StringComparison.OrdinalIgnoreCase))
                named = named[side.Length..] + side;
        }

        // A modifier with no side is the left one, Ctrl is ControlLeft
        if (named.ToLowerInvariant() is "control" or "shift" or "alt" or "super")
            named += "Left";

        // Silk's name first, a key whose name starts with a side of its own (LeftBracket) is that
        return (Enum.TryParse(name, ignoreCase: true, out key) || Enum.TryParse(named, ignoreCase: true, out key))
            && key != Key.Unknown && !int.TryParse(name, out _);
    }

    /// <summary>
    /// Helper method to read a shortcut written the way a menu writes it (Ctrl+Shift+G) into its keys, the modifiers the
    /// left ones and the last key whatever it names. False when a part of it isn't a key.
    /// </summary>
    private static bool TryChord(string written, out Key[] keys)
    {
        string[] parts = written.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        keys = new Key[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i].StartsWith(KEY_PREFIX, StringComparison.OrdinalIgnoreCase) ? parts[i][KEY_PREFIX.Length..] : parts[i];
            Key? modifier = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => Key.ControlLeft,
                "shift" => Key.ShiftLeft,
                "alt" => Key.AltLeft,
                "cmd" or "super" or "win" or "meta" => Key.SuperLeft,
                _ => null
            };

            if (modifier is { } held && i < parts.Length - 1)
                keys[i] = held;
            else if (!TryKeyName(part, out keys[i]))
                return false;
        }

        return keys.Length > 0;
    }

    private static bool TryNumber(string text, out float number) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
}
