
using Silk.NET.Input;

using Horizon.Input;

namespace Horizon.Hex;

/// <summary>
/// The keys of the editor. Each shortcut is a key (with or without control) and what pressing it does,
/// and it goes off once for every press however long the key is held.
/// </summary>
internal sealed class HexShortcuts
{
    private sealed class Shortcut(Key key, bool control, bool? shift, bool whileTyping, Action pressed)
    {
        public Key Key { get; } = key;
        public bool Control { get; } = control;

        // Null for "don't care", which is what the arrows want (shift makes them go further, they still go)
        public bool? Shift { get; } = shift;
        public bool WhileTyping { get; } = whileTyping;
        public Action Pressed { get; } = pressed;
        public bool WasDown { get; set; }
    }

    private readonly List<Shortcut> shortcuts = [];

    /// <summary>
    /// Adds a key that does something when it is pressed.
    /// </summary>
    /// <param name="control">Whether control has to be held as well. A key that doesn't want it doesn't go off while it is held.</param>
    /// <param name="whileTyping">Whether it also goes off while something is being typed into. Delete and the arrows mean something else in a text box.</param>
    public void Add(Key key, Action pressed, bool control = false, bool whileTyping = false, bool? shift = null)
    {
        shortcuts.Add(new Shortcut(key, control, shift, whileTyping, pressed));
    }

    /// <summary>
    /// Called every update to set off whatever was pressed since the last one.
    /// </summary>
    /// <param name="typing">Whether the keyboard is busy with a text box right now.</param>
    public void Update(Keyboard keyboard, bool typing)
    {
        bool control = keyboard.IsDown(Key.ControlLeft) || keyboard.IsDown(Key.ControlRight);
        bool shift = keyboard.IsDown(Key.ShiftLeft) || keyboard.IsDown(Key.ShiftRight);

        foreach (Shortcut shortcut in shortcuts)
        {
            bool down = keyboard.IsDown(shortcut.Key) && control == shortcut.Control && (shortcut.Shift is not { } wanted || wanted == shift);
            bool pressed = down && !shortcut.WasDown;
            shortcut.WasDown = down;

            if (pressed && (shortcut.WhileTyping || !typing))
                shortcut.Pressed();
        }
    }
}
