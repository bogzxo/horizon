using System.Numerics;


namespace Horizon.UI.Components;

/// <summary>Which way a split divides its room, side by side or one over the other.</summary>
public enum SplitOrientation
{
    /// <summary>The two halves next to each other, left and right.</summary>
    Horizontal,

    /// <summary>The two halves one over the other, top and bottom.</summary>
    Vertical
}

/// <summary>Which way to look for a neighbour, see <see cref="SplitTree{T}.Neighbour"/>.</summary>
public enum SplitDirection { Left, Right, Up, Down }

/// <summary>
/// A node of a <see cref="SplitTree{T}"/>, either a leaf holding one of whatever the tree holds, or a split with two
/// children and a <see cref="Ratio"/> saying how much of its room the first one takes. The ratio is what a drag on the
/// edge between the two changes, and what a tween moves to slide a new split open.
/// </summary>
public sealed class SplitNode<T> where T : class
{
    /// <summary>What is in it, null for a split.</summary>
    public T? Content { get; internal set; }

    /// <summary>The split above it, null for the root.</summary>
    public SplitNode<T>? Parent { get; internal set; }

    /// <summary>Which way it is split, meaningless for a leaf.</summary>
    public SplitOrientation Orientation { get; internal set; }

    /// <summary>The two halves of a split, the first on the left or the top, null on a leaf.</summary>
    public SplitNode<T>? First { get; internal set; }

    public SplitNode<T>? Second { get; internal set; }

    /// <summary>How much of the room the first half takes, 0 to 1. Half unless somebody dragged the edge.</summary>
    public float Ratio { get; set; } = 0.5f;

    public bool IsLeaf => Content is not null;

    /// <summary>Whether a tween is moving its ratio (a split sliding open or shut), the halves then go under the least a pane is given.</summary>
    public bool Animating { get; set; }

    /// <summary>Where it was last laid out, see <see cref="SplitTree{T}.Layout"/>.</summary>
    public UIRect Bounds { get; internal set; }

    internal SplitNode(T content) => Content = content;

    internal SplitNode(SplitOrientation orientation, SplitNode<T> first, SplitNode<T> second)
    {
        Orientation = orientation;
        First = first;
        Second = second;
        first.Parent = this;
        second.Parent = this;
    }

    /// <summary>Every leaf under it, left to right and top to bottom.</summary>
    public IEnumerable<SplitNode<T>> Leaves()
    {
        if (IsLeaf)
        {
            yield return this;
            yield break;
        }

        foreach (var leaf in First!.Leaves()) yield return leaf;
        foreach (var leaf in Second!.Leaves()) yield return leaf;
    }
}

/// <summary>
/// A tree of splits, the way a terminal's tab is divided into panes (Micro's splits, Windows Terminal's panes, vim's
/// windows). A leaf holds one thing, a split holds two nodes side by side or one over the other with a ratio
/// between them, and splitting a leaf puts a new split where the leaf was with the leaf and the newcomer in it.
/// Closing a leaf has its sibling take the split's place. <see cref="Layout"/> hands every leaf its rectangle out of
/// one for the whole, with a gap between neighbours, and <see cref="Neighbour"/> finds what is to the left, right, over
/// or under a leaf by where it was laid out, which is what the keys that move the focus want. Nothing in here
/// draws, it is tested with strings in it.
/// </summary>
public sealed class SplitTree<T> where T : class
{
    /// <summary>The top of the tree, null while it is empty.</summary>
    public SplitNode<T>? Root { get; private set; }

    public bool IsEmpty => Root is null;

    /// <summary>How many leaves there are, kept as they come and go, it is read in every paint of a panel.</summary>
    public int Count { get; private set; }

    /// <summary>Every leaf, left to right and top to bottom.</summary>
    public IEnumerable<SplitNode<T>> Leaves => Root?.Leaves() ?? [];

    /// <summary>The leaf holding something, null when nothing holds it.</summary>
    public SplitNode<T>? Find(T content)
    {
        foreach (var leaf in Leaves)
        {
            if (ReferenceEquals(leaf.Content, content))
                return leaf;
        }

        return null;
    }

    /// <summary>Puts the first thing in, the tree being empty. The only way in while there is nothing to split.</summary>
    public SplitNode<T> Add(T content)
    {
        if (Root is not null)
            throw new InvalidOperationException("The tree already has something in it, split a leaf of it instead.");

        Count = 1;
        return Root = new SplitNode<T>(content);
    }

    /// <summary>
    /// Divides a leaf, the newcomer going on the right or under it (or the left and over it with <paramref name="before"/>),
    /// and says which leaf the newcomer is in. The new split starts with the room shared evenly, or with the share
    /// given to the newcomer (0 for a split that is to slide open).
    /// </summary>
    public SplitNode<T> Split(SplitNode<T> leaf, SplitOrientation orientation, T content, bool before = false, float share = 0.5f)
    {
        if (!leaf.IsLeaf)
            throw new ArgumentException("Only a leaf can be split.", nameof(leaf));

        var parent = leaf.Parent;
        var added = new SplitNode<T>(content);
        var split = before ? new SplitNode<T>(orientation, added, leaf) : new SplitNode<T>(orientation, leaf, added);
        split.Ratio = Math.Clamp(before ? share : 1.0f - share, 0.0f, 1.0f);
        split.Bounds = leaf.Bounds;
        Replace(parent, leaf, split);
        split.Parent = parent;
        Count++;
        return added;
    }

    /// <summary>Takes a leaf out, its sibling taking the whole of their split. False if it isn't in the tree.</summary>
    public bool Remove(SplitNode<T> leaf)
    {
        if (!leaf.IsLeaf || Find(leaf.Content!) != leaf)
            return false;

        var split = leaf.Parent;
        Count--;
        if (split is null)
        {
            Root = null;
            return true;
        }

        var sibling = ReferenceEquals(split.First, leaf) ? split.Second! : split.First!;
        Replace(split.Parent, split, sibling);
        sibling.Parent = split.Parent;
        leaf.Parent = null;
        return true;
    }

    private void Replace(SplitNode<T>? parent, SplitNode<T> old, SplitNode<T> with)
    {
        if (parent is null)
            Root = with;
        else if (ReferenceEquals(parent.First, old))
            parent.First = with;
        else
            parent.Second = with;
        with.Parent = parent;
    }

    /// <summary>
    /// Hands every node its rectangle out of one for the whole tree, a gap wide between neighbours (where the edge
    /// is dragged). A half never gets less than <paramref name="least"/> on the axis of its split while there is room for two of that.
    /// </summary>
    public void Layout(UIRect whole, float gap = 4.0f, float least = 40.0f)
    {
        if (Root is not null)
            Layout(Root, whole, gap, least);
    }

    private static void Layout(SplitNode<T> node, UIRect area, float gap, float least)
    {
        node.Bounds = area;
        if (node.IsLeaf)
            return;

        bool across = node.Orientation == SplitOrientation.Horizontal;
        float length = (across ? area.Width : area.Height) - gap;
        float first = MathF.Round(length * node.Ratio);
        if (length >= least * 2.0f && !node.Animating)
            first = Math.Clamp(first, least, length - least);
        else
            first = Math.Clamp(first, 0.0f, MathF.Max(0.0f, length));

        UIRect a, b;
        if (across)
        {
            a = new UIRect(area.Min, new Vector2(area.Min.X + first, area.Max.Y));
            b = new UIRect(new Vector2(area.Min.X + first + gap, area.Min.Y), area.Max);
        }
        else
        {
            // Y goes up, the first half is the top one
            a = new UIRect(new Vector2(area.Min.X, area.Max.Y - first), area.Max);
            b = new UIRect(area.Min, new Vector2(area.Max.X, area.Max.Y - first - gap));
        }

        Layout(node.First!, a, gap, least);
        Layout(node.Second!, b, gap, least);
    }

    /// <summary>
    /// The nearest split above a leaf whose edge is on the given side of it, with which half the leaf is in. The
    /// split a drag of that edge of the leaf changes the ratio of, null when the leaf is against the outside there.
    /// </summary>
    public (SplitNode<T> Split, bool InFirst)? EdgeOwner(SplitNode<T> leaf, SplitDirection side)
    {
        bool across = side is SplitDirection.Left or SplitDirection.Right;
        bool wantFirst = side is SplitDirection.Right or SplitDirection.Down;
        for (SplitNode<T>? node = leaf; node?.Parent is { } parent; node = parent)
        {
            bool inFirst = ReferenceEquals(parent.First, node);
            if ((parent.Orientation == SplitOrientation.Horizontal) == across && inFirst == wantFirst)
                return (parent, inFirst);
        }

        return null;
    }

    /// <summary>
    /// The leaf next to one in a direction, by where they were laid out, the one that overlaps it most along the
    /// edge. Null at the outside.
    /// </summary>
    public SplitNode<T>? Neighbour(SplitNode<T> leaf, SplitDirection direction)
    {
        UIRect from = leaf.Bounds;
        SplitNode<T>? best = null;
        float bestOverlap = -1.0f, bestDistance = float.MaxValue;
        foreach (var other in Leaves)
        {
            if (other == leaf)
                continue;

            UIRect to = other.Bounds;
            float distance, overlap;
            switch (direction)
            {
                case SplitDirection.Left:
                    distance = from.Min.X - to.Max.X;
                    overlap = Overlap(from.Min.Y, from.Max.Y, to.Min.Y, to.Max.Y);
                    break;
                case SplitDirection.Right:
                    distance = to.Min.X - from.Max.X;
                    overlap = Overlap(from.Min.Y, from.Max.Y, to.Min.Y, to.Max.Y);
                    break;
                case SplitDirection.Up:
                    distance = to.Min.Y - from.Max.Y;
                    overlap = Overlap(from.Min.X, from.Max.X, to.Min.X, to.Max.X);
                    break;
                default:
                    distance = from.Min.Y - to.Max.Y;
                    overlap = Overlap(from.Min.X, from.Max.X, to.Min.X, to.Max.X);
                    break;
            }

            if (distance < -0.5f || overlap <= 0.0f)
                continue;

            // The nearest first, then the one with the most in common along the edge
            if (distance < bestDistance - 0.5f || (MathF.Abs(distance - bestDistance) <= 0.5f && overlap > bestOverlap))
            {
                best = other;
                bestDistance = distance;
                bestOverlap = overlap;
            }
        }

        return best;
    }

    private static float Overlap(float a0, float a1, float b0, float b1) => MathF.Min(a1, b1) - MathF.Max(a0, b0);

    /// <summary>
    /// The tree as text, for a test or a log, <c>[a|b]</c> for two side by side and <c>[a/b]</c> for one over the
    /// other, leaves by what <paramref name="name"/> makes of them.
    /// </summary>
    /// <summary>Two leaves' contents swapped, each pane taking the other's place and room.</summary>
    public void Swap(SplitNode<T> a, SplitNode<T> b)
    {
        if (!a.IsLeaf || !b.IsLeaf || ReferenceEquals(a, b))
            return;
        (a.Content, b.Content) = (b.Content, a.Content);
    }

    public string Describe(Func<T, string> name) => Root is null ? string.Empty : Describe(Root, name);

    private static string Describe(SplitNode<T> node, Func<T, string> name) =>
        node.IsLeaf ? name(node.Content!) : $"[{Describe(node.First!, name)}{(node.Orientation == SplitOrientation.Horizontal ? "|" : "/")}{Describe(node.Second!, name)}]";
}
