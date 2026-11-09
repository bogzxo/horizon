using System.Numerics;

using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// A button on a row of a <see cref="TreeView"/>, an icon of <see cref="UISymbols"/> at the row's right end that shows
/// while the pointer is on the row (or the row is picked in a tree with the keyboard). VS Code's inline actions, the
/// stage and discard buttons on a file of Source Control.
/// </summary>
public sealed record UITreeAction(string Icon, string Tooltip, Action Pressed);

public sealed partial class UITreeItem
{
    /// <summary>
    /// A letter or a number at the row's right edge, in a colour of its own. What git thinks of a file (M, U, D), or how
    /// many rows a heading has. Nothing for none.
    /// </summary>
    public string Badge { get; set; } = string.Empty;

    /// <summary>The colour of the badge, null for the dim one.</summary>
    public Vector4? BadgeColor { get; set; }

    /// <summary>The buttons at the row's right end, left of the badge, null for none.</summary>
    public IReadOnlyList<UITreeAction>? Actions { get; set; }
}

// The badge and the buttons at the right end of a row, and the keys handed to the owner first
public partial class TreeView
{
    // How far the buttons are apart, and how far in from the top and bottom of the row their squares are
    private const float ACTION_GAP = 1.0f, ACTION_INSET = 2.0f;

    // Which button of the row under the pointer the pointer is on, -1 for none
    private int hoveredAction = -1;

    /// <summary>
    /// A key pressed with a row selected, asked before the tree does anything with it. True says the owner did something
    /// with it and the tree leaves it alone. Space to stage a file, Delete to throw its changes away.
    /// </summary>
    public Func<UITreeItem, char, bool>? OnKey { get; set; }

    /// <summary>A key pressed on the tree as if it had the keyboard, for a test or a program driving it.</summary>
    public void PressKey(char key) => OnTextInput(key);

    /// <summary>Where a button of a row is on screen right now (like <see cref="RowBounds"/>), null when it has no such button or isn't in sight.</summary>
    public UIRect? ActionBounds(UITreeItem item, int index)
    {
        if (Module?.Compositor.Skin is not { } skin || item.Actions is not { } actions || index < 0 || index >= actions.Count || RowBounds(item) is not { } row)
            return null;

        return ActionRect(item, row with { Max = new Vector2(RightOf(skin), row.Max.Y) }, BadgeWidth(item, skin, ScaleOf(skin)), index);
    }

    // Where the rows end on the right, short of the scroll bar when there is one
    private float RightOf(UISkin skin) => Travel(skin) > 0.0f ? Bounds.Max.X - BAR_WIDTH * 2.0f : Bounds.Max.X;

    // How much of the row's right end its badge takes, the gap before it included
    private static float BadgeWidth(UITreeItem item, UISkin skin, float scale) =>
        item.Badge.Length > 0 ? skin.Font.Measure(item.Badge, scale, markup: false).X + DETAIL_GAP : 0.0f;

    // Whether a row's buttons show, the row under the pointer or the picked one of a tree with the keyboard
    private bool ShowsActions(UITreeItem item) => item.Actions is { Count: > 0 } && (item == hovered || item == selected && IsFocused && TakesKeys);

    // The square of one of a row's buttons, counted from the left, all of them packed up against the badge
    private static UIRect ActionRect(UITreeItem item, UIRect row, float badge, int index)
    {
        float side = row.Height - ACTION_INSET * 2.0f;
        float left = row.Max.X - LEFT_PAD - badge - (item.Actions!.Count - index) * (side + ACTION_GAP);
        return new UIRect(new Vector2(left, row.Min.Y + ACTION_INSET), new Vector2(left + side, row.Max.Y - ACTION_INSET));
    }

    // Where the first of a row's buttons starts, the text is cut off short of it while they show
    private static float ActionsLeft(UITreeItem item, UIRect row, float badge) =>
        item.Actions is { Count: > 0 } ? ActionRect(item, row, badge, 0).Min.X - ICON_GAP : row.Max.X;

    // Which button of a row a point is on, -1 for none
    private int ActionAt(UITreeItem item, Vector2 point)
    {
        if (item.Actions is not { Count: > 0 } actions)
            return -1;

        for (int i = 0; i < actions.Count; i++)
        {
            if (ActionBounds(item, i) is { } square && square.Contains(point))
                return i;
        }

        return -1;
    }

    // A button pressed, before the row is selected or opened, true when the press was on one
    private bool PressAction(UITreeItem item, Vector2 point)
    {
        if (ActionAt(item, point) is not (>= 0 and var index))
            return false;

        lastClicked = null;
        item.Actions![index].Pressed();
        return true;
    }

    // The button the pointer is on worked out with the row, its tooltip in place of the row's
    private void UpdateActions(Vector2 pointer)
    {
        hoveredAction = hovered is { } row ? ActionAt(row, pointer) : -1;
        if (hoveredAction >= 0)
            Tooltip = hovered!.Actions![hoveredAction].Tooltip;
    }

    // The badge at the right end of a row, in its colour
    private static void PaintBadge(UIDrawList list, UITreeItem item, UIRect row, float scale, Vector4 dim)
    {
        if (item.Badge.Length == 0)
            return;

        UISkin skin = list.Skin;
        Vector2 size = skin.Font.Measure(item.Badge, scale, markup: false);
        list.Text(item.Badge, new Vector2(row.Max.X - LEFT_PAD - size.X, MathF.Round(row.Center.Y + size.Y * 0.5f)), scale, item.BadgeColor ?? dim, markup: false);
    }

    // A row's buttons. Painted on every row that has them, see-through where they don't show, so a row's quads are the
    // same few whether the pointer is on it or not and never get blended with another's (see the highlight in Paint)
    private void PaintActions(UIDrawList list, UITreeItem item, UIRect row, float badge, float icon, Vector4 text)
    {
        if (item.Actions is not { Count: > 0 } actions)
            return;

        UISkin skin = list.Skin;
        bool shown = ShowsActions(item);
        for (int i = 0; i < actions.Count; i++)
        {
            UIRect square = ActionRect(item, row, badge, i);
            bool lit = shown && item == hovered && i == hoveredAction;
            list.RoundRect(square, skin.CornerRadius, skin.HoverColor with { W = lit ? MathF.Max(skin.HoverColor.W, 0.12f) * 2.0f : 0.0f });

            var glyph = new UIRect(square.Center - new Vector2(icon * 0.5f), square.Center + new Vector2(icon * 0.5f));
            list.Symbol(actions[i].Icon, glyph, text with { W = shown ? text.W * (lit ? 1.0f : 0.8f) : 0.0f });
        }
    }
}
