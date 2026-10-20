using System.Numerics;

using Horizon.Rendering;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// Helper class for the list of a <see cref="Menu"/> that pops up, out of a <see cref="MenuBar"/> or as a
/// <see cref="ContextMenu"/>, and the list of whichever of its items is a menu of its own, opened by resting the
/// pointer on it, and so on down. It lays the lists out, paints them and says what is under the pointer, the two
/// components only say where the first list goes and what happens when one is picked.
/// </summary>
internal sealed class MenuPanel
{
    private const float BORDER = 5.0f;
    private const float ITEM_PADDING = 12.0f;

    // The room kept in front of every item for its icon or the mark of the ones that are checked, and between an item and its shortcut
    private const float CHECK_WIDTH = 22.0f;
    private const float SHORTCUT_GAP = 28.0f;

    // How tall a separator is against an item
    private const float SEPARATOR_SHARE = 0.4f;

    // The arrow at the end of an item that opens a menu of its own
    private const string SUBMENU_ARROW = "chevron-right";

    private readonly float textScale;

    // What the parts of this list are counted from, a list open off another counts on from that one's (UIDrawList.BeginPart)
    private readonly int partBase;
    private const int PARTS_PER_LEVEL = 1000;
    private readonly Menu menu;
    private UIRect bounds;

    // The list of the item that is a menu of its own and is open, and which item that is
    private MenuPanel? child;
    private int childIndex = -1;

    // With a menu open off an item, the pointer has to rest on another for a moment before that one takes over. On its way
    // to a far item of the open menu it crosses the items between, which would swap the menu out from under it, every
    // menu there is waits a little for that reason. Which item is waiting, and since when
    private const long SWAP_DELAY = 300;
    private int waiting = -1;
    private long waitingSince;

    public Menu Menu => menu;
    public UIRect Bounds => bounds;

    /// <param name="corner">Where the top left corner of the list goes (Y up), pushed back inside the frame if it would hang off it.</param>
    /// <param name="minWidth">How narrow it can be at the least, the width of the word on the bar for a menu of one.</param>
    public MenuPanel(Menu menu, UISkin skin, float textScale, Vector2 corner, UIRect frame, float minWidth = 0.0f, int partBase = 0)
    {
        this.menu = menu;
        this.textScale = textScale;
        this.partBase = partBase;
        bounds = Place(skin, corner, frame, minWidth);
    }

    private float ScaleOf(UISkin skin) => textScale > 0.0f ? textScale : skin.TextScale;

    public static float RowHeight(UISkin skin, float textScale) =>
        MathF.Max(skin.RowHeight, MathF.Round(skin.Font.LineHeight * (textScale > 0.0f ? textScale : skin.TextScale) + skin.ButtonPadding.Total.Y * 0.6f));

    private float HeightOf(MenuItem item, float row) => item.IsSeparator ? MathF.Round(row * SEPARATOR_SHARE) : row;

    private UIRect Place(UISkin skin, Vector2 corner, UIRect frame, float minWidth)
    {
        float scale = ScaleOf(skin), row = RowHeight(skin, textScale);
        float width = 0.0f, height = 0.0f;

        foreach (MenuItem item in menu.Items)
        {
            if (!item.Shown) continue;

            height += HeightOf(item, row);

            float end = item.Submenu is not null ? row : item.Shortcut.Length > 0 ? skin.Font.Measure(UIShortcutText.Shown(item.Shortcut), scale, markup: false).X + SHORTCUT_GAP : 0.0f;
            width = MathF.Max(width, CHECK_WIDTH + skin.Font.Measure(item.Label, scale, markup: false).X + end + ITEM_PADDING * 2.0f);
        }

        Vector2 size = new(MathF.Max(width, minWidth) + BORDER * 2.0f, height + BORDER * 2.0f);

        // Down and to the right of the corner, pushed back in if that runs off the frame (Y is up)
        float left = MathF.Max(MathF.Min(corner.X, frame.Max.X - size.X), frame.Min.X);
        float top = MathF.Min(MathF.Max(corner.Y, frame.Min.Y + size.Y), frame.Max.Y);
        return new UIRect(new Vector2(left, top - size.Y), new Vector2(left + size.X, top));
    }

    /// <summary>Where one of the items is, counted from the top with the separators counted too. Empty for one that isn't shown.</summary>
    public UIRect ItemBounds(UISkin skin, int index)
    {
        if (index < 0 || index >= menu.Items.Count || !menu.Items[index].Shown)
            return default;

        UIRect list = bounds.Shrink(new UIEdges(BORDER));
        float row = RowHeight(skin, textScale);
        float top = list.Max.Y;

        for (int i = 0; i < index; i++)
        {
            if (menu.Items[i].Shown)
                top -= HeightOf(menu.Items[i], row);
        }

        return new UIRect(new Vector2(list.Min.X, top - HeightOf(menu.Items[index], row)), new Vector2(list.Max.X, top));
    }

    /// <summary>Where an item is by what it says, in this list or the open one of its items. Empty if there is none.</summary>
    public UIRect ItemBounds(UISkin skin, string label)
    {
        if (child?.ItemBounds(skin, label) is { IsEmpty: false } inside)
            return inside;

        for (int i = 0; i < menu.Items.Count; i++)
        {
            if (menu.Items[i].Label == label)
                return ItemBounds(skin, i);
        }

        return default;
    }

    /// <summary>Whether a point is on this list or one of the lists open off it.</summary>
    public bool Contains(Vector2 point) => bounds.Contains(point) || (child?.Contains(point) ?? false);

    /// <summary>The menu open off an item, the deepest one, null for none.</summary>
    public Menu? OpenSubmenu => child?.OpenSubmenu ?? child?.menu;

    /// <summary>
    /// Opens the list of the item under the pointer when it is a menu of its own and shuts one that it has gone off,
    /// the way a menu does as the pointer goes down it. Called whenever the lists are painted.
    /// </summary>
    public void Track(UISkin skin, UIRect frame, Vector2 pointer)
    {
        child?.Track(skin, frame, pointer);

        // Somewhere in the list that is open off this one, it stays open
        if (child is not null && child.Contains(pointer))
        {
            Moved(pointer);
            return;
        }

        if (!bounds.Contains(pointer))
        {
            Moved(pointer);
            return;
        }

        for (int i = 0; i < menu.Items.Count; i++)
        {
            UIRect area = ItemBounds(skin, i);
            if (!area.Contains(pointer) || i == childIndex)
                continue;

            // Nothing open off this list, the pointer has it at once. Something is and the pointer is on its way to it
            // across another item, the list off this one waits a moment for it to get there. Anywhere else (up or down
            // this list) it's the other item's at once, the wait for every item made going back up a list feel stuck
            if (child is not null && Aiming(pointer))
            {
                long now = Environment.TickCount64;
                if (waiting != i)
                {
                    waiting = i;
                    waitingSince = now;
                    Moved(pointer);
                    return;
                }

                if (now - waitingSince < SWAP_DELAY)
                {
                    Moved(pointer);
                    return;
                }
            }

            waiting = -1;
            Moved(pointer);
            MenuItem item = menu.Items[i];
            if (item.Submenu is { } submenu && item.Enabled)
            {
                Open(skin, frame, i, submenu, area);
            }
            else
            {
                child = null;
                childIndex = -1;
            }

            return;
        }

        Moved(pointer);
    }

    // Where the pointer was the last time it moved, for which way it's going
    private Vector2 lastPointer;
    private bool pointerSeen;

    private void Moved(Vector2 pointer)
    {
        if (pointerSeen && Vector2.DistanceSquared(pointer, lastPointer) < 0.25f)
            return;

        lastPointer = pointer;
        pointerSeen = true;
    }

    /// <summary>
    /// Whether the pointer is on its way to the list open off this one, inside the triangle from where it last was to
    /// the two corners of that list's near edge. Still, or going anywhere else, it isn't.
    /// </summary>
    private bool Aiming(Vector2 pointer)
    {
        if (child is not { } open || !pointerSeen || Vector2.DistanceSquared(pointer, lastPointer) < 0.25f)
            return false;

        bool right = open.bounds.Min.X >= bounds.Center.X;
        float edge = right ? open.bounds.Min.X : open.bounds.Max.X;

        // A little more room than the list itself, a hand isn't a ruler
        var top = new Vector2(edge, open.bounds.Max.Y + 8.0f);
        var bottom = new Vector2(edge, open.bounds.Min.Y - 8.0f);
        return InTriangle(pointer, lastPointer, top, bottom);
    }

    private static bool InTriangle(Vector2 point, Vector2 a, Vector2 b, Vector2 c)
    {
        static float Side(Vector2 p, Vector2 from, Vector2 to) => (to.X - from.X) * (p.Y - from.Y) - (to.Y - from.Y) * (p.X - from.X);
        float ab = Side(point, a, b), bc = Side(point, b, c), ca = Side(point, c, a);
        bool negative = ab < 0.0f || bc < 0.0f || ca < 0.0f, positive = ab > 0.0f || bc > 0.0f || ca > 0.0f;
        return !(negative && positive);
    }

    /// <summary>Opens the list of one of the items that is a menu of its own, by what it says. For a test, or the keyboard.</summary>
    public bool OpenSubmenuOf(UISkin skin, UIRect frame, string label)
    {
        for (int i = 0; i < menu.Items.Count; i++)
        {
            if (menu.Items[i].Label != label || menu.Items[i].Submenu is not { } submenu)
                continue;

            Open(skin, frame, i, submenu, ItemBounds(skin, i));
            return true;
        }

        return child?.OpenSubmenuOf(skin, frame, label) ?? false;
    }

    private void Open(UISkin skin, UIRect frame, int index, Menu submenu, UIRect area)
    {
        childIndex = index;
        child = new MenuPanel(submenu, skin, textScale, new Vector2(area.Max.X + BORDER, area.Max.Y + BORDER), frame, 0.0f, partBase + PARTS_PER_LEVEL);

        // No room on the right, it goes on the left of this list instead
        if (child.bounds.Min.X < area.Max.X - 1.0f)
            child = new MenuPanel(submenu, skin, textScale, new Vector2(bounds.Min.X - child.bounds.Width + BORDER, area.Max.Y + BORDER), frame, 0.0f, partBase + PARTS_PER_LEVEL);
    }

    /// <summary>
    /// What is under a point the pointer let go of. The item, or null for nothing that can be picked. An item that is
    /// a menu of its own is not picked, it opens.
    /// </summary>
    /// <param name="inside">Whether the point was on any of the lists at all.</param>
    public MenuItem? Pick(UISkin skin, Vector2 point, out bool inside)
    {
        if (child is not null && child.Contains(point))
            return child.Pick(skin, point, out inside);

        inside = bounds.Contains(point);
        if (!inside)
            return null;

        for (int i = 0; i < menu.Items.Count; i++)
        {
            if (!ItemBounds(skin, i).Contains(point))
                continue;

            MenuItem item = menu.Items[i];
            return item.Enabled && item.Submenu is null ? item : null;
        }

        return null;
    }

    /// <param name="glass">What the list sits on, null for a solid panel, see <see cref="UICompositor.PopupGlass"/>.</param>
    // When the list was first painted, by the draw list's clock, for its entrance
    private float openedAt = -1.0f;

    // Where the highlight is drawn, on its way to the item it's for, and whether it's on one at all
    private UIRect highlight;
    private bool highlighted;

    // How quickly the highlight gets to its item, a share of the way a second as the rate of an exponential
    private const float HIGHLIGHT_RATE = 30.0f;

    // A part number no item has, the highlight's own
    private const int HIGHLIGHT_PART = PARTS_PER_LEVEL - 1;

    /// <summary>Helper method to paint the highlight under the item the pointer is on (or whose submenu is open), eased to it.</summary>
    private void PaintHighlight(UIDrawList list, UISkin skin, Vector2 pointer)
    {
        // The item under the pointer first, the one whose list is open when the pointer is on none of this list's
        UIRect target = default;
        bool any = false;
        for (int pass = 0; pass < 2 && !any; pass++)
        {
            for (int i = 0; i < menu.Items.Count && !any; i++)
            {
                MenuItem item = menu.Items[i];
                if (!item.Shown || item.IsSeparator || !item.Enabled)
                    continue;

                UIRect area = ItemBounds(skin, i);
                if (pass == 0 ? area.Contains(pointer) : childIndex == i)
                {
                    target = area.Shrink(new UIEdges(4.0f, 1.0f));
                    any = true;
                }
            }
        }

        if (!any)
        {
            highlighted = false;
            return;
        }

        // Onto the first item it's on at once, from one to the next eased
        if (!highlighted || !UIDrawList.Motion)
            highlight = target;
        else
        {
            float share = 1.0f - MathF.Exp(-HIGHLIGHT_RATE * list.Delta);
            highlight = new UIRect(Vector2.Lerp(highlight.Min, target.Min, share), Vector2.Lerp(highlight.Max, target.Max, share));
            if (Vector2.DistanceSquared(highlight.Min, target.Min) < 0.25f)
                highlight = target;
        }

        highlighted = true;
        list.BeginPart(partBase + HIGHLIGHT_PART);
        list.RoundRect(highlight, skin.CornerRadius, skin.HoverColor);
    }

    public void Paint(UIDrawList list, Vector2 pointer, UIGlass? glass = null)
    {
        UISkin skin = list.Skin;
        float scale = ScaleOf(skin);

        // It grows down out of the word on the bar (or the point of the click), a submenu slides in from its parent
        bool entering = list.BeginEntrance(ref openedAt, new Vector2(bounds.Min.X, bounds.Max.Y), partBase > 0 ? new Vector2(-6.0f, 0.0f) : new Vector2(0.0f, 6.0f));

        // What every popup sits on, a shadow, the glass when there is any, the panel and a hairline
        list.Popup(bounds, glass);

        Vector4 dim = skin.TextColor * skin.DisabledTint;

        // The highlight glides from the item it was on to the one under the pointer rather than jumping, a part of its own
        PaintHighlight(list, skin, pointer);

        for (int i = 0; i < menu.Items.Count; i++)
        {
            // An item at a time, or the highlight of one has the rule after it blended with it for a tick
            list.BeginPart(partBase + i);
            MenuItem item = menu.Items[i];
            if (!item.Shown)
                continue;

            UIRect area = ItemBounds(skin, i);

            if (item.IsSeparator)
            {
                float middle = MathF.Round(area.Center.Y);
                list.Rect(new UIRect(new Vector2(area.Min.X + ITEM_PADDING, middle), new Vector2(area.Max.X - ITEM_PADDING, middle + 1.0f)), dim);
                continue;
            }

            bool enabled = item.Enabled;

            UIRect content = area.Shrink(new UIEdges(ITEM_PADDING, 0.0f));
            Vector4 color = enabled ? skin.TextColor : dim;
            float side = MathF.Round(area.Height * 0.6f);
            UIRect mark = new(new Vector2(content.Min.X - 4.0f, MathF.Round(content.Center.Y - side * 0.5f)), new Vector2(content.Min.X - 4.0f + side, MathF.Round(content.Center.Y + side * 0.5f)));

            // A checked item has a tick in the room in front of it, one with an icon has the icon there
            if (item.IsChecked?.Invoke() == true)
                list.Symbol("check", mark, skin.AccentColor);
            else if (item.Icon.Length > 0)
                list.Symbol(item.Icon, mark, color);

            UIRect text = new(new Vector2(content.Min.X + CHECK_WIDTH, content.Min.Y), content.Max);
            list.Text(item.Label, text, Origin.Left, scale, color, markup: false);

            if (item.Submenu is not null)
                list.Symbol(SUBMENU_ARROW, new UIRect(new Vector2(content.Max.X - side, mark.Min.Y), new Vector2(content.Max.X, mark.Max.Y)), dim);
            else if (item.Shortcut.Length > 0)
                list.Text(UIShortcutText.Shown(item.Shortcut), text, Origin.Right, scale, dim, markup: false);
        }

        list.EndParts();

        if (entering)
            list.EndEntrance();

        child?.Paint(list, pointer, glass);
    }
}
