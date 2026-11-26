using System.Numerics;

using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

// The tree view's PaintVersion, so an idle tree with hundreds or thousands of rows is copied out of the previous
// tick's draw list instead of re-measuring and repainting every visible row every tick. Only the visible row slice
// is hashed, and any active motion, drag, reveal, inline edit, or visible keyboard focus ring opts out so side
// effects (clamping, revealing, ShowingFocus) always run.
public partial class TreeView
{
    protected override long PaintVersion
    {
        get
        {
            if (paintedSkin is not { } skin)
                return -1;
            if (ease.Goal is not null || ease.Over.Active || revealSelected || revealOnly is not null || dragging || draggingBar || edited is not null)
                return -1;

            bool keyed = IsFocused && TakesKeys && EnabledInHierarchy;
            bool ringed = keyed && Module?.Compositor.FocusVisible == true;
            if (ringed)
                ShowingFocus();

            var hash = new HashCode();

            // Tree-level appearance and interaction state
            hash.Add(Color);
            hash.Add(Radius);
            hash.Add(Indent);
            hash.Add(RowHeight);
            hash.Add(TextScale);
            hash.Add(Guides);
            hash.Add(TakesKeys);
            hash.Add(FocusLine);
            hash.Add(keyed);
            hash.Add(ringed);
            hash.Add(TypeToFind);
            if (findShown.Length > 0 && findMissed)
                hash.Add(FindNothingText);
            hash.Add(findShown);
            hash.Add(findMissed);
            hash.Add(scroll);
            hash.Add(chevronRight);
            hash.Add(chevronDown);
            hash.Add(selected);
            hash.Add(alsoSelected.Count);
            for (int i = 0; i < alsoSelected.Count; i++)
                hash.Add(alsoSelected[i]);
            hash.Add(hovered);
            hash.Add(hoveredAction);
            hash.Add(hoveredHeader);
            hash.Add(SortColumn);
            hash.Add(SortDescending);

            // Indent-guide highlighting depends on the selected item's parent chain
            UITreeItem? litLevel = selected?.Parent;
            int litDepth = -1;
            for (UITreeItem? up = litLevel; up is not null; up = up.Parent)
                litDepth++;
            hash.Add(litLevel);
            hash.Add(litDepth);

            // Scrollbar hover highlight depends on whether the pointer is inside the grab zone
            bool barLit = IsHovered && Module is { } module && !scrollBarTrack.IsEmpty
                && ScrollBarGrabZone.Contains(module.ToLocal(module.Compositor.Pointer.Position));
            hash.Add(barLit);

            // Table columns
            if (Columns is { Count: > 0 } columns)
            {
                hash.Add(columns.Count);
                for (int i = 0; i < columns.Count; i++)
                {
                    hash.Add(columns[i].Title);
                    hash.Add(columns[i].Width);
                }
            }
            else
            {
                hash.Add(0);
            }

            // Total row count governs scroll travel, scrollbar thumb geometry, and bottom fade
            hash.Add(rows.Count);

            float line = LineHeight(skin);
            UIRect bounds = RowsArea(skin);
            int first = Math.Max(0, (int)(scroll / line));
            int last = Math.Min(rows.Count - 1, (int)(scroll / line) + (int)(bounds.Height / line) + 1);
            hash.Add(first);
            hash.Add(last);

            for (int i = first; i <= last; i++)
            {
                var (item, depth) = rows[i];
                hash.Add(depth);
                hash.Add(item.Parent);
                hash.Add(item.HasChildren);
                hash.Add(item.Expanded);
                hash.Add(item.Button);
                hash.Add(item.Pip);
                hash.Add(item.Icon);
                hash.Add(item.IconColor);
                hash.Add(item.wrapLine ?? item.Text);
                hash.Add(item.Color);
                hash.Add(item.Bold);
                hash.Add(item.Mark);
                hash.Add(item.LitColor);
                if (item.Lit is { Count: > 0 } lit)
                {
                    hash.Add(lit.Count);
                    for (int j = 0; j < lit.Count; j++)
                        hash.Add(lit[j]);
                }
                else
                {
                    hash.Add(0);
                }
                hash.Add(item.Edge);
                hash.Add(item.Note);
                hash.Add(item.Detail);
                hash.Add(item.DetailMarkup);
                hash.Add(item.DetailColor);
                hash.Add(item.DetailKeys);
                hash.Add(item.Badge);
                hash.Add(item.BadgeColor);
                hash.Add(item.Dot);
                if (item.StatusDots is { Count: > 0 } dots)
                {
                    hash.Add(dots.Count);
                    for (int j = 0; j < dots.Count; j++)
                        hash.Add(dots[j]);
                }
                else
                {
                    hash.Add(0);
                }
                if (item.Actions is { Count: > 0 } actions)
                {
                    hash.Add(actions.Count);
                    for (int j = 0; j < actions.Count; j++)
                        hash.Add(actions[j].Icon);
                }
                else
                {
                    hash.Add(0);
                }
            }

            return hash.ToHashCode() & 0x7FFFFFFF;
        }
    }
}
