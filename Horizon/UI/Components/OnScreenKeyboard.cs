using System.Numerics;

using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// One key of an <see cref="OnScreenKeyboard"/>.
/// </summary>
/// <param name="Label">What the key says, and what it goes by in <see cref="OnScreenKeyboard.OnKey"/> and <see cref="OnScreenKeyboard.Select(string)"/>.</param>
/// <param name="Types">The character it types, or one of the named keys (<see cref="OnScreenKeyboard.BACKSPACE"/> and the like). Empty for a key that only reports itself.</param>
/// <param name="Sprite">The art the skin draws it with when it has it (a key of the keyboard of the Dead Revolver pack), empty for a plain key with its label on it.</param>
/// <param name="Width">How wide it is compared to an ordinary key, for the keys with art of their own this is the art's business.</param>
public readonly record struct KeyDefinition(string Label, string Types, string Sprite = "", float Width = 1.0f)
{
    /// <summary>A key that types its own label, with the art of that key if the skin has it.</summary>
    public static KeyDefinition Of(string label) => new(label, Types: label.Length == 1 || OnScreenKeyboard.IsNamed(label) ? label : string.Empty, Sprite: OnScreenKeyboard.SpriteFor(label), Width: OnScreenKeyboard.WidthFor(label));
}

/// <summary>
/// A keyboard on screen that types into a <see cref="TextBox"/>, for when there is no real one (a gamepad, a kiosk).
/// Its keys can be clicked, or walked with a gamepad. The game calls <see cref="Navigate"/> when the d-pad is
/// pressed and <see cref="Press"/> for its confirm button, and a <see cref="UINavigator"/> that lands on the keyboard
/// does the same by itself. The highlighted key is the one that gets pressed.
/// <see cref="Full"/> is laid out like the real thing, with the keys drawn as the keys of the skin's keyboard when
/// it has one (the Dead Revolver pack does, pressed animation and all), otherwise as plain buttons with their labels.
/// A key types its character, or does what a named key does (<see cref="BACKSPACE"/>, <see cref="SPACE"/>,
/// <see cref="SHIFT"/>...), and anything else (e.g. "CONNECT") types nothing and is only reported through
/// <see cref="OnKey"/>, so a layout can carry its own actions.
/// </summary>
public class OnScreenKeyboard : StackPanel
{
    /// <summary>The key that removes the last character.</summary>
    public const string BACKSPACE = "DEL";

    /// <summary>The key that types a space.</summary>
    public const string SPACE = "SPACE";

    /// <summary>The key that is only reported, for whoever listens to confirm what was typed.</summary>
    public const string ENTER = "ENTER";

    /// <summary>The key that makes the next letter a capital, or the lot while it is held down on a real keyboard. Here it stays on until it is pressed again.</summary>
    public const string SHIFT = "SHIFT";

    /// <summary>The key that keeps the letters capitals until it is pressed again.</summary>
    public const string CAPS = "CAPS";

    /// <summary>The key that types a tab, which a text box takes as a space.</summary>
    public const string TAB = "TAB";

    private const float KEY_SPACING = 6.0f;
    private const float KEY_LABEL_SCALE = 0.3f;
    private const string KEY_STYLE = "key";

    /// <summary>Digits and a dot, enough for numbers and addresses.</summary>
    public static string[][] Numeric =>
    [
        ["1", "2", "3"],
        ["4", "5", "6"],
        ["7", "8", "9"],
        [".", "0", BACKSPACE]
    ];

    /// <summary>Digits and letters.</summary>
    public static string[][] Alphanumeric =>
    [
        ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0"],
        ["Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P"],
        ["A", "S", "D", "F", "G", "H", "J", "K", "L", "-"],
        ["Z", "X", "C", "V", "B", "N", "M", ".", "_", BACKSPACE],
        [SPACE]
    ];

    /// <summary>
    /// The real thing, without the function keys. Letters come out small unless shift or caps is on, like a keyboard.
    /// </summary>
    public static string[][] Full =>
    [
        ["`", "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "=", BACKSPACE],
        [TAB, "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P", "[", "]", "\\"],
        [CAPS, "A", "S", "D", "F", "G", "H", "J", "K", "L", ";", "'", ENTER],
        [SHIFT, "Z", "X", "C", "V", "B", "N", "M", ",", ".", "/", SHIFT],
        [SPACE]
    ];

    // What the keys of the Dead Revolver keyboard are called in the sheet, by their label
    private static readonly Dictionary<string, string> SpriteNames = new()
    {
        ["`"] = "grave", ["-"] = "minus", ["="] = "equals", ["["] = "lbracket", ["]"] = "rbracket", ["\\"] = "backslash",
        [";"] = "semicolon", ["'"] = "quote", [","] = "comma", ["."] = "period", ["/"] = "slash",
        [BACKSPACE] = "backspace", [SPACE] = "space", [ENTER] = "enter", [SHIFT] = "shift", [CAPS] = "caps", [TAB] = "tab",
        ["ESC"] = "esc", ["CTRL"] = "ctrl", ["ALT"] = "alt", ["UP"] = "up", ["DOWN"] = "down", ["LEFT"] = "left", ["RIGHT"] = "right"
    };

    /// <summary>Whether a label is one of the named keys, which do something rather than type their label.</summary>
    public static bool IsNamed(string label) => label is BACKSPACE or SPACE or ENTER or SHIFT or CAPS or TAB;

    /// <summary>The sprite a key with a label is drawn with, if the skin has it. "key_q" for Q and so on.</summary>
    public static string SpriteFor(string label)
    {
        if (SpriteNames.TryGetValue(label, out var named))
            return "key_" + named;

        return label.Length == 1 && char.IsAsciiLetterOrDigit(label[0]) ? "key_" + char.ToLowerInvariant(label[0]) : string.Empty;
    }

    /// <summary>How wide a key with a label is compared to an ordinary one, when it is drawn without art.</summary>
    public static float WidthFor(string label) => label switch
    {
        SPACE => 6.0f,
        BACKSPACE or ENTER or SHIFT or CAPS or TAB => 1.75f,
        _ => 1.0f
    };

    private readonly KeyDefinition[][] layout;
    private readonly Button[][] keys;
    private readonly Vector2 keySize;
    private int row, column;
    private bool showSelection = true;
    private bool shift, caps;

    /// <summary>The text box the keys type into.</summary>
    public TextBox? Target { get; set; }

    /// <summary>Called with the label of every key that is pressed, after it has typed.</summary>
    public Action<string>? OnKey { get; set; }

    /// <summary>
    /// Whether the keys are drawn with the art the skin has for them (a Q key for Q) rather than as plain buttons with
    /// their labels. On unless switched off, and makes no difference to a skin without any.
    /// </summary>
    public bool UseArt { get; set; } = true;

    /// <summary>
    /// Whether the key that <see cref="Press"/> would press is highlighted. Worth switching off when
    /// only the pointer is used.
    /// </summary>
    public bool ShowSelection
    {
        get => showSelection;
        set
        {
            showSelection = value;
            Highlight();
        }
    }

    /// <summary>Whether the letters come out as capitals right now, by shift or caps.</summary>
    public bool Capitals => shift != caps;

    /// <summary>The label of the highlighted key.</summary>
    public string Selected => layout[row][column].Label;

    public OnScreenKeyboard()
        : this(Alphanumeric) { }

    /// <param name="labels">The labels of the keys, row by row from the top. Rows can differ in length.</param>
    /// <param name="keySize">The size of a key. Keys whose label doesn't fit are as wide as they need.</param>
    public OnScreenKeyboard(string[][] labels, Vector2? keySize = null)
        : this([.. labels.Select(line => line.Select(KeyDefinition.Of).ToArray())], keySize)
    { }

    /// <param name="layout">The keys, row by row from the top. Rows can differ in length.</param>
    /// <param name="keySize">The size of a key. Keys whose label doesn't fit are as wide as they need.</param>
    public OnScreenKeyboard(KeyDefinition[][] layout, Vector2? keySize = null)
    {
        if (layout.Length == 0 || layout.Any(line => line.Length == 0))
            throw new ArgumentException("A keyboard needs at least one key in every row.", nameof(layout));

        this.layout = layout;
        this.keySize = keySize ?? new Vector2(60.0f, 52.0f);
        Spacing = KEY_SPACING;

        keys = new Button[layout.Length][];
        for (int r = 0; r < layout.Length; r++)
        {
            var line = Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = KEY_SPACING });
            keys[r] = new Button[layout[r].Length];

            for (int c = 0; c < layout[r].Length; c++)
            {
                KeyDefinition key = layout[r][c];
                int keyRow = r, keyColumn = c;

                keys[r][c] = line.Add(new Button(key.Label)
                {
                    LabelScale = KEY_LABEL_SCALE,
                    Size = new Vector2(this.keySize.X * key.Width, this.keySize.Y),
                    OnPressed = () =>
                    {
                        // A clicked key becomes the highlighted one, so the pad carries on from there.
                        Select(keyRow, keyColumn);
                        Activate(key);
                    }
                });
            }
        }

        Highlight();
    }

    protected internal override bool Navigable => true;

    protected internal override void OnActivate() => Press();

    protected internal override bool OnNavigate(int right, int down)
    {
        // The edges hand the press on to whatever is past the keyboard
        int newRow = row + down, newColumn = column + right;
        if (newRow < 0 || newRow >= keys.Length || (down == 0 && (newColumn < 0 || newColumn >= keys[row].Length)))
            return false;

        Navigate(right, down);
        return true;
    }

    protected override Vector2 Measure(UISkin skin)
    {
        // What a key looks like depends on the skin, which isn't known before now. The keys pick their look up here
        // and the layout after this one has them at their new sizes
        bool keyArt = skin.TryGetRegion(KEY_STYLE, out _);

        for (int r = 0; r < keys.Length; r++)
        {
            for (int c = 0; c < keys[r].Length; c++)
            {
                KeyDefinition key = layout[r][c];
                Button button = keys[r][c];

                // The art of the key itself, which says everything there is to say about the key
                if (UseArt && key.Sprite.Length > 0 && skin.TryGetRegion(key.Sprite, out var art))
                {
                    button.Style = key.Sprite;
                    button.Label = string.Empty;
                    button.Size = art.Size;
                    button.Animated = false;
                    continue;
                }

                // A blank key with the label on it, or a plain button for a skin without keys
                button.Style = keyArt ? KEY_STYLE : "button";
                button.Label = key.Label;

                float needed = skin.Font.Measure(key.Label, KEY_LABEL_SCALE).X + skin.ButtonPadding.Total.X;
                button.Size = new Vector2(MathF.Max(keySize.X * key.Width, MathF.Ceiling(needed)), keySize.Y);
            }
        }

        return base.Measure(skin);
    }

    /// <summary>
    /// Moves the highlight by a number of keys, positive <paramref name="right"/> goes right and
    /// positive <paramref name="down"/> goes down. It stops at the edges.
    /// </summary>
    public void Navigate(int right, int down)
    {
        int newRow = Math.Clamp(row + down, 0, keys.Length - 1);
        int newColumn = column;

        // Going to a row of a different length lands on the key that is nearest across, by where the keys are
        if (newRow != row)
        {
            float x = keys[row][column].Bounds.Center.X;
            float nearest = float.MaxValue;

            for (int c = 0; c < keys[newRow].Length; c++)
            {
                float away = MathF.Abs(keys[newRow][c].Bounds.Center.X - x);
                if (away < nearest)
                {
                    nearest = away;
                    newColumn = c;
                }
            }
        }

        Select(newRow, Math.Clamp(newColumn + right, 0, keys[newRow].Length - 1));
    }

    /// <summary>Highlights the key with a label, if the layout has it.</summary>
    public bool Select(string label)
    {
        for (int r = 0; r < layout.Length; r++)
        {
            int c = Array.FindIndex(layout[r], key => key.Label == label);
            if (c < 0)
                continue;

            Select(r, c);
            return true;
        }

        return false;
    }

    /// <summary>Presses the highlighted key.</summary>
    public void Press() => Activate(layout[row][column]);

    private void Select(int newRow, int newColumn)
    {
        row = newRow;
        column = newColumn;
        Highlight();
    }

    private void Highlight()
    {
        for (int r = 0; r < keys.Length; r++)
        {
            for (int c = 0; c < keys[r].Length; c++)
                keys[r][c].Selected = showSelection && r == row && c == column;
        }
    }

    private void Activate(KeyDefinition key)
    {
        switch (key.Types)
        {
            case BACKSPACE:
                Target?.Backspace();
                break;

            case SPACE or TAB:
                Target?.Insert(' ');
                break;

            case SHIFT:
                shift = !shift;
                break;

            case CAPS:
                caps = !caps;
                break;

            case ENTER:
                break;

            default:
                if (key.Types.Length == 1)
                {
                    // A letter on the full keyboard is small unless shift or caps says otherwise, the smaller
                    // keyboards type what they show. Shift is used up by the one letter, like the real thing
                    char character = key.Types[0];
                    if (layout.Length > 1 && char.IsAsciiLetter(character) && IsFullLayout)
                        character = Capitals ? char.ToUpperInvariant(character) : char.ToLowerInvariant(character);

                    Target?.Insert(character);
                    shift = false;
                }
                break;
        }

        OnKey?.Invoke(key.Label);
    }

    // The full keyboard has a shift key, the small ones don't
    private bool IsFullLayout => layout.Any(line => line.Any(key => key.Types == SHIFT));
}
