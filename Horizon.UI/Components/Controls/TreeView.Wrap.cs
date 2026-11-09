using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

public sealed partial class UITreeItem
{
    /// <summary>
    /// Whether the row is a note that goes onto as many rows as it takes at the tree's width, rather than being cut short
    /// with dots. What a list says while it has nothing in it, which is a sentence and wants reading whole. The rows after
    /// the first are the tree's own, they line up with the text of the first.
    /// </summary>
    public bool Wraps { get; set; }

    // The rows it's broken into and the width it was broken at, the first of them itself
    internal List<UITreeItem>? wrapped;
    internal float wrappedAt;
    internal string? wrappedOf;
}

// Notes that go onto more than one row. Every row of a tree is as tall as the next, so a note too long for one is broken
// into rows of its own at the width the tree has, and broken again when that changes. The first of them is the note
// itself, the rest are rows the tree makes, with the note's tag and colour and no icon of their own.
public partial class TreeView
{
    // The width the notes were last broken at, and whether there's a note to break at all
    private float wrapWidth;
    private bool hasWraps;

    // Where a line of a note starts and how long it is, filled again for every note
    private readonly List<(int Start, int Length)> wrapLines = [];

    /// <summary>Helper method to put a note's rows in the list, broken at the width the tree has now.</summary>
    private void FlattenNote(UITreeItem item, UITreeItem? parent, int depth)
    {
        hasWraps = true;
        if (Module?.Compositor.Skin is not { } skin || wrapWidth <= 0.0f)
        {
            rows.Add((item, depth));
            return;
        }

        if (item.wrapped is null || item.wrappedAt != wrapWidth || !ReferenceEquals(item.wrappedOf, item.Text))
            BreakNote(item, depth, skin);

        // The note keeps its whole text for whoever reads it, the rows it's broken into each say a line of it
        foreach (UITreeItem line in item.wrapped!)
        {
            line.Parent = parent;
            rows.Add((line, depth));
        }
    }

    private void BreakNote(UITreeItem item, int depth, UISkin skin)
    {
        float scale = ScaleOf(skin), icon = MathF.Round(LineHeight(skin) * 0.62f);

        // From the row's left to the end of its text, the chevron's room, the icon's if it has one and the scroll bar's
        float left = LEFT_PAD + depth * Indent + icon + ICON_GAP + (item.Icon.Length > 0 ? icon + ICON_GAP : 0.0f);
        float room = MathF.Max(icon * 4.0f, wrapWidth - left - LEFT_PAD - BAR_WIDTH * 2.0f);

        skin.Font.Wrap(item.Text, scale, room, wrapLines, markup: false);
        item.wrapped = [];
        item.wrappedAt = wrapWidth;
        item.wrappedOf = item.Text;
        if (wrapLines.Count <= 1)
        {
            item.wrapped.Add(item);
            return;
        }

        for (int i = 0; i < wrapLines.Count; i++)
        {
            string line = item.Text.Substring(wrapLines[i].Start, wrapLines[i].Length).Trim();
            item.wrapped.Add(new UITreeItem(line, item.Icon.Length > 0 ? (i == 0 ? item.Icon : WRAP_ICON) : string.Empty)
            {
                Color = item.Color,
                IconColor = item.IconColor,
                Tooltip = item.Tooltip,
                Tag = item.Tag,
            });
        }
    }

    // An icon nobody has, so the rows after a note's first leave the room its icon took and line up with its text
    private const string WRAP_ICON = " ";

    /// <summary>Helper method to break the notes again when the tree's width changed since they were.</summary>
    private void RewrapNotes()
    {
        float width = Bounds.Width;
        if (!hasWraps || width <= 0.0f || MathF.Abs(width - wrapWidth) < 0.5f)
            return;

        wrapWidth = width;
        UITreeItem? keep = selected;
        Refresh();
        if (keep is not null && IndexOf(keep) >= 0)
            selected = keep;
    }
}
