using System.Numerics;
using System.Runtime.InteropServices;

using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

// Viewport-windowed stack for the settings page's rows, headings and section panels. A full settings page has
// ~150 rows (~1,500 descendants), and measuring, arranging and updating all of them every tick dominated layout cost
// whenever the settings window was open or dragged. Each visible item's height is measured once (and refreshed on
// build, filter, skin change, or when a realized row's height changes), while only the items overlapping the scroll
// viewport plus a buffer (and any focused, flashing or popup-owning item) stay attached as live children.
public partial class SettingsPage
{
    private sealed class RowsStack : StackPanel
    {
        private const int VIRTUALIZE_THRESHOLD = 24;
        private const float BUFFER_FACTOR = 1.0f;
        private const float MIN_BUFFER = 160.0f;
        private const int NEIGHBOR_MARGIN = 3;

        private readonly ScrollPanel scroll;
        private readonly List<UIComponent> items = [];
        private readonly Dictionary<UIComponent, int> itemIndex = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<UIComponent> attached = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<UIComponent> wantedSet = new(ReferenceEqualityComparer.Instance);
        private readonly List<UIComponent> desired = [];
        private readonly List<UIComponent> scratchAdd = [];
        private readonly List<UIComponent> scratchRemove = [];

        private float[] itemTops = [];
        private float[] itemHeights = [];
        private bool[] itemVisible = [];
        private float totalHeight, maxWidth;
        private float gap;
        private UISkin? measuredSkin;
        private bool heightsDirty = true;

        public RowsStack(ScrollPanel scroll)
        {
            this.scroll = scroll;
        }

        public IReadOnlyList<UIComponent> Items => items;

        public void SetItems(List<UIComponent> next)
        {
            DetachAll();
            items.Clear();
            itemIndex.Clear();
            for (int i = 0; i < next.Count; i++)
            {
                UIComponent item = next[i];
                items.Add(item);
                itemIndex[item] = i;
            }

            heightsDirty = true;

            // Keep small pages and unlaid-out pages (headless tests) fully attached immediately.
            if (items.Count <= VIRTUALIZE_THRESHOLD || scroll.Bounds.IsEmpty)
            {
                AttachAll();
            }
        }

        public void Reorder(List<UIComponent> ordered)
        {
            items.Clear();
            itemIndex.Clear();
            for (int i = 0; i < ordered.Count; i++)
            {
                UIComponent item = ordered[i];
                items.Add(item);
                itemIndex[item] = i;
            }

            heightsDirty = true;
            if (items.Count <= VIRTUALIZE_THRESHOLD || scroll.Bounds.IsEmpty)
            {
                SyncChildrenOrder(items);
            }
        }

        public void InvalidateHeights() => heightsDirty = true;

        public bool IsAttached(UIComponent item) => attached.Contains(item);

        public bool HasMeasured(UIComponent item) =>
            !heightsDirty && itemIndex.TryGetValue(item, out int index) && index < itemVisible.Length && itemVisible[index];

        public bool TryGetTop(UIComponent item, out float top)
        {
            if (!heightsDirty && itemIndex.TryGetValue(item, out int index) && index < itemTops.Length && item.Visible)
            {
                top = itemTops[index];
                return true;
            }

            top = 0.0f;
            return false;
        }

        public bool TryGetSpan(UIComponent item, out float top, out float height)
        {
            if (!heightsDirty && itemIndex.TryGetValue(item, out int index) && index < itemTops.Length && item.Visible)
            {
                top = itemTops[index];
                height = itemHeights[index];
                return true;
            }

            top = height = 0.0f;
            return false;
        }

        // Ensures an offscreen row or panel is attached before giving it keyboard focus so its Module is valid.
        public void EnsureAttached(UIComponent item)
        {
            if (!itemIndex.ContainsKey(item) || attached.Contains(item))
                return;

            Add(item);
            attached.Add(item);
        }

        private void DetachAll()
        {
            if (Children.Count == 0)
            {
                attached.Clear();
                return;
            }

            scratchRemove.Clear();
            foreach (UIComponent child in ChildSpan)
                scratchRemove.Add(child);
            RemoveRange(CollectionsMarshal.AsSpan(scratchRemove));
            scratchRemove.Clear();
            attached.Clear();
        }

        private void AttachAll()
        {
            if (items.Count == 0)
                return;

            AddRange(CollectionsMarshal.AsSpan(items));
            foreach (UIComponent item in items)
                attached.Add(item);
        }

        private void EnsureCapacity(int count)
        {
            if (itemTops.Length >= count)
                return;

            int next = Math.Max(count, itemTops.Length * 2);
            itemTops = new float[next];
            itemHeights = new float[next];
            itemVisible = new bool[next];
        }

        protected override Vector2 Measure(UISkin skin)
        {
            gap = Spacing >= 0.0f ? Spacing : skin.Spacing;
            int count = items.Count;
            if (count == 0)
            {
                totalHeight = 0.0f;
                maxWidth = 0.0f;
                measuredSkin = skin;
                heightsDirty = false;
                return Padding.Total;
            }

            EnsureCapacity(count);

            bool full = heightsDirty || !ReferenceEquals(skin, measuredSkin);
            if (!full)
            {
                for (int i = 0; i < count; i++)
                {
                    if (items[i].Visible != itemVisible[i])
                    {
                        full = true;
                        break;
                    }
                }
            }

            if (full)
            {
                measuredSkin = skin;
                heightsDirty = false;
                float along = 0.0f;
                float across = 0.0f;
                int visibleCount = 0;
                for (int i = 0; i < count; i++)
                {
                    UIComponent item = items[i];
                    bool visible = item.Visible;
                    itemVisible[i] = visible;
                    if (!visible)
                    {
                        itemTops[i] = along;
                        itemHeights[i] = 0.0f;
                        continue;
                    }

                    // Attached children were already measured by UIComponent.MeasureTree before this call.
                    if (!attached.Contains(item))
                        item.MeasureTree(skin);

                    Vector2 size = item.DesiredSize;
                    if (visibleCount > 0)
                        along += gap;
                    itemTops[i] = along;
                    itemHeights[i] = size.Y;
                    along += size.Y;
                    across = MathF.Max(across, size.X);
                    visibleCount++;
                }

                totalHeight = along;
                maxWidth = across;
                return new Vector2(maxWidth, totalHeight) + Padding.Total;
            }

            // Steady state: only the realized children were measured this tick. Recompute tops only if one changed height.
            bool changed = false;
            foreach (UIComponent child in ChildSpan)
            {
                if (!child.Visible || !itemIndex.TryGetValue(child, out int idx))
                    continue;

                Vector2 size = child.DesiredSize;
                if (MathF.Abs(size.Y - itemHeights[idx]) > 0.01f)
                {
                    itemHeights[idx] = size.Y;
                    changed = true;
                }

                if (size.X > maxWidth)
                    maxWidth = size.X;
            }

            if (changed)
                RecomputeTops();

            return new Vector2(maxWidth, totalHeight) + Padding.Total;
        }

        private void RecomputeTops()
        {
            float along = 0.0f;
            int visibleCount = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (!itemVisible[i])
                {
                    itemTops[i] = along;
                    continue;
                }

                if (visibleCount > 0)
                    along += gap;
                itemTops[i] = along;
                along += itemHeights[i];
                visibleCount++;
            }

            totalHeight = along;
        }

        protected override void Arrange(UIRect content)
        {
            if (items.Count == 0)
                return;

            if (heightsDirty || itemTops.Length < items.Count)
            {
                SyncChildrenOrder(items);
                base.Arrange(content);
                return;
            }

            SyncWindow();

            foreach (UIComponent child in ChildSpan)
            {
                if (!child.Visible || !itemIndex.TryGetValue(child, out int idx))
                    continue;

                Vector2 size = child.DesiredSize;
                Vector2 side = child.Anchor.ToVector();
                if (Stretch || (child.Fill & UIFill.Horizontal) != 0)
                {
                    size.X = content.Width;
                    side.X = 0.0f;
                }

                float centerX = content.Center.X + side.X * (content.Width - size.X);
                float top = content.Max.Y - itemTops[idx];
                child.ArrangeTree(UIRect.FromCenter(new Vector2(centerX, top - size.Y * 0.5f), size));
            }
        }

        private void SyncWindow()
        {
            if (items.Count <= VIRTUALIZE_THRESHOLD || scroll.Bounds.IsEmpty)
            {
                SyncChildrenOrder(items);
                return;
            }

            UIRect view = scroll.Bounds.Shrink(scroll.Padding);
            float viewHeight = view.Height;
            float offset = scroll.Offset;
            float buffer = MathF.Max(viewHeight * BUFFER_FACTOR, MIN_BUFFER);
            float minY = offset - Padding.Top - buffer;
            float maxY = offset - Padding.Top + viewHeight + buffer;

            int first = -1, last = -1;
            for (int i = 0; i < items.Count; i++)
            {
                if (!itemVisible[i])
                    continue;

                float top = itemTops[i];
                float bottom = top + itemHeights[i];
                if (bottom >= minY && top <= maxY)
                {
                    if (first < 0)
                        first = i;
                    last = i;
                }
                else if (first >= 0 && top > maxY)
                {
                    break;
                }
            }

            // Expand by a couple of visible neighbors on each side so Tab and ScrollIntoView always have them ready.
            if (first >= 0)
            {
                int added = 0;
                for (int i = first - 1; i >= 0 && added < NEIGHBOR_MARGIN; i--)
                {
                    if (itemVisible[i])
                    {
                        first = i;
                        added++;
                    }
                }

                added = 0;
                for (int i = last + 1; i < items.Count && added < NEIGHBOR_MARGIN; i++)
                {
                    if (itemVisible[i])
                    {
                        last = i;
                        added++;
                    }
                }
            }

            UIComponent? focusedItem = OwningItem(Module?.Compositor.Focus);
            UIComponent? popupItem = OwningItem(Module?.Popup);

            desired.Clear();
            for (int i = 0; i < items.Count; i++)
            {
                UIComponent item = items[i];
                bool inRange = first >= 0 && i >= first && i <= last && itemVisible[i];
                bool pinned = ReferenceEquals(item, focusedItem) || ReferenceEquals(item, popupItem) || (item is SettingRow { Flashing: true });
                if (inRange || pinned)
                    desired.Add(item);
            }

            SyncChildrenOrder(desired);
        }

        private UIComponent? OwningItem(UIComponent? target)
        {
            for (UIComponent? at = target; at is not null; at = at.Parent)
            {
                if (at.Parent == this)
                    return at;
            }

            return null;
        }

        private void SyncChildrenOrder(List<UIComponent> wanted)
        {
            ReadOnlySpan<UIComponent> current = ChildSpan;
            if (current.Length == wanted.Count)
            {
                bool same = true;
                for (int i = 0; i < current.Length; i++)
                {
                    if (!ReferenceEquals(current[i], wanted[i]))
                    {
                        same = false;
                        break;
                    }
                }

                if (same)
                    return;
            }

            // Diff against currently attached children so staying items are never detached or have OnParentChanged fired.
            // Only runs when scrolling moves items in or out of the viewport buffer (never during idle or window drag).
            wantedSet.Clear();
            for (int i = 0; i < wanted.Count; i++)
                wantedSet.Add(wanted[i]);

            scratchRemove.Clear();
            foreach (UIComponent child in current)
            {
                if (!wantedSet.Contains(child))
                    scratchRemove.Add(child);
            }

            if (scratchRemove.Count > 0)
            {
                RemoveRange(CollectionsMarshal.AsSpan(scratchRemove));
                for (int i = 0; i < scratchRemove.Count; i++)
                    attached.Remove(scratchRemove[i]);
                scratchRemove.Clear();
            }

            scratchAdd.Clear();
            UISkin? skin = measuredSkin;
            for (int i = 0; i < wanted.Count; i++)
            {
                UIComponent item = wanted[i];
                if (!attached.Contains(item))
                {
                    if (skin is not null && item.Visible)
                        item.MeasureTree(skin);
                    scratchAdd.Add(item);
                }
            }

            if (scratchAdd.Count > 0)
            {
                AddRange(CollectionsMarshal.AsSpan(scratchAdd));
                for (int i = 0; i < scratchAdd.Count; i++)
                    attached.Add(scratchAdd[i]);
                scratchAdd.Clear();
            }

            // Ensure ascending item order (only moves when scrolling up prepends newly entered items).
            for (int i = 0; i < wanted.Count; i++)
            {
                if (i >= ChildSpan.Length || !ReferenceEquals(ChildSpan[i], wanted[i]))
                    MoveChild(wanted[i], i);
            }
        }
    }
}
