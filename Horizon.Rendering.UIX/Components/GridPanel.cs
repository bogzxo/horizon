using System.Numerics;

using Horizon.Rendering.UIX.Skinning;

namespace Horizon.Rendering.UIX.Components;

/// <summary>
/// A panel that lines its children up in rows of so many columns, left to right and then top to bottom.
/// Every cell is as big as the biggest child, so the rows and the columns come out straight whatever is in
/// them; a child smaller than its cell sits in it by its <see cref="UIComponent.Anchor"/>.
/// </summary>
public class GridPanel : Panel
{
    private int columns = 2;

    // What the last measure settled on, for arrange to use.
    private Vector2 cell;
    private float gap;

    /// <summary>How many children go next to each other before a new row starts.</summary>
    public int Columns
    {
        get => columns;
        set => columns = Math.Max(1, value);
    }

    /// <summary>The gap between two cells, both ways. A negative number uses the skin's.</summary>
    public float Spacing { get; set; } = -1.0f;

    protected override Vector2 Measure(UISkin skin)
    {
        gap = Spacing >= 0.0f ? Spacing : skin.Spacing;
        cell = Vector2.Zero;

        int count = 0;
        foreach (var child in ChildSpan)
        {
            if (!child.Visible)
                continue;

            cell = Vector2.Max(cell, child.DesiredSize);
            count++;
        }

        if (count == 0)
            return Padding.Total;

        int across = Math.Min(columns, count);
        int down = (count + columns - 1) / columns;

        return new Vector2(
            across * cell.X + (across - 1) * gap,
            down * cell.Y + (down - 1) * gap) + Padding.Total;
    }

    protected override void Arrange(UIRect content)
    {
        int index = 0;

        foreach (var child in ChildSpan)
        {
            // A hidden child doesn't take up a cell, the ones after it move up.
            if (!child.Visible)
                continue;

            int column = index % columns;
            int row = index / columns;
            index++;

            // Cells start in the top left corner of the grid, the world being Y-up.
            Vector2 topLeft = new(
                content.Min.X + column * (cell.X + gap),
                content.Max.Y - row * (cell.Y + gap));
            UIRect area = new(new Vector2(topLeft.X, topLeft.Y - cell.Y), new Vector2(topLeft.X + cell.X, topLeft.Y));

            // Slides the child between the edges of its cell, the way a stack does across its line.
            Vector2 size = child.DesiredSize;
            Vector2 side = child.Anchor.ToVector();
            Vector2 center = area.Center + side * (area.Size - size);

            child.ArrangeTree(UIRect.FromCenter(center, size));
        }
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("columns", () => Columns, value => Columns = (int)value);
        Expose("spacing", () => Spacing, value => Spacing = value);
    }
}
