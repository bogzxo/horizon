using System.Numerics;

using Horizon.Rendering.UIX.Drawing;
using Horizon.Rendering.UIX.Skinning;

namespace Horizon.Rendering.UIX.Components;

/// <summary>
/// The list that pops up where you right click: cut, copy, delete, bring to front, that sort of thing. It's the same
/// <see cref="Menu"/> a <see cref="MenuBar"/> opens, so whatever builds one builds the other.
/// <code>
/// row.OnContextMenu = point =>
/// {
///     var menu = new Menu("row");
///     menu.Add("Rename", Rename, "F2");
///     menu.AddSeparator();
///     menu.Add("Delete", Delete, "Del");
///     ContextMenu.Show(row.Module!, point, menu);
/// };
/// </code>
/// It sits on top of everything in its module until something is picked, or you click anywhere else. One per module,
/// showing another just swaps what's in it. Nothing about it goes into a layout file.
/// </summary>
public sealed class ContextMenu : UIComponent
{
    private const float BORDER = 2.0f;
    private const float ITEM_PADDING = 12.0f;
    private const float CHECK_WIDTH = 16.0f;
    private const float SHORTCUT_GAP = 28.0f;
    private const float SEPARATOR_SHARE = 0.4f;

    private Menu? menu;
    private Vector2 corner;

    /// <summary>The menu that is showing, null when it's shut.</summary>
    public Menu? Showing => IsPopupOpen ? menu : null;

    /// <summary>How big its text is, 0 for the skin's.</summary>
    public float TextScale { get; set; }

    /// <summary>
    /// Shows a menu with its top left corner at a point of a module, in the module's units (what
    /// <see cref="OnContextMenu"/> hands you). Kept inside the module's frame so it never hangs off the edge.
    /// </summary>
    public static ContextMenu Show(UIModule module, Vector2 point, Menu menu)
    {
        var shown = module.Root.Children.OfType<ContextMenu>().FirstOrDefault() ?? module.AddComponent(new ContextMenu());

        shown.menu = menu;
        shown.corner = point;
        shown.OpenPopup();
        return shown;
    }

    /// <inheritdoc cref="Show(UIModule, Vector2, Menu)"/>
    /// <param name="world">Where, in the camera's world (what <see cref="UICompositor.ContextRequested"/> hands you).</param>
    public static ContextMenu ShowAt(UIModule module, Vector2 world, Menu menu) => Show(module, module.ToLocal(world), menu);

    /// <summary>Shuts the menu without picking anything.</summary>
    public void Close() => ClosePopup();

    private float TextScaleOf(UISkin skin) => TextScale > 0.0f ? TextScale : skin.TextScale;

    private float RowHeight(UISkin skin) => MathF.Round(skin.Font.LineHeight * TextScaleOf(skin) + skin.ButtonPadding.Total.Y * 0.6f);

    /// <summary>Where the list is, in the module's units.</summary>
    public UIRect ListBounds
    {
        get
        {
            if (Showing is not { } shown || Module?.Compositor.Skin is not { } skin)
                return default;

            float scale = TextScaleOf(skin), row = RowHeight(skin);
            float width = 0.0f, height = 0.0f;

            foreach (MenuItem item in shown.Items)
            {
                height += item.IsSeparator ? MathF.Round(row * SEPARATOR_SHARE) : row;

                float shortcut = item.Shortcut.Length > 0 ? skin.Font.Measure(item.Shortcut, scale, markup: false).X + SHORTCUT_GAP : 0.0f;
                width = MathF.Max(width, CHECK_WIDTH + skin.Font.Measure(item.Label, scale, markup: false).X + shortcut + ITEM_PADDING * 2.0f);
            }

            Vector2 size = new(width + BORDER * 2.0f, height + BORDER * 2.0f);

            // Down and to the right of the click, pushed back in if that runs off the frame (Y is up)
            UIRect frame = Module.Frame;
            float left = MathF.Min(corner.X, frame.Max.X - size.X);
            float top = MathF.Max(corner.Y, frame.Min.Y + size.Y);
            left = MathF.Max(left, frame.Min.X);
            top = MathF.Min(top, frame.Max.Y);

            return new UIRect(new Vector2(left, top - size.Y), new Vector2(left + size.X, top));
        }
    }

    /// <summary>Where an item of the showing menu is, in the module's units.</summary>
    public UIRect ItemBounds(int index)
    {
        if (Showing is not { } shown || Module?.Compositor.Skin is not { } skin || index < 0 || index >= shown.Items.Count)
            return default;

        UIRect list = ListBounds.Shrink(new UIEdges(BORDER));
        float row = RowHeight(skin);
        float top = list.Max.Y;

        for (int i = 0; i < index; i++)
            top -= shown.Items[i].IsSeparator ? MathF.Round(row * SEPARATOR_SHARE) : row;

        float height = shown.Items[index].IsSeparator ? MathF.Round(row * SEPARATOR_SHARE) : row;
        return new UIRect(new Vector2(list.Min.X, top - height), new Vector2(list.Max.X, top));
    }

    /// <inheritdoc cref="ItemBounds(int)"/>
    public UIRect ItemBounds(string label)
    {
        if (Showing is not { } shown)
            return default;

        for (int i = 0; i < shown.Items.Count; i++)
        {
            if (shown.Items[i].Label == label)
                return ItemBounds(i);
        }

        return default;
    }

    protected internal override bool PopupContains(Vector2 point) => Showing is not null && ListBounds.Contains(point);

    protected internal override void PaintPopup(UIDrawList list)
    {
        if (Showing is not { } shown)
            return;

        UISkin skin = list.Skin;
        float scale = TextScaleOf(skin);
        UIRect bounds = ListBounds;

        // Solid, nothing under it may show through
        list.Box(bounds, skin.PanelColor with { W = 1.0f });
        list.Frame(bounds, BORDER, skin.AccentColor);

        Vector2 pointer = Module is { } module ? module.ToLocal(module.Compositor.Pointer.Position) : default;
        Vector4 dim = skin.TextColor * skin.DisabledTint;

        for (int i = 0; i < shown.Items.Count; i++)
        {
            MenuItem item = shown.Items[i];
            UIRect area = ItemBounds(i);

            if (item.IsSeparator)
            {
                float middle = MathF.Round(area.Center.Y);
                list.Rect(new UIRect(new Vector2(area.Min.X + ITEM_PADDING, middle), new Vector2(area.Max.X - ITEM_PADDING, middle + 1.0f)), dim);
                continue;
            }

            bool enabled = item.Enabled;
            if (enabled && area.Contains(pointer))
                list.Rect(area, skin.HoverColor);

            UIRect content = area.Shrink(new UIEdges(ITEM_PADDING, 0.0f));
            if (item.IsChecked?.Invoke() == true)
            {
                float side = MathF.Round(area.Height * 0.3f);
                Vector2 mark = new(content.Min.X, MathF.Round(content.Center.Y - side * 0.5f));
                list.Rect(new UIRect(mark, mark + new Vector2(side)), skin.AccentColor);
            }

            UIRect text = new(new Vector2(content.Min.X + CHECK_WIDTH, content.Min.Y), content.Max);
            list.Text(item.Label, text, Origin.Left, scale, enabled ? skin.TextColor : dim, markup: false);

            if (item.Shortcut.Length > 0)
                list.Text(item.Shortcut, text, Origin.Right, scale, dim, markup: false);
        }
    }

    protected internal override void OnPointerUp(Vector2 point)
    {
        if (Showing is not { } shown)
            return;

        for (int i = 0; i < shown.Items.Count; i++)
        {
            if (!ItemBounds(i).Contains(point))
                continue;

            MenuItem item = shown.Items[i];

            // A separator or something that can't be done right now leaves it open
            if (!item.Enabled)
                return;

            ClosePopup();
            item.OnPicked?.Invoke();
            return;
        }

        ClosePopup();
    }
}
