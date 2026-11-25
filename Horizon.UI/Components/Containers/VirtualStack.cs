using System.Numerics;

using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// A scrolled list of any number of rows that makes components only for the rows in sight, and one sight's worth either
/// side of them, so a chat of thousands of rows costs what the few dozen on screen do. The rows come from whoever owns
/// the list, by number: <see cref="ItemCount"/> of them, <see cref="Make"/> making the component of one, and
/// <see cref="Recycle"/> told of one that went out of sight. A row's height is what it measured the last time it was
/// made, at about this width, and <see cref="EstimateHeight"/> or <see cref="Estimate"/> until then, so the bar is right
/// enough before every row has been seen. Heights put right above what's in sight move the offset with them, the row at
/// the top stays where it is on screen. Components can be used again for a row of their kind (<see cref="KindOf"/> and
/// <see cref="Bind"/>), rather than made anew every time a row comes into sight.
/// </summary>
public class VirtualStack : ScrollPanel
{
    /// <summary>How many rows there are.</summary>
    public int ItemCount { get; set; }

    /// <summary>What a row's measured height is kept by, the row's number unless it says (a row's id, so heights survive rows put in above).</summary>
    public Func<int, long>? KeyOf { get; set; }

    /// <summary>A row's height before it has been seen, from how wide the list is, <see cref="Estimate"/> when it doesn't say.</summary>
    public Func<int, float, float>? EstimateHeight { get; set; }

    /// <summary>The height a row is taken to be when nothing says better.</summary>
    public float Estimate { get; set; } = 32.0f;

    /// <summary>The component of a row, made as it comes into sight.</summary>
    public Func<int, UIComponent>? Make { get; set; }

    /// <summary>A component taken out of the list as its row went out of sight, with the row it was.</summary>
    public Action<int, UIComponent>? Recycle { get; set; }

    /// <summary>
    /// The kind of a row, components of one kind kept as they go out of sight and given to another row of that kind
    /// through <see cref="Bind"/>. Null keeps none, every row's component is made by <see cref="Make"/>.
    /// </summary>
    public Func<int, int>? KindOf { get; set; }

    /// <summary>A component kept from a row gone out of sight made into another row of its kind.</summary>
    public Action<int, UIComponent>? Bind { get; set; }

    /// <summary>The most components of each kind kept for using again.</summary>
    public int MostKept { get; set; } = 16;

    /// <summary>The most measured heights kept, by their keys.</summary>
    public int MostHeights { get; set; } = 20_000;

    /// <summary>The gap between two rows.</summary>
    public float Spacing { get; set; }

    // Heights are kept for widths within this many pixels of each other, a row measured at a width far from the list's
    // now is measured again as it comes into sight
    private const float WIDTH_BUCKET = 8.0f;

    // The rows that have components, by number, and the components kept for using again, by kind
    private readonly Dictionary<int, UIComponent> realized = [];
    private readonly Dictionary<int, Stack<UIComponent>> kept = [];
    private readonly List<int> going = [];

    // The components of the rows coming into sight and going out of it in one layout, put in and taken out of the list's
    // children all at once, kept between layouts so a tick allocates nothing for them
    private readonly List<UIComponent> arriving = [], leaving = [];

    // Each row's measured height by its key and the width it was measured at, and where each row's top is
    private readonly Dictionary<long, (int Bucket, float Height)> heights = [];
    private float[] tops = [];
    private int laidCount = -1, laidBucket = int.MinValue;
    private bool dirty = true;
    private UISkin? skin;

    /// <summary>The rows that have components now, first and last, (0, -1) for none.</summary>
    public (int First, int Last) Realized { get; private set; } = (0, -1);

    /// <summary>The component a row has while it's in sight, null for one that isn't.</summary>
    public UIComponent? ComponentOf(int index) => realized.GetValueOrDefault(index);

    /// <summary>How far down a row's top is, as the list is laid out now.</summary>
    public float TopOf(int index)
    {
        EnsureTops(CurrentWidth);
        return index <= 0 || laidCount <= 0 ? 0.0f : index >= ItemCount ? tops[laidCount] : tops[index];
    }

    /// <summary>The whole height of every row, measured or estimated.</summary>
    public float ContentHeight
    {
        get
        {
            EnsureTops(CurrentWidth);
            return laidCount <= 0 ? 0.0f : tops[laidCount];
        }
    }

    /// <summary>
    /// A row's height to be taken again, it grew or changed (the last row streaming). A row in sight is measured anyway, one
    /// out of sight goes back to its estimate.
    /// </summary>
    public void ItemGrew(int index)
    {
        if (!realized.ContainsKey(index))
            heights.Remove(Key(index));
        dirty = true;
    }

    /// <summary>The rows changed in number, the ones there were left as they were (rows put on at the end).</summary>
    public void ItemsChanged() => dirty = true;

    /// <summary>A row's component asked for again as it's laid out next, the row having another now.</summary>
    public void Refresh(int index)
    {
        if (realized.Remove(index, out UIComponent? component))
            Release(index, component);
        dirty = true;
    }

    /// <summary>
    /// Every row's component asked for again, the heights kept by their keys, for rows put in or taken out above or among
    /// the ones in sight, which moves what number each row is.
    /// </summary>
    public void Rebind()
    {
        TakeOutAll();
        foreach (var (index, component) in realized)
            Recycle?.Invoke(index, component);
        realized.Clear();
        Realized = (0, -1);
        dirty = true;
    }

    /// <summary>Every row made again from the start, the heights forgotten, for a list of other rows altogether.</summary>
    public void Reset()
    {
        ReleaseAll();
        heights.Clear();
        Realized = (0, -1);
        dirty = true;
    }

    /// <summary>Scrolls a row's top to the top of what's in sight, or as near as the end lets it.</summary>
    public void ScrollTo(int index)
    {
        EnsureTops(CurrentWidth);
        // How far it can go as the rows are now, a list not yet laid out knows nothing else
        contentHeight = laidCount <= 0 ? 0.0f : tops[laidCount];
        Offset = TopOf(Math.Clamp(index, 0, Math.Max(0, ItemCount - 1)));
    }

    private long Key(int index) => KeyOf?.Invoke(index) ?? index;

    private float CurrentWidth => MathF.Max(0.0f, Bounds.Shrink(Padding).Width - BarWidth - BarGap);

    private static int BucketOf(float width) => (int)MathF.Floor(width / WIDTH_BUCKET);

    // A row's height as best known at a width, measured near it or estimated
    private float HeightOf(int index, float width)
    {
        // Measured at another width it's taken as that until it's seen again, nearer than any estimate
        if (heights.TryGetValue(Key(index), out var known))
            return known.Height;
        return MathF.Max(0.0f, EstimateHeight?.Invoke(index, width) ?? Estimate);
    }

    // Where every row's top is, worked out again only when the rows, their heights or the width moved
    private void EnsureTops(float width)
    {
        int bucket = BucketOf(width);
        if (!dirty && laidCount == ItemCount && laidBucket == bucket)
            return;

        // Grow geometrically rather than allocating a fresh array on every single appended row in streaming logs/chats.
        if (tops.Length < ItemCount + 1)
            tops = new float[Math.Max(ItemCount + 1, tops.Length * 2)];
        // A gap goes between two rows that take room, one of no height (a hidden one) has none either side of it, the way
        // a stack leaves out a child that isn't visible
        float at = 0.0f;
        bool any = false;
        for (int i = 0; i < ItemCount; i++)
        {
            float height = HeightOf(i, width);
            if (height > 0.0f && any)
                at += Spacing;
            tops[i] = at;
            at += height;
            any |= height > 0.0f;
        }

        tops[ItemCount] = at;
        laidCount = ItemCount;
        laidBucket = bucket;
        dirty = false;
    }

    // The first row whose bottom is below a place, by halves
    private int RowAt(float y)
    {
        int low = 0, high = ItemCount - 1;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (tops[middle + 1] <= y)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    protected override Vector2 Measure(UISkin skin)
    {
        this.skin = skin;
        return base.Measure(skin);
    }

    protected override void Arrange(UIRect content)
    {
        float width = MathF.Max(0.0f, content.Width - BarWidth - BarGap);
        float sight = content.Height;

        // The row at the top of the view and how far into it the view starts, which stays put as heights are put right
        EnsureTops(width);
        bool atEnd = StickToEnd && offset >= laidMax - 0.5f;
        int anchor = ItemCount > 0 ? RowAt(offset) : 0;
        float into = ItemCount > 0 ? offset - tops[anchor] : 0.0f;

        // At its end it's the end of the rows as they are now that's in sight, the rows made for it are the last ones
        if (atEnd)
            offset = MathF.Max(0.0f, tops[ItemCount] - sight);

        // Twice at the most, the rows made the first time can measure other than they were estimated
        float anchorTop = ItemCount > 0 ? tops[anchor] : 0.0f;
        for (int pass = 0; pass < 2; pass++)
        {
            Realize(width, sight);
            if (!Remeasure(width))
                break;
            EnsureTops(width);
            if (atEnd)
                offset = MathF.Max(0.0f, tops[ItemCount] - sight);
            else if (ItemCount > 0)
                offset = tops[Math.Min(anchor, ItemCount - 1)] + into;
        }

        // Where the wheel is easing to moves with the rows above it as well. Left behind, scrolling up through rows not yet
        // measured (a chat just opened) pulled the list to the old place, which looked like it snapping up
        if (!atEnd && ItemCount > 0 && ease.Goal is { } goal)
            ease.Goal = goal + tops[Math.Min(anchor, ItemCount - 1)] - anchorTop;

        contentHeight = ItemCount <= 0 ? 0.0f : tops[ItemCount];
        float max = MathF.Max(0.0f, contentHeight - sight);
        float before = offset;
        offset = atEnd ? max : Math.Clamp(offset, 0.0f, max);
        ease.Clamp(max);
        if (UIWheel.Tracing && (MathF.Abs(before - offset) > 0.5f || MathF.Abs(laidMax - max) > 0.5f))
            UIWheel.Trace(FormattableString.Invariant($"virtual arrange offset {before:F1} -> {offset:F1} atEnd={atEnd} stick={StickToEnd} max {laidMax:F1} -> {max:F1} items={ItemCount} stretch={ease.Over.Stretch:F2}"));
        laidMax = max;

        // Rows in sight at their places, from the top less the offset
        foreach (var (index, component) in realized)
        {
            float height = HeightOf(index, width);
            float top = content.Max.Y + offset - tops[index];
            component.ArrangeTree(new UIRect(new Vector2(content.Min.X, top - height), new Vector2(content.Min.X + width, top)));
        }
    }

    // The rows from a sight above to a sight below given components, the rest let go of
    private void Realize(float width, float sight)
    {
        if (ItemCount == 0 || Make is null)
        {
            // Nothing to let go of is the usual, an empty list laid out every tick allocated a copy of none each time
            if (realized.Count > 0)
                ReleaseAll();
            Realized = (0, -1);
            return;
        }

        float from = MathF.Max(0.0f, offset - sight), to = offset + sight * 2.0f;
        int first = RowAt(from), last = RowAt(to);

        // On idle ticks where the realized window is unchanged and complete, skip dictionary scans and child-list updates.
        if (Realized == (first, last) && realized.Count == last - first + 1 && last < ItemCount)
            return;

        // Gathered first and let go of after, into a list kept between layouts so a tick allocates nothing
        going.Clear();
        foreach (var (index, _) in realized)
        {
            if (index < first || index > last || index >= ItemCount)
                going.Add(index);
        }
        // Taken out of the list together, then each let go of
        if (going.Count > 0)
        {
            leaving.Clear();
            foreach (int index in going)
                leaving.Add(realized[index]);
            RemoveRange(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(leaving));
            leaving.Clear();
            foreach (int index in going)
            {
                realized.Remove(index, out UIComponent? component);
                Let(index, component!);
            }
        }

        // Made in order and put in the list together, then measured
        arriving.Clear();
        for (int i = first; i <= last; i++)
        {
            if (realized.ContainsKey(i))
                continue;

            UIComponent made = Take(i) ?? Make(i);
            realized[i] = made;
            arriving.Add(made);
        }

        if (arriving.Count > 0)
        {
            AddRange(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(arriving));
            if (skin is not null)
            {
                foreach (UIComponent made in arriving)
                    made.MeasureTree(skin);
            }
            arriving.Clear();
        }

        Realized = (first, last);
    }

    // The heights of the rows in sight as they measured, true when one isn't what the layout took it to be
    private bool Remeasure(float width)
    {
        int bucket = BucketOf(width);
        bool moved = false;
        foreach (var (index, component) in realized)
        {
            float height = component.Visible ? MathF.Max(1.0f, component.DesiredSize.Y) : 0.0f;
            long key = Key(index);
            if (heights.TryGetValue(key, out var known) && known.Bucket == bucket && MathF.Abs(known.Height - height) < 0.5f)
                continue;
            float was = HeightOf(index, width);
            // Kept for every row ever seen, rows gone for good among them, so past the most they start again from estimates
            if (heights.Count >= MostHeights && !heights.ContainsKey(key))
                heights.Clear();
            heights[key] = (bucket, height);
            if (MathF.Abs(was - height) >= 0.5f)
                moved = true;
        }

        if (moved)
            dirty = true;
        return moved;
    }

    // A component kept from a row of this kind, made into this row
    private UIComponent? Take(int index)
    {
        if (KindOf is null || Bind is null || !kept.TryGetValue(KindOf(index), out var pool) || pool.Count == 0)
            return null;
        UIComponent component = pool.Pop();
        Bind(index, component);
        return component;
    }

    // Every row's component taken out of the list at once, still known by its row
    private void TakeOutAll()
    {
        leaving.Clear();
        foreach (var (_, component) in realized)
            leaving.Add(component);
        RemoveRange(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(leaving));
        leaving.Clear();
    }

    // Every row's component out of the list at once, each let go of as Release does
    private void ReleaseAll()
    {
        TakeOutAll();
        foreach (var (index, component) in realized)
            Let(index, component);
        realized.Clear();
    }

    // A component out of the list, kept for its kind when there's room
    private void Release(int index, UIComponent component)
    {
        Remove(component);
        Let(index, component);
    }

    // A component already out of the list told of and kept for its kind when there's room
    private void Let(int index, UIComponent component)
    {
        Recycle?.Invoke(index, component);
        if (KindOf is null || Bind is null || index >= ItemCount)
            return;
        int kind = KindOf(index);
        if (!kept.TryGetValue(kind, out var pool))
            kept[kind] = pool = new Stack<UIComponent>();
        if (pool.Count < MostKept)
            pool.Push(component);
    }
}
