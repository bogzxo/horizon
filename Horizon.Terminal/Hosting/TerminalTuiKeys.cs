using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;

namespace Horizon.UI.Components;

/// <summary>
/// The bytes a terminal sends for keys read back into keys, for a program with no shell between it and the view
/// (<see cref="TerminalTui"/>). The arrows and the rest of the legacy sequences with their modifiers (ESC [ 1 ; 5 A is
/// control and up), control and a letter, alt as escape first, a paste as the text it was. A key is
/// <c>{ name, text, ctrl, alt, shift }</c>, the name what it is (up, enter, a, f5) and the text what it types ("" for
/// none).
/// </summary>
public static class TerminalTuiKeys
{
    public readonly record struct Key(string Name, string Text, bool Ctrl, bool Alt, bool Shift);

    /// <summary>The keys in what was sent, as the objects a program gets.</summary>
    public static IEnumerable<ObjectValue> Read(string bytes) => Parse(bytes).Select(ToObject);

    public static ObjectValue ToObject(Key key) => Natives.Object(
        ("name", new StringValue(key.Name)),
        ("text", new StringValue(key.Text)),
        ("ctrl", Values.Bool(key.Ctrl)),
        ("alt", Values.Bool(key.Alt)),
        ("shift", Values.Bool(key.Shift)));

    public static List<Key> Parse(string bytes)
    {
        var keys = new List<Key>();
        int i = 0;
        while (i < bytes.Length)
        {
            char c = bytes[i];

            // a paste, all of it the text of one key
            if (bytes.AsSpan(i).StartsWith("\u001b[200~"))
            {
                int end = bytes.IndexOf("\u001b[201~", i + 6, StringComparison.Ordinal);
                string pasted = end < 0 ? bytes[(i + 6)..] : bytes[(i + 6)..end];
                keys.Add(new Key("paste", pasted, false, false, false));
                i = end < 0 ? bytes.Length : end + 6;
                continue;
            }

            if (c == '\u001b')
            {
                // escape on its own
                if (i + 1 >= bytes.Length)
                {
                    keys.Add(new Key("escape", "", false, false, false));
                    i++;
                    continue;
                }

                char next = bytes[i + 1];
                if (next is '[' or 'O')
                {
                    int at = i + 2;
                    int start = at;
                    while (at < bytes.Length && (char.IsAsciiDigit(bytes[at]) || bytes[at] == ';'))
                        at++;
                    if (at >= bytes.Length)
                    {
                        i = bytes.Length;
                        break;
                    }

                    string parameters = bytes[start..at];
                    char final = bytes[at];
                    i = at + 1;
                    keys.Add(Sequence(parameters, final));
                    continue;
                }

                // alt and a key
                var inner = Parse(bytes[(i + 1)..(i + 2)]);
                if (inner.Count == 1)
                    keys.Add(inner[0] with { Alt = true });
                i += 2;
                continue;
            }

            i++;
            keys.Add(c switch
            {
                '\r' or '\n' => new Key("enter", "", false, false, false),
                '\t' => new Key("tab", "", false, false, false),
                '\u007f' or '\b' => new Key("backspace", "", false, false, false),
                '\0' => new Key("space", "", true, false, false),
                < ' ' => new Key(((char)('a' + c - 1)).ToString(), "", true, false, false),
                ' ' => new Key("space", " ", false, false, false),
                _ => new Key(char.ToLowerInvariant(c).ToString(), c.ToString(), false, false, char.IsUpper(c))
            });
        }

        return keys;
    }

    // ESC [ ... final, the arrows, home and end, the function keys, with modifiers after a ;
    private static Key Sequence(string parameters, char final)
    {
        string[] parts = parameters.Split(';');
        int modifier = parts.Length > 1 && int.TryParse(parts[1], out int m) ? m - 1 : 0;
        bool shift = (modifier & 1) != 0, alt = (modifier & 2) != 0, ctrl = (modifier & 4) != 0;
        int number = parts.Length > 0 && int.TryParse(parts[0], out int n) ? n : 0;

        string name = final switch
        {
            'A' => "up",
            'B' => "down",
            'C' => "right",
            'D' => "left",
            'H' => "home",
            'F' => "end",
            'P' => "f1",
            'Q' => "f2",
            'R' => "f3",
            'S' => "f4",
            'Z' => "tab",
            '~' => number switch
            {
                1 or 7 => "home",
                2 => "insert",
                3 => "delete",
                4 or 8 => "end",
                5 => "pgup",
                6 => "pgdn",
                15 => "f5",
                17 => "f6",
                18 => "f7",
                19 => "f8",
                20 => "f9",
                21 => "f10",
                23 => "f11",
                24 => "f12",
                _ => "unknown"
            },
            _ => "unknown"
        };

        // shift and tab comes as its own sequence
        if (final == 'Z')
            shift = true;
        return new Key(name, "", ctrl, alt, shift);
    }
}
