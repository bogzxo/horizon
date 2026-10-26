using Silk.NET.Input;

using Horizon.Input;

namespace Horizon.UI;

/// <summary>
/// The keys of a program that are its own, not a text box's. Each shortcut is a key (with or without control) and what
/// pressing it does, and it goes off once for every press however long the key is held. <see cref="UICommands"/> keeps
/// one for the keys of its commands.
/// <para>
/// A shortcut can say whether it took a press, and a press goes to the first of those on its key that takes it and no
/// further, a command that isn't there to be picked passing it on to the next. Two commands on one key both went off,
/// Ctrl+Shift+G showed Source Control and said there was no group to take apart in the same press. A shortcut that just
/// does something goes off on every press of its key whatever else does, as they always have.
/// </para>
/// <para>
/// A shortcut can be two keys one after the other, VS Code's chords ("Ctrl+K Z"). Pressing the first of any chord does
/// nothing yet and waits for the next key (<see cref="OnChordPending"/>), which picks the chord on it, or says there is
/// none (<see cref="OnChordMissed"/>). Escape, or <see cref="ChordWait"/> going by, stops the waiting.
/// </para>
/// </summary>
public sealed class UIShortcuts
{
    private sealed class Shortcut(Key key, bool control, bool? shift, bool alt, bool whileTyping, Func<bool> pressed, bool takes, bool first, UIKeyStroke? then)
    {
        public Key Key { get; } = key;
        public bool Control { get; } = control;
        public bool Alt { get; } = alt;

        // Null for "don't care", which is what the arrows want (shift makes them go further, they still go)
        public bool? Shift { get; set; } = shift;
        public bool WhileTyping { get; } = whileTyping;
        public Func<bool> Pressed { get; } = pressed;

        // Whether it says if it took the press, and whether it's asked before the ones that aren't first
        public bool Takes { get; } = takes;
        public bool First { get; } = first;

        // The second key of a chord, null for a shortcut that's one key
        public UIKeyStroke? Then { get; } = then;
    }

    private readonly List<Shortcut> shortcuts = [];

    // The keys a shortcut took this update, so the next one on the same key leaves it
    private readonly HashSet<Key> taken = [];

    // The first key of a chord that was pressed, while the second is waited for, and when it was pressed
    private UIKeyStroke? pending;
    private double pendingSince;

    // Every key that is pressed rather than held with others, for the key after the first of a chord
    private static readonly Key[] Pressable = [.. Enum.GetValues<Key>().Distinct().Where(key => !UIKeys.IsHeld(key))];

    // The keyboard's WasPressed kept as one delegate, making a new one every update was garbage every tick
    private Keyboard? heard;
    private Func<Key, bool>? wasPressed;

    /// <summary>How long the second key of a chord is waited for, in seconds.</summary>
    public float ChordWait { get; set; } = 2.0f;

    /// <summary>The time in seconds that the wait for a chord's second key goes by. A test sets its own.</summary>
    public Func<double> Clock { get; set; } = () => Environment.TickCount64 / 1000.0;

    /// <summary>The first key of a chord was pressed and the second is waited for, the first as the menus write it.</summary>
    public Action<string>? OnChordPending { get; set; }

    /// <summary>The second key pressed after a chord's first was no chord's, both of them as the menus write them.</summary>
    public Action<string>? OnChordMissed { get; set; }

    /// <summary>The first key of a chord while the second is waited for, null while nothing is.</summary>
    public string? Pending => pending?.ToString();

    /// <summary>Whether the last update's press was a key of a chord, taken by it and nothing else's.</summary>
    public bool Swallowed { get; private set; }

    /// <summary>
    /// Adds a key that does something when it is pressed, every time it is, whatever else is on the key.
    /// </summary>
    /// <param name="control">Whether control has to be held as well. A key that doesn't want it doesn't go off while it is held.</param>
    /// <param name="whileTyping">Whether it also goes off while something is being typed into. Delete and the arrows mean something else in a text box.</param>
    /// <param name="alt">Whether alt has to be held as well, Ctrl+Alt+B being another key than Ctrl+B.</param>
    /// <param name="shift">
    /// Whether shift has to be held, or not held. Null for either, unless the same key with shift is something else,
    /// when it's without (Ctrl+S saves, Ctrl+Shift+S saves everything, and not both).
    /// </param>
    /// <param name="then">The second key of a chord, the key and the rest being the first, null for a shortcut that's one key.</param>
    public void Add(Key key, Action pressed, bool control = false, bool whileTyping = false, bool? shift = null, bool alt = false, UIKeyStroke? then = null) =>
        Add(new Shortcut(key, control, then is null ? shift : shift ?? false, alt, whileTyping, () =>
        {
            pressed();
            return true;
        }, takes: false, first: false, then));

    /// <summary>
    /// Adds a key whose handler says whether it took the press, false for one that had nothing to do (a command that
    /// isn't there to be picked right now). A press goes to the first that takes it, in the order they were added with
    /// the ones that are <paramref name="first"/> before the rest, and the others on its key don't hear of it.
    /// </summary>
    /// <param name="first">
    /// Whether it's asked before the ones on its key that aren't, for a key of something only there some of the time
    /// (the designer's Ungroup over Source Control's Ctrl+Shift+G while a group is selected).
    /// </param>
    /// <param name="then">The second key of a chord, see the other Add.</param>
    public void Add(Key key, Func<bool> pressed, bool control = false, bool whileTyping = false, bool? shift = null, bool alt = false, bool first = false, UIKeyStroke? then = null) =>
        Add(new Shortcut(key, control, then is null ? shift : shift ?? false, alt, whileTyping, pressed, takes: true, first, then));

    private void Add(Shortcut added)
    {
        foreach (Shortcut other in shortcuts)
        {
            // A chord's keys are the keys pressed exactly, shift or not
            if (other.Key != added.Key || other.Control != added.Control || other.Alt != added.Alt || other.Then is not null || added.Then is not null)
                continue;

            if (added.Shift is true && other.Shift is null)
                other.Shift = false;
            else if (added.Shift is null && other.Shift is true)
                added.Shift = false;
        }

        // A first one after the first ones added before it, ahead of all the rest
        int at = added.First ? shortcuts.FindIndex(shortcut => !shortcut.First) : -1;
        if (at >= 0)
            shortcuts.Insert(at, added);
        else
            shortcuts.Add(added);
    }

    /// <summary>
    /// Takes a key away, every shortcut on it (one that doesn't care about shift among them), for a key that was rebound
    /// in a file of keys. Null if nothing was on it, otherwise whether what was on it went off while typing. The chords
    /// that start with the key stay, see the other Remove.
    /// </summary>
    public bool? Remove(Key key, bool control, bool shift, bool alt) => Remove(new UIKeyStroke(key, control, shift, alt), null);

    /// <summary>Takes away what's on a chord, or on one key when there's no second, the way the other Remove does.</summary>
    public bool? Remove(UIKeyStroke first, UIKeyStroke? then)
    {
        bool? whileTyping = null;
        for (int i = shortcuts.Count - 1; i >= 0; i--)
        {
            if (On(shortcuts[i], first, then))
            {
                whileTyping = shortcuts[i].WhileTyping || (whileTyping ?? false);
                shortcuts.RemoveAt(i);
            }
        }

        return whileTyping;
    }

    /// <summary>Whether anything is on a key, held exactly so. Not a chord that starts with it.</summary>
    public bool Has(Key key, bool control, bool shift, bool alt) => Has(new UIKeyStroke(key, control, shift, alt), null);

    /// <summary>Whether anything is on a chord, or on one key when there's no second.</summary>
    public bool Has(UIKeyStroke first, UIKeyStroke? then) => shortcuts.Exists(shortcut => On(shortcut, first, then));

    private static bool On(Shortcut shortcut, UIKeyStroke first, UIKeyStroke? then) =>
        Is(shortcut, first.Key, first.Control, first.Shift, first.Alt) && Nullable.Equals(shortcut.Then, then);

    // Whether a shortcut's first key is a key held so, one that doesn't care about shift either way
    private static bool Is(Shortcut shortcut, Key key, bool control, bool shift, bool alt) =>
        shortcut.Key == key && shortcut.Control == control && shortcut.Alt == alt && (shortcut.Shift is not { } wanted || wanted == shift);

    /// <summary>
    /// Called every update to set off whatever was pressed since the last one.
    /// </summary>
    /// <param name="typing">Whether the keyboard is busy with a text box right now.</param>
    public void Update(Keyboard keyboard, bool typing)
    {
        if (heard != keyboard || wasPressed is null)
        {
            heard = keyboard;
            wasPressed = keyboard.WasPressed;
        }

        Update(wasPressed, keyboard.Primary, keyboard.IsDown(Key.ShiftLeft) || keyboard.IsDown(Key.ShiftRight),
            keyboard.IsDown(Key.AltLeft) || keyboard.IsDown(Key.AltRight), typing);

        // A key of a chord types nothing, the z of Ctrl+K Z went into the code as Zen Mode came on
        if (Swallowed)
            UIKeyboard.Clear();
    }

    /// <summary>
    /// What the other Update does, with what was pressed and what's held said rather than read off a keyboard, for a test
    /// with no window. Ctrl in a shortcut is command on a Mac, see Keyboard.Primary, and that's what control is.
    /// </summary>
    public void Update(Func<Key, bool> wasPressed, bool control, bool shift, bool alt, bool typing)
    {
        taken.Clear();
        Swallowed = false;

        if (pending is not null && Clock() - pendingSince > ChordWait)
            pending = null;

        if (pending is { } first)
        {
            SecondOf(first, wasPressed, control, shift, alt, typing);
            return;
        }

        // The first key of a chord waits for the second and does nothing else, whatever else is on it
        foreach (Shortcut shortcut in shortcuts)
        {
            if (shortcut.Then is null || !shortcut.WhileTyping && typing || !wasPressed(shortcut.Key) || !Is(shortcut, shortcut.Key, control, shift, alt))
                continue;

            var stroke = new UIKeyStroke(shortcut.Key, control, shift, alt);
            pending = stroke;
            pendingSince = Clock();
            Swallowed = true;
            OnChordPending?.Invoke(stroke.ToString());
            return;
        }

        // A shortcut goes off as its key goes down with the modifiers it wants held then, and not as the modifiers change
        // under a key that's held. It went by what was held, and letting go of shift before F turned Ctrl+Shift+F into a
        // Ctrl+F a moment later, Find in Files and the find bar both
        foreach (Shortcut shortcut in shortcuts)
        {
            if (shortcut.Then is not null)
                continue;

            bool pressed = wasPressed(shortcut.Key) && control == shortcut.Control && alt == shortcut.Alt
                && (shortcut.Shift is not { } wanted || wanted == shift);

            if (!pressed || !shortcut.WhileTyping && typing)
                continue;

            if (!shortcut.Takes)
                shortcut.Pressed();
            else if (!taken.Contains(shortcut.Key) && shortcut.Pressed())
                taken.Add(shortcut.Key);
        }
    }

    // The key pressed after the first of a chord, the chord on it set off, or nothing and said so. Escape stops waiting
    private void SecondOf(UIKeyStroke first, Func<Key, bool> wasPressed, bool control, bool shift, bool alt, bool typing)
    {
        Key? pressed = null;
        foreach (Key key in Pressable)
        {
            if (wasPressed(key))
            {
                pressed = key;
                break;
            }
        }

        if (pressed is not { } next)
            return;

        pending = null;
        Swallowed = true;
        if (next == Key.Escape && !control && !shift && !alt)
            return;

        var second = new UIKeyStroke(next, control, shift, alt);
        bool fired = false, took = false;
        foreach (Shortcut shortcut in shortcuts)
        {
            if (shortcut.Then != second || !Is(shortcut, first.Key, first.Control, first.Shift, first.Alt) || !shortcut.WhileTyping && typing)
                continue;

            if (!shortcut.Takes)
            {
                shortcut.Pressed();
                fired = true;
            }
            else if (!took && shortcut.Pressed())
            {
                took = true;
                fired = true;
            }
        }

        if (!fired)
            OnChordMissed?.Invoke(UIKeys.WriteChord(first, second));
    }
}
