using System.Numerics;

using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>Where a list of a menu goes against what opened it.</summary>
internal enum MenuPlacement
{
    /// <summary>Under a word of the bar, moved along to stay in the window but never over the bar.</summary>
    Below,

    /// <summary>At the point of a click, up from it when there's no room under it.</summary>
    AtPoint,

    /// <summary>To the side of the item that opens it, the right if it fits, else the left.</summary>
    BesideItem
}

// Where the list goes and how it scrolls. No list ever leaves the window, one too tall for it scrolls, with a strip at
// the top and the bottom that scrolls it while the pointer rests there and the wheel scrolling it too
internal sealed partial class MenuPanel
{
    // How far a list keeps from the edges of the window
    private const float MARGIN = 4.0f;

    // How many rows a second the strips scroll it, and a notch of the wheel
    private const float STRIP_RATE = 12.0f;
    private const float WHEEL_ROWS = 3.0f;

    private readonly MenuPlacement placement;

    // What it's placed against, the word of the bar, the point (an empty rectangle) or the item, and the list of that item
    private readonly UIRect anchor;
    private readonly UIRect parentList;
    private readonly float minWidth;

    // What it was placed for last time, so it goes again when the window or what's shown in it changes
    private UIRect placedFrame;
    private int placedKey;

    // How tall everything in it is, how wide it would be with room for all of it, and how far down it's scrolled
    private float contentHeight;
    private float naturalWidth;
    private float scroll;
    private bool scrolls;

    /// <summary>Whether it is taller than the room it has and scrolls.</summary>
    public bool Scrolls => scrolls;

    // The strips at the top and the bottom of one that scrolls
    private float StripHeight(UISkin skin) => MathF.Round(RowHeight(skin, textScale) * 0.6f);

    // Where the rows are, inside the border and the strips
    private UIRect View(UISkin skin)
    {
        UIRect inside = bounds.Shrink(new UIEdges(BORDER));
        if (!scrolls)
            return inside;

        float strip = StripHeight(skin);
        return new UIRect(new Vector2(inside.Min.X, inside.Min.Y + strip), new Vector2(inside.Max.X, inside.Max.Y - strip));
    }

    private UIRect TopStrip(UISkin skin)
    {
        UIRect inside = bounds.Shrink(new UIEdges(BORDER));
        return new UIRect(new Vector2(inside.Min.X, inside.Max.Y - StripHeight(skin)), inside.Max);
    }

    private UIRect BottomStrip(UISkin skin)
    {
        UIRect inside = bounds.Shrink(new UIEdges(BORDER));
        return new UIRect(inside.Min, new Vector2(inside.Max.X, inside.Min.Y + StripHeight(skin)));
    }

    private float MaxScroll(UISkin skin) => scrolls ? MathF.Max(0.0f, contentHeight - View(skin).Height) : 0.0f;

    // Something that changes when what's shown does, which items are there right now
    private int ShownKey()
    {
        var hash = new HashCode();
        for (int i = 0; i < menu.Items.Count; i++)
            hash.Add(ShownAt(i));

        return hash.ToHashCode();
    }

    /// <summary>Places it again if the window or what is shown in it changed since it was placed.</summary>
    private void PlaceIfChanged(UISkin skin, UIRect frame)
    {
        if (frame != placedFrame || ShownKey() != placedKey)
            Place(skin, frame);
    }

    private void Place(UISkin skin, UIRect frame)
    {
        placedFrame = frame;
        placedKey = ShownKey();

        float scale = ScaleOf(skin), row = RowHeight(skin, textScale);
        float width = 0.0f, height = 0.0f;
        for (int index = 0; index < menu.Items.Count; index++)
        {
            MenuItem item = menu.Items[index];
            if (!ShownAt(index)) continue;

            height += HeightOf(item, row);
            width = MathF.Max(width, Lead(row) + skin.Font.Measure(item.Label, scale, markup: false).X + EndWidth(skin, item, row) + ITEM_PADDING * 2.0f);
        }

        contentHeight = height;
        naturalWidth = MathF.Max(width, minWidth) + BORDER * 2.0f;

        // Never wider or taller than the window, a bar's list no taller than the room under the bar
        float roomX = MathF.Max(frame.Width - MARGIN * 2.0f, 1.0f);
        float roomY = placement == MenuPlacement.Below ? anchor.Min.Y - frame.Min.Y - MARGIN : frame.Height - MARGIN * 2.0f;
        roomY = MathF.Max(roomY, row * 3.0f);

        Vector2 size = new(MathF.Min(naturalWidth, roomX), MathF.Min(height + BORDER * 2.0f, roomY));
        scrolls = height + BORDER * 2.0f > roomY + 0.5f;

        float lowest = frame.Min.X + MARGIN, highest = frame.Max.X - MARGIN - size.X;
        float bottom = frame.Min.Y + MARGIN, ceiling = frame.Max.Y - MARGIN;
        float left, top;
        switch (placement)
        {
            case MenuPlacement.Below:
                left = anchor.Min.X;
                top = anchor.Min.Y;
                break;

            case MenuPlacement.AtPoint:
                // Right of the point, or left of it with no room there, and down from it, or up with no room under
                left = anchor.Min.X + size.X <= frame.Max.X - MARGIN ? anchor.Min.X : anchor.Min.X - size.X;
                top = anchor.Min.Y - size.Y >= bottom ? anchor.Min.Y : anchor.Min.Y + size.Y <= ceiling ? anchor.Min.Y + size.Y : bottom + size.Y;
                break;

            default:
                // Right of the item, or left of its list with no room there, its top with the item's, up from there
                // with no room under it
                left = anchor.Max.X + BORDER + size.X <= frame.Max.X - MARGIN ? anchor.Max.X + BORDER : parentList.Min.X - size.X + BORDER;
                top = anchor.Max.Y + BORDER;
                if (top - size.Y < bottom)
                    top = bottom + size.Y;
                break;
        }

        left = MathF.Max(MathF.Min(left, highest), lowest);
        top = MathF.Min(MathF.Max(top, bottom + size.Y), ceiling);
        bounds = new UIRect(new Vector2(left, top - size.Y), new Vector2(left + size.X, top));
        scroll = Math.Clamp(scroll, 0.0f, MaxScroll(skin));
    }

    // The room at the end of an item for its keys or the arrow of a menu of its own
    private float EndWidth(UISkin skin, MenuItem item, float row) =>
        item.Submenu is not null ? row : item.Shortcut.Length > 0 ? skin.Font.Measure(UIShortcutText.Shown(item.Shortcut), ScaleOf(skin), markup: false).X + SHORTCUT_GAP : 0.0f;

    /// <summary>Scrolls so the item at an index is all in view, for a test or the keyboard reaching it.</summary>
    public void Reveal(UISkin skin, int index)
    {
        if (!scrolls || index < 0 || index >= menu.Items.Count || !ShownAt(index))
            return;

        float row = RowHeight(skin, textScale), above = 0.0f;
        for (int i = 0; i < index; i++)
        {
            if (ShownAt(i))
                above += HeightOf(menu.Items[i], row);
        }

        float view = View(skin).Height, tall = HeightOf(menu.Items[index], row);
        float was = scroll;
        if (above < scroll)
            scroll = above;
        else if (above + tall > scroll + view)
            scroll = above + tall - view;

        scroll = Math.Clamp(scroll, 0.0f, MaxScroll(skin));
        if (scroll != was && childIndex != index)
            CloseChild();
    }

    /// <summary>A turn of the wheel over the lists, the one under the pointer scrolled. Whether it was over any of them.</summary>
    public bool Wheel(UISkin skin, Vector2 pointer, float delta)
    {
        if (child is not null && child.Contains(pointer))
            return child.Wheel(skin, pointer, delta);

        if (!bounds.Contains(pointer))
            return false;

        ScrollBy(skin, -delta * WHEEL_ROWS * RowHeight(skin, textScale));
        return true;
    }

    private void ScrollBy(UISkin skin, float by)
    {
        float was = scroll;
        scroll = Math.Clamp(scroll + by, 0.0f, MaxScroll(skin));

        // What was open off an item it scrolled away from goes, it would point at nothing
        if (scroll != was)
            CloseChild();
    }

    private void CloseChild()
    {
        child = null;
        childIndex = -1;
    }

    // The strips scroll it while the pointer rests on one, a dozen rows a second
    private void ScrollFromStrips(UISkin skin, Vector2 pointer, float delta)
    {
        if (!scrolls || delta <= 0.0f)
            return;

        float by = STRIP_RATE * RowHeight(skin, textScale) * MathF.Min(delta, 0.1f);
        if (TopStrip(skin).Contains(pointer))
            ScrollBy(skin, -by);
        else if (BottomStrip(skin).Contains(pointer))
            ScrollBy(skin, by);
    }

    // What an item says cut short with dots to fit, for a list narrower than its longest item
    private string Fitted(UISkin skin, string label, float room)
    {
        float scale = ScaleOf(skin);
        if (naturalWidth <= bounds.Width + 0.5f || skin.Font.Measure(label, scale, markup: false).X <= room)
            return label;

        for (int length = label.Length - 1; length > 0; length--)
        {
            string shorter = string.Concat(label.AsSpan(0, length).TrimEnd(), "...");
            if (skin.Font.Measure(shorter, scale, markup: false).X <= room)
                return shorter;
        }

        return "...";
    }

    /// <summary>The lists open now, this one and the ones off it, for a test that they're all in the window.</summary>
    public IEnumerable<UIRect> AllBounds()
    {
        yield return bounds;
        if (child is not null)
        {
            foreach (UIRect inside in child.AllBounds())
                yield return inside;
        }
    }
}
