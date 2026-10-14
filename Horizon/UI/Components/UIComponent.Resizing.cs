using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.UI.Drawing;
using Horizon.UI.Scripting;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// The edges of a component that can be dragged to make it bigger or smaller, see <see cref="UIComponent.Resizable"/>.
/// More than one at once is written with a bar between them, <c>UIResize.Right | UIResize.Bottom</c>, and where two
/// of them meet the corner drags both.
/// </summary>
[Flags]
public enum UIResize
{
    None = 0,
    Left = 1,
    Right = 2,
    Top = 4,
    Bottom = 8,

    /// <summary>Both sides.</summary>
    Horizontal = Left | Right,

    /// <summary>The top and the bottom.</summary>
    Vertical = Top | Bottom,

    /// <summary>All four.</summary>
    All = Horizontal | Vertical
}

// The half of a component that lets somebody drag its edges, a tab on every edge that says so. Any component can,
// a side bar somebody wants wider, a list somebody wants taller, a window.
public abstract partial class UIComponent
{
    /// <summary>How long the tab on an edge is at most, and how thick, in pixels.</summary>
    private const float TAB_LENGTH = 44.0f, TAB_THICKNESS = 4.0f;

    /// <summary>
    /// Which of the component's edges can be dragged with the pointer to make it bigger or smaller, none of them
    /// unless told otherwise. Each of them gets a small tab in its middle to be found by, which lights up under the
    /// pointer along with the edge. Dragging one sets <see cref="Size"/> on that axis (so the component stops
    /// sizing itself there), between <see cref="MinSize"/> and <see cref="MaxSize"/>, and calls
    /// <see cref="OnResized"/>.
    /// <code>
    /// var sidebar = module.AddComponent(new StackPanel { Anchor = Origin.Left, Fill = UIFill.Vertical, Size = new Vector2(280, 0) });
    /// sidebar.Resizable = UIResize.Right;
    /// sidebar.MinSize = new Vector2(180, 0);
    /// sidebar.OnResized = size => settings.SidebarWidth = size.X;
    /// </code>
    /// The size changes by as far as the pointer moves. That keeps the edge under the pointer for anything that is
    /// held by its far side (a side bar against the edge of the screen, a child of a stack), something held by
    /// its middle grows both ways and its edge follows at half the pace. In a layout file it is
    /// <c>resize: "right"</c>, or several with commas, <c>"right, bottom"</c>, or <c>"all"</c>.
    /// </summary>
    public UIResize Resizable { get; set; }

    /// <summary>The smallest it can be dragged to. An axis left at zero stops at a few pixels.</summary>
    public Vector2 MinSize { get; set; }

    /// <summary>The biggest it can be dragged to. An axis left at zero has no limit.</summary>
    public Vector2 MaxSize { get; set; }

    /// <summary>How far in from an edge the pointer still has hold of it, in pixels.</summary>
    public float GripSize { get; set; } = 7.0f;

    /// <summary>
    /// How far outside of an edge the pointer has hold of it already. For something with a thing of its own right
    /// against the edge (a scroll bar), which wants most of the hold on the outside, in the gap to its neighbour.
    /// </summary>
    public float GripReach { get; set; } = 4.0f;

    /// <summary>Called with the new size whenever dragging an edge changed it.</summary>
    public Action<Vector2>? OnResized { get; set; }

    /// <summary>Called once when the edge that was being dragged is let go of, with the size it was left at. Where to save it.</summary>
    public Action<Vector2>? OnResizeEnded { get; set; }

    /// <summary>The edges the pointer is on or has hold of right now, none while it is elsewhere. For painting, and for tests.</summary>
    public UIResize ActiveGrip { get; internal set; }

    /// <summary>Whether an edge is being dragged right now.</summary>
    public bool IsResizing { get; private set; }

    private IRuntimeValue? resizedHandler;
    private Vector2 resizeFrom, resizeSize;
    private UIResize resizeEdges;

    /// <summary>
    /// Which of the edges that can be dragged a point is on, in the layout's units. The corner where two of them
    /// meet is both.
    /// </summary>
    public UIResize GripAt(Vector2 point) =>
        Resizable == UIResize.None || !EnabledInHierarchy ? UIResize.None : EdgesAt(point, Resizable, GripSize, GripReach);

    /// <summary>
    /// Which of some edges of the component a point is on, whether they can be dragged or not. For an editor that
    /// lets things be resized that never asked to be, see <see cref="BeginResize"/>.
    /// </summary>
    /// <param name="inside">How far in from an edge still counts, in the layout's units.</param>
    /// <param name="outside">How far past it does.</param>
    public UIResize EdgesAt(Vector2 point, UIResize of, float inside, float outside)
    {
        UIRect bounds = Bounds;

        // Never more than a third of the way in, or something small is all edge and has no middle to take hold of
        float across = MathF.Min(inside, bounds.Width / 3.0f), down = MathF.Min(inside, bounds.Height / 3.0f);

        // A little past an edge counts too, a pointer coming from outside finds it sooner. Up is up, the top edge
        // is the one with the bigger y
        bool left = (of & UIResize.Left) != 0 && point.X >= bounds.Min.X - outside && point.X <= bounds.Min.X + across;
        bool right = !left && (of & UIResize.Right) != 0 && point.X <= bounds.Max.X + outside && point.X >= bounds.Max.X - across;
        bool top = (of & UIResize.Top) != 0 && point.Y <= bounds.Max.Y + outside && point.Y >= bounds.Max.Y - down;
        bool bottom = !top && (of & UIResize.Bottom) != 0 && point.Y >= bounds.Min.Y - outside && point.Y <= bounds.Min.Y + down;

        // Only along the edge itself. Past its ends it is somebody else's edge, unless that is a corner of this
        // component's own and the other edge of the corner can be dragged as well
        bool alongside = point.Y >= bounds.Min.Y && point.Y <= bounds.Max.Y;
        bool above = point.X >= bounds.Min.X && point.X <= bounds.Max.X;

        UIResize on = UIResize.None;
        if (left && (alongside || top || bottom)) on |= UIResize.Left;
        if (right && (alongside || top || bottom)) on |= UIResize.Right;
        if (top && (above || left || right)) on |= UIResize.Top;
        if (bottom && (above || left || right)) on |= UIResize.Bottom;

        return on;
    }

    /// <summary>
    /// Takes hold of some edges, from here every <see cref="DragResize"/> is a new size until <see cref="EndResize"/>.
    /// The UI does this by itself for a component that is <see cref="Resizable"/> when the pointer goes down on an
    /// edge. It is here for whoever routes the pointer themselves (an editor resizing what it edits).
    /// </summary>
    public void BeginResize(UIResize edges, Vector2 point)
    {
        IsResizing = true;
        resizeEdges = ActiveGrip = edges;
        resizeFrom = point;
        resizeSize = Bounds.Size;
    }

    /// <summary>The pointer moved with an edge in hand.</summary>
    /// <param name="snap">Sizes land on a multiple of this, 0 for wherever the pointer is.</param>
    /// <returns>Whether that changed the size.</returns>
    public bool DragResize(Vector2 point, float snap = 0.0f)
    {
        if (!IsResizing)
            return false;

        Vector2 moved = point - resizeFrom;
        Vector2 size = Size;

        if ((resizeEdges & UIResize.Horizontal) != 0)
        {
            float wide = resizeSize.X + ((resizeEdges & UIResize.Right) != 0 ? moved.X : -moved.X);
            size.X = Limit(Snap(wide, snap), MinSize.X, MaxSize.X);
        }

        if ((resizeEdges & UIResize.Vertical) != 0)
        {
            float tall = resizeSize.Y + ((resizeEdges & UIResize.Top) != 0 ? moved.Y : -moved.Y);
            size.Y = Limit(Snap(tall, snap), MinSize.Y, MaxSize.Y);
        }

        if (size == Size)
            return false;

        Size = size;
        OnResized?.Invoke(size);
        InvokeScript(resizedHandler, new Vector2Value(size));
        return true;
    }

    private static float Snap(float value, float to) => to > 0.0f ? MathF.Round(value / to) * to : value;

    /// <summary>The edge was let go of.</summary>
    public void EndResize()
    {
        if (!IsResizing)
            return;

        IsResizing = false;
        ActiveGrip = UIResize.None;
        OnResizeEnded?.Invoke(Size);
    }

    private static float Limit(float value, float least, float most)
    {
        // Never down to nothing, a component nobody can see has no edge left to drag it back out by
        value = MathF.Max(value, MathF.Max(least, 8.0f));
        return most > 0.0f ? MathF.Min(value, most) : value;
    }

    /// <summary>
    /// Helper method to draw the tabs of the edges that can be dragged, over the component and what is in it. A
    /// short bar in the middle of each, and the whole edge lit while the pointer is on it or has hold of it.
    /// </summary>
    private void PaintGrips(UIDrawList list) => PaintGrips(list, Resizable, ActiveGrip);

    /// <summary>
    /// Helper method to draw the tabs of some edges whether they can be dragged or not, for whoever marks a
    /// component out from outside (the compositor, for an editor).
    /// </summary>
    internal void PaintGrips(UIDrawList list, UIResize edges, UIResize held)
    {
        UISkin skin = list.Skin;
        UIRect bounds = Bounds;

        // Something to see on a dark panel and on a light one, the colour text is written in
        Vector4 quiet = skin.TextColor with { W = 0.28f };
        Vector4 lit = skin.AccentColor with { W = 1.0f };

        for (int i = 0; i < 4; i++)
        {
            UIResize edge = (UIResize)(1 << i);
            if ((edges & edge) == 0)
                continue;

            bool active = (held & edge) != 0;
            bool upright = (edge & UIResize.Horizontal) != 0;

            // Where the edge is, as a line down or across the middle of it
            float along = upright ? bounds.Height : bounds.Width;
            float tab = MathF.Min(TAB_LENGTH, along * 0.5f);
            Vector2 middle = edge switch
            {
                UIResize.Left => new Vector2(bounds.Min.X, bounds.Center.Y),
                UIResize.Right => new Vector2(bounds.Max.X, bounds.Center.Y),
                UIResize.Top => new Vector2(bounds.Center.X, bounds.Max.Y),
                _ => new Vector2(bounds.Center.X, bounds.Min.Y)
            };

            // Just inside of the edge, so a panel against the side of the screen still shows its tab
            Vector2 inward = edge switch
            {
                UIResize.Left => new Vector2(TAB_THICKNESS * 0.5f + 1.0f, 0.0f),
                UIResize.Right => new Vector2(-TAB_THICKNESS * 0.5f - 1.0f, 0.0f),
                UIResize.Top => new Vector2(0.0f, -TAB_THICKNESS * 0.5f - 1.0f),
                _ => new Vector2(0.0f, TAB_THICKNESS * 0.5f + 1.0f)
            };

            if (active)
            {
                Vector2 whole = upright ? new Vector2(2.0f, along) : new Vector2(along, 2.0f);
                list.Rect(UIRect.FromCenter(middle + inward * 0.5f, whole), lit);
            }

            Vector2 size = upright ? new Vector2(TAB_THICKNESS, tab) : new Vector2(tab, TAB_THICKNESS);
            list.Box(UIRect.FromCenter(middle + inward, size), active ? lit : quiet);
        }
    }

    /// <summary>Helper method to declare what a layout file can say about dragging the edges.</summary>
    private void DefineResizeScript()
    {
        Expose(
            "resize",
            () => new StringValue(Describe(Resizable)),
            value => Resizable = ReadResize(UIScript.ToText(value, "resize")));
        Expose("min_size", () => MinSize, value => MinSize = value);
        Expose("max_size", () => MaxSize, value => MaxSize = value);
        Expose("on_resize", () => resizedHandler ?? new NullValue(), value => resizedHandler = value);
    }

    private static string Describe(UIResize edges)
    {
        if (edges == UIResize.None) return "none";
        if (edges == UIResize.All) return "all";

        string text = string.Empty;
        foreach (UIResize edge in (ReadOnlySpan<UIResize>)[UIResize.Left, UIResize.Right, UIResize.Top, UIResize.Bottom])
        {
            if ((edges & edge) != 0)
                text += (text.Length > 0 ? ", " : string.Empty) + edge.ToString().ToLowerInvariant();
        }

        return text;
    }

    private static UIResize ReadResize(string text)
    {
        UIResize edges = UIResize.None;
        foreach (string word in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            edges |= word.ToLowerInvariant() switch
            {
                "none" => UIResize.None,
                "left" => UIResize.Left,
                "right" => UIResize.Right,
                "top" => UIResize.Top,
                "bottom" => UIResize.Bottom,
                "horizontal" => UIResize.Horizontal,
                "vertical" => UIResize.Vertical,
                "all" => UIResize.All,
                _ => throw new Exception($"resize is none, left, right, top, bottom, horizontal, vertical or all, or several of them with commas, not '{word}'.")
            };
        }

        return edges;
    }
}
