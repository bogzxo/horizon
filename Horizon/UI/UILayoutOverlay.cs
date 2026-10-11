using System.Numerics;

using Horizon.UI.Components;
using Horizon.UI.Drawing;

namespace Horizon.UI;

/// <summary>
/// Draws the layout of a UI over it, as <see cref="UILayoutOverlayOptions"/> ask for it, the edges of every
/// component, the padding inside of them and the gaps stacks leave between their children. It is painted into
/// the same list as the UI itself, after it, so it lines up with what is on screen whatever the camera does.
/// </summary>
internal static class UILayoutOverlay
{
    private const float LINE = 1.0f;
    private const float LABEL_SCALE = 0.2f;

    private static readonly Vector4 PaddingColor = new(0.3f, 0.85f, 0.4f, 0.3f);
    private static readonly Vector4 SpacingColor = new(1.0f, 0.6f, 0.15f, 0.38f);
    private static readonly Vector4 HoverColor = new(1.0f, 1.0f, 1.0f, 1.0f);
    private static readonly Vector4 HiddenColor = new(0.6f, 0.6f, 0.6f, 0.5f);
    private static readonly Vector4 SelectedColor = new(1.0f, 0.85f, 0.2f, 1.0f);
    private static readonly Vector4 LabelBack = new(0.02f, 0.02f, 0.05f, 0.92f);

    // One colour per level of the tree, so a component can be told from the one it is in.
    private static readonly Vector4[] DepthColors =
    [
        new(0.2f, 0.85f, 1.0f, 0.85f),
        new(1.0f, 0.35f, 0.85f, 0.85f),
        new(1.0f, 0.9f, 0.25f, 0.85f),
        new(0.45f, 1.0f, 0.55f, 0.85f),
        new(1.0f, 0.55f, 0.3f, 0.85f)
    ];

    /// <summary>
    /// Paints the layout of a module. The list has to have the transform of the module set already.
    /// </summary>
    /// <param name="pointer">Where the pointer is, in the space the module is laid out in.</param>
    public static void Paint(UIDrawList list, UIModule module, UILayoutOverlayOptions options, Vector2 pointer)
    {
        UIComponent? under = FindUnder(module.Root, pointer, options.ShowHidden);

        foreach (var component in module.Root.Children)
            PaintTree(list, component, options, under, 0);

        if (options.ShowLabels && under is not null)
            PaintLabel(list, under);
    }

    /// <summary>
    /// The innermost component a point is in. Unlike the hit test this finds everything, a label as much as a
    /// button. What is wanted here is what is drawn there, not what reacts to it.
    /// </summary>
    public static UIComponent? FindUnder(UIComponent component, Vector2 point, bool hidden)
    {
        if ((!component.Visible || component.IsOnHiddenLayer) && !hidden)
            return null;

        // The pointer is where things are drawn, which for a component that is being animated isn't where
        // the layout has them.
        if (component.VisualOffset != Vector2.Zero || component.VisualScale != Vector2.One)
        {
            if (component.VisualScale.X == 0.0f || component.VisualScale.Y == 0.0f)
                return null;

            point = component.Bounds.Center + (point - component.VisualOffset - component.Bounds.Center) / component.VisualScale;
        }

        var children = component.Children;
        for (int i = children.Count - 1; i >= 0; i--)
        {
            if ((hidden || component.ShowsChild(children[i])) && FindUnder(children[i], point, hidden) is { } found)
                return found;
        }

        // The root of a module is the whole screen, which is never what anybody is pointing at.
        return component.Parent is not null && component.Bounds.Contains(point) ? component : null;
    }

    private static void PaintTree(UIDrawList list, UIComponent component, UILayoutOverlayOptions options, UIComponent? under, int depth)
    {
        if (!component.Visible && !options.ShowHidden)
            return;

        // Drawn the way the component itself is, so the overlay follows whatever is animating it.
        bool animated = component.VisualOffset != Vector2.Zero || component.VisualScale != Vector2.One;
        if (animated)
            list.PushVisual(component.VisualOffset, component.VisualScale, component.Bounds.Center, 1.0f);

        bool wanted = !options.HoveredOnly || IsInside(under, component);
        if (wanted && !component.Bounds.IsEmpty)
        {
            if (options.ShowPadding)
                PaintPadding(list, component);

            if (options.ShowSpacing && component is StackPanel stack)
                PaintSpacing(list, stack, options.ShowHidden);

            if (options.ShowBounds)
            {
                Vector4 color = !component.Visible ? HiddenColor
                    : component == under ? HoverColor
                    : DepthColors[depth % DepthColors.Length];

                list.Outline(component.Bounds, component == under ? LINE * 2.0f : LINE, color);
            }
        }

        foreach (var child in component.Children)
            PaintTree(list, child, options, under, depth + 1);

        if (animated)
            list.PopVisual();
    }

    /// <summary>The four strips between the edges of a component and where its children are allowed.</summary>
    private static void PaintPadding(UIDrawList list, UIComponent component)
    {
        UIEdges padding = component.Padding;
        if (padding == default)
            return;

        UIRect outer = component.Bounds;
        UIRect inner = outer.Shrink(padding);

        // Top and bottom run the whole width, left and right fill in what is between them.
        list.Rect(new UIRect(new Vector2(outer.Min.X, inner.Max.Y), outer.Max), PaddingColor);
        list.Rect(new UIRect(outer.Min, new Vector2(outer.Max.X, inner.Min.Y)), PaddingColor);
        list.Rect(new UIRect(new Vector2(outer.Min.X, inner.Min.Y), new Vector2(inner.Min.X, inner.Max.Y)), PaddingColor);
        list.Rect(new UIRect(new Vector2(inner.Max.X, inner.Min.Y), new Vector2(outer.Max.X, inner.Max.Y)), PaddingColor);
    }

    /// <summary>What is between every two children of a stack that follow each other, across the whole stack.</summary>
    private static void PaintSpacing(UIDrawList list, StackPanel stack, bool hidden)
    {
        UIRect content = stack.Bounds.Shrink(stack.Padding);
        UIComponent? previous = null;

        foreach (var child in stack.Children)
        {
            // A hidden child takes up no room in a stack, so there is no gap next to it either.
            if (!child.Visible)
                continue;

            if (previous is not null)
            {
                UIRect gap = stack.Direction == UIDirection.Vertical
                    ? new UIRect(new Vector2(content.Min.X, child.Bounds.Max.Y), new Vector2(content.Max.X, previous.Bounds.Min.Y))
                    : new UIRect(new Vector2(previous.Bounds.Max.X, content.Min.Y), new Vector2(child.Bounds.Min.X, content.Max.Y));

                if (!gap.IsEmpty)
                    list.Rect(gap, SpacingColor);
            }

            previous = child;
        }
    }

    /// <summary>
    /// Marks one component out from the rest. A bright edge, its padding, and what it is written above it.
    /// For whatever an editor has selected.
    /// </summary>
    public static void PaintHighlight(UIDrawList list, UIComponent component)
    {
        // Every component it is inside of may be animating, the mark has to go where the component is drawn.
        var chain = new List<UIComponent>();
        for (UIComponent? at = component; at is not null; at = at.Parent)
            chain.Add(at);

        int pushed = 0;
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            UIComponent at = chain[i];
            if (at.VisualOffset == Vector2.Zero && at.VisualScale == Vector2.One)
                continue;

            list.PushVisual(at.VisualOffset, at.VisualScale, at.Bounds.Center, 1.0f);
            pushed++;
        }

        PaintPadding(list, component);
        list.Outline(component.Bounds, LINE * 2.0f, SelectedColor);
        PaintLabel(list, component);

        for (int i = 0; i < pushed; i++)
            list.PopVisual();
    }

    /// <summary>What a component is and how big, written just above it (or inside, at the top of the screen).</summary>
    private static void PaintLabel(UIDrawList list, UIComponent component)
    {
        UIRect bounds = component.Bounds;
        UIEdges padding = component.Padding;

        string text = $"{component.GetType().Name}  {bounds.Width:0} x {bounds.Height:0}";
        if (padding != default)
            text += $"  pad {padding.Left:0} {padding.Top:0} {padding.Right:0} {padding.Bottom:0}";
        if (component is StackPanel stack && stack.Children.Count > 1)
            text += $"  gap {GapOf(stack):0}";

        Vector2 size = list.Skin.Font.Measure(text, LABEL_SCALE, markup: false);
        Vector2 margin = new(6.0f, 4.0f);

        // Its bottom left corner sits on the top left corner of the component.
        Vector2 corner = new(bounds.Min.X, bounds.Max.Y + 2.0f);
        UIRect back = new(corner, corner + size + margin * 2.0f);

        list.Rect(back, LabelBack);
        list.Text(text, new Vector2(back.Min.X + margin.X, back.Max.Y - margin.Y), LABEL_SCALE, HoverColor, markup: false);
    }

    /// <summary>The gap a stack ended up with, measured off its first two visible children.</summary>
    private static float GapOf(StackPanel stack)
    {
        UIComponent? previous = null;

        foreach (var child in stack.Children)
        {
            if (!child.Visible)
                continue;

            if (previous is not null)
            {
                return stack.Direction == UIDirection.Vertical
                    ? previous.Bounds.Min.Y - child.Bounds.Max.Y
                    : child.Bounds.Min.X - previous.Bounds.Max.X;
            }

            previous = child;
        }

        return 0.0f;
    }

    private static bool IsInside(UIComponent? inner, UIComponent outer)
    {
        for (UIComponent? component = inner; component is not null; component = component.Parent)
        {
            if (component == outer)
                return true;
        }

        return false;
    }
}
