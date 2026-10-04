using System.Numerics;

using Horizon.Rendering.UIX.Skinning;

namespace Horizon.Rendering.UIX.Components;

/// <summary>
/// A grid of keys that types into a <see cref="TextBox"/>, for when there is no real keyboard.
/// The keys can be clicked, or driven by a gamepad: the game calls <see cref="Navigate"/> when the
/// d-pad is pressed and <see cref="Press"/> for its confirm button, the highlighted key is the one
/// that gets pressed.
/// A key is just its label. One character types that character; <see cref="BACKSPACE"/> and
/// <see cref="SPACE"/> do what they say; anything else (e.g. "CONNECT") types nothing and is only
/// reported through <see cref="OnKey"/>, so a layout can carry its own actions.
/// </summary>
public class OnScreenKeyboard : StackPanel
{
    /// <summary>The key that removes the last character.</summary>
    public const string BACKSPACE = "DEL";

    /// <summary>The key that types a space.</summary>
    public const string SPACE = "SPACE";

    private const float KEY_SPACING = 6.0f;
    private const float KEY_LABEL_SCALE = 0.3f;

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

    private readonly Button[][] keys;
    private readonly Vector2 keySize;
    private int row, column;
    private bool showSelection = true;

    /// <summary>The text box the keys type into.</summary>
    public TextBox? Target { get; set; }

    /// <summary>Called with the label of every key that is pressed, after it has typed.</summary>
    public Action<string>? OnKey { get; set; }

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

    /// <summary>The label of the highlighted key.</summary>
    public string Selected => keys[row][column].Label;

    public OnScreenKeyboard()
        : this(Alphanumeric) { }

    /// <param name="layout">The labels of the keys, row by row from the top. Rows can differ in length.</param>
    /// <param name="keySize">The size of a key. Keys whose label doesn't fit are as wide as they need.</param>
    public OnScreenKeyboard(string[][] layout, Vector2? keySize = null)
    {
        if (layout.Length == 0 || layout.Any(line => line.Length == 0))
            throw new ArgumentException("A keyboard needs at least one key in every row.", nameof(layout));

        this.keySize = keySize ?? new Vector2(60.0f, 52.0f);
        Spacing = KEY_SPACING;

        keys = new Button[layout.Length][];
        for (int r = 0; r < layout.Length; r++)
        {
            var line = Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = KEY_SPACING });
            keys[r] = new Button[layout[r].Length];

            for (int c = 0; c < layout[r].Length; c++)
            {
                string label = layout[r][c];
                int keyRow = r, keyColumn = c;

                keys[r][c] = line.Add(new Button(label)
                {
                    LabelScale = KEY_LABEL_SCALE,
                    Size = this.keySize,
                    OnPressed = () =>
                    {
                        // A clicked key becomes the highlighted one, so the pad carries on from there.
                        Select(keyRow, keyColumn);
                        Activate(label);
                    }
                });
            }
        }

        Highlight();
    }

    protected override Vector2 Measure(UISkin skin)
    {
        // Keys are all one size, apart from the ones whose label needs more room. That depends on the
        // font, which isn't known before now; the keys pick the new width up on the next layout.
        foreach (var line in keys)
        {
            foreach (var key in line)
            {
                float needed = skin.Font.Measure(key.Label, KEY_LABEL_SCALE).X + skin.ButtonPadding.Total.X;
                key.Size = new Vector2(MathF.Max(keySize.X, MathF.Ceiling(needed)), keySize.Y);
            }
        }

        return base.Measure(skin);
    }

    /// <summary>
    /// Moves the highlight by a number of keys: positive <paramref name="right"/> goes right and
    /// positive <paramref name="down"/> goes down. It stops at the edges.
    /// </summary>
    public void Navigate(int right, int down)
    {
        int newRow = Math.Clamp(row + down, 0, keys.Length - 1);
        int newColumn = column;

        // Going to a row of a different length lands on the key that is nearest across.
        if (newRow != row && keys[row].Length > 1)
        {
            float across = column / (float)(keys[row].Length - 1);
            newColumn = (int)MathF.Round(across * (keys[newRow].Length - 1), MidpointRounding.AwayFromZero);
        }

        Select(newRow, Math.Clamp(newColumn + right, 0, keys[newRow].Length - 1));
    }

    /// <summary>Highlights the key with a label, if the layout has it.</summary>
    public bool Select(string label)
    {
        for (int r = 0; r < keys.Length; r++)
        {
            int c = Array.FindIndex(keys[r], key => key.Label == label);
            if (c < 0)
                continue;

            Select(r, c);
            return true;
        }

        return false;
    }

    /// <summary>Presses the highlighted key.</summary>
    public void Press() => Activate(Selected);

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

    private void Activate(string label)
    {
        if (label == BACKSPACE)
            Target?.Backspace();
        else if (label == SPACE)
            Target?.Insert(' ');
        else if (label.Length == 1)
            Target?.Insert(label[0]);

        OnKey?.Invoke(label);
    }
}
