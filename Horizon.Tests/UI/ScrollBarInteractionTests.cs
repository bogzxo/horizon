using System.Numerics;

using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.Tests.UI;

public sealed class ScrollBarInteractionTests
{
    private static readonly UISkin Skin = new(new UIFont(Path.GetTempPath(), "no-font-here.ttf"));

    private static void Paint(UIComponent component, UIRect bounds)
    {
        component.MeasureTree(Skin);
        component.ArrangeTree(bounds);
        var list = new UIDrawList();
        list.Begin(Skin, 1, 1.0f / 60.0f, 1.0f / 60.0f);
        component.PaintTree(list);
        list.End();
    }

    [Fact]
    public void A_list_scrollbar_can_be_clicked_and_dragged()
    {
        var list = new ListBox { Rows = 4, Items = [.. Enumerable.Range(0, 30).Select(i => $"item {i}")] };
        Paint(list, UIRect.FromCenter(Vector2.Zero, new Vector2(260, 120)));

        UIRect track = list.ScrollBarTrack;
        Assert.False(track.IsEmpty);
        Assert.True(list.ScrollBarThumb.Height >= UIScrollBar.MIN_THUMB);
        Vector2 click = new(track.Center.X, track.Min.Y + 3.0f);
        Assert.Same(list, list.HitTest(click));
        list.OnPointerDown(click);
        list.OnPointerUp(click);
        Assert.True(list.Top > 0);

        Paint(list, UIRect.FromCenter(Vector2.Zero, new Vector2(260, 120)));
        UIRect thumb = list.ScrollBarThumb;
        Vector2 grab = thumb.Center;
        list.OnPointerDown(grab);
        list.OnPointerDrag(new Vector2(grab.X, list.ScrollBarTrack.Max.Y - thumb.Height * 0.5f));
        list.OnPointerUp(grab);

        Assert.Equal(0, list.Top);
    }

    [Fact]
    public void A_tree_scrollbar_can_be_clicked_and_dragged()
    {
        var tree = new TreeView
        {
            RowHeight = 20.0f,
            Items = [.. Enumerable.Range(0, 40).Select(i => new UITreeItem($"item {i}"))]
        };
        Paint(tree, UIRect.FromCenter(Vector2.Zero, new Vector2(300, 120)));

        UIRect track = tree.ScrollBarTrack;
        Assert.False(track.IsEmpty);
        Assert.True(tree.ScrollBarThumb.Height >= UIScrollBar.MIN_THUMB);
        Vector2 click = new(track.Center.X, track.Min.Y + 3.0f);
        Assert.Same(tree, tree.HitTest(click));
        tree.OnPointerDown(click);
        tree.OnPointerUp(click);
        Assert.True(tree.ScrollOffset > 0.0f);

        Paint(tree, UIRect.FromCenter(Vector2.Zero, new Vector2(300, 120)));
        UIRect thumb = tree.ScrollBarThumb;
        Vector2 grab = thumb.Center;
        tree.OnPointerDown(grab);
        tree.OnPointerDrag(new Vector2(grab.X, tree.ScrollBarTrack.Max.Y - thumb.Height * 0.5f));
        tree.OnPointerUp(grab);

        Assert.Equal(0.0f, tree.ScrollOffset, 0.01f);
    }
}
