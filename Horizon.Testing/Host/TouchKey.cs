using System.Numerics;

using Horizon.Input;
using Horizon.UI.Components;

namespace Horizon.Testing;

/// <summary>
/// A key cap on the card of controls that is also the key, for a phone with no keyboard. A finger on it holds the key
/// down and lifting lets it go, so a tap is a press and a held W walks, the same as the real thing would. The keys go
/// in through the engine's keyboard as if somebody had pressed them, so nothing in an example knows the difference.
/// </summary>
internal sealed class TouchKey : StackPanel
{
    private readonly IReadOnlyList<Key> keys;
    private readonly Vector4 color;
    private bool down;

    public TouchKey(IReadOnlyList<Key> keys, Vector4 color)
    {
        this.keys = keys;
        this.color = color;
        Color = color;
    }

    protected override bool HitTestVisible => true;

    protected internal override void OnPointerDown(Vector2 point) => Hold(true);

    protected internal override void OnPointerUp(Vector2 point) => Hold(false);

    protected internal override void OnPointerCancel() => Hold(false);

    // Lit while held, so a finger can see it landed
    private void Hold(bool now)
    {
        if (down == now) return;
        down = now;
        Color = now ? HostStyle.Accent with { W = 0.6f } : color;
        foreach (Key key in keys) global::Horizon.Engine.GameEngine.Instance.Input.Keyboard.Hear(key, now);
    }

    /// <summary>
    /// What a control's text comes to as caps, each with the key it presses or null for one that's only words (a
    /// click, the wheel, a stick). "Q / E" and "Space, left click" are split where they say, "W A S D" into a cap a
    /// letter, and "Arrows" into the four.
    /// </summary>
    public static List<(string Text, Key? Key)> Split(string input)
    {
        var caps = new List<(string, Key?)>();
        foreach (string raw in input.Split([" / ", ", ", "  "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Equals("arrows", StringComparison.OrdinalIgnoreCase))
            {
                caps.Add(("Left", Key.Left));
                caps.Add(("Up", Key.Up));
                caps.Add(("Down", Key.Down));
                caps.Add(("Right", Key.Right));
                continue;
            }

            string[] words = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 1 && Array.TrueForAll(words, word => Of(word) is not null))
            {
                foreach (string word in words) caps.Add((word, Of(word)));
                continue;
            }

            caps.Add((raw, Of(raw)));
        }
        return caps;
    }

    /// <summary>The key a name on a cap stands for ("G", "Space", "F3", "PgUp", "Esc"), null for none.</summary>
    public static Key? Of(string name)
    {
        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z') return (Key)((int)Key.A + c - 'A');
            if (c is >= '0' and <= '9') return (Key)((int)Key.Number0 + c - '0');
            return c switch { '-' => Key.Minus, '=' => Key.Equal, '+' => Key.KeypadAdd, _ => null };
        }

        if (name.Length is 2 or 3 && (name[0] is 'F' or 'f') && int.TryParse(name[1..], out int f) && f is >= 1 and <= 12)
            return (Key)((int)Key.F1 + f - 1);

        return name.ToLowerInvariant() switch
        {
            "space" => Key.Space,
            "enter" or "return" => Key.Enter,
            "tab" => Key.Tab,
            "esc" or "escape" => Key.Escape,
            "shift" => Key.ShiftLeft,
            "ctrl" or "control" => Key.ControlLeft,
            "alt" => Key.AltLeft,
            "backspace" => Key.Backspace,
            "delete" or "del" => Key.Delete,
            "up" => Key.Up,
            "down" => Key.Down,
            "left" => Key.Left,
            "right" => Key.Right,
            "pgup" or "pageup" => Key.PageUp,
            "pgdn" or "pagedown" => Key.PageDown,
            "home" => Key.Home,
            "end" => Key.End,
            _ => null
        };
    }
}
