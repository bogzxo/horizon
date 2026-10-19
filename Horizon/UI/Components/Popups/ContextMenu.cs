using Horizon.Rendering;
using System.Numerics;

using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// The list that pops up where you right click, with cut, copy, delete, bring to front, that sort of thing. It's the same
/// <see cref="Menu"/> a <see cref="MenuBar"/> opens, so whatever builds one builds the other, icons, menus inside of
/// it and all.
/// <code>
/// row.OnContextMenu = point =>
/// {
///     var menu = new Menu("row");
///     menu.Add("Rename", Rename, "F2", icon: "edit");
///     menu.AddSeparator();
///     menu.Add("Delete", Delete, "Del", icon: "trash");
///     ContextMenu.Show(row.Module!, point, menu);
/// };
/// </code>
/// It sits on top of everything in its module until something is picked, or you click anywhere else. One per module,
/// showing another just swaps what's in it. Nothing about it goes into a layout file.
/// </summary>
public sealed class ContextMenu : UIComponent
{
    private Menu? menu;
    private Vector2 corner;
    private MenuPanel? panel;

    /// <summary>The menu that is showing, null when it's shut.</summary>
    public Menu? Showing => IsPopupOpen ? menu : null;

    /// <summary>The menu open off one of its items (the deepest, when they go further), null for none.</summary>
    public Menu? OpenSubmenu => Panel?.OpenSubmenu;

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
        shown.panel = null;
        shown.OpenPopup();
        return shown;
    }

    /// <inheritdoc cref="Show(UIModule, Vector2, Menu)"/>
    /// <param name="world">Where, in the camera's world (what <see cref="UICompositor.ContextRequested"/> hands you).</param>
    public static ContextMenu ShowAt(UIModule module, Vector2 world, Menu menu) => Show(module, module.ToLocal(world), menu);

    /// <summary>Shuts the menu without picking anything.</summary>
    // The list that was showing as it was put away, painted going, see PaintClosing
    private MenuPanel? closingPanel;

    protected internal override void PopupClosed()
    {
        closingPanel = panel ?? closingPanel;
        panel = null;
    }

    protected internal override bool PaintClosing(UIDrawList list, ref float closedAt)
    {
        if (closingPanel is not { } going || !list.BeginExit(ref closedAt, new Vector2(going.Bounds.Min.X, going.Bounds.Max.Y), new Vector2(0.0f, 6.0f)))
        {
            closingPanel = null;
            return false;
        }

        // Nothing under the pointer, it's going
        going.Paint(list, new Vector2(float.NegativeInfinity), Glass ?? Module?.Compositor.PopupGlass);
        list.EndExit();
        return true;
    }

    public void Close()
    {
        closingPanel = panel ?? closingPanel;
        panel = null;
        ClosePopup();
    }

    private UISkin? Skin => Module?.Compositor.Skin;

    private MenuPanel? Panel
    {
        get
        {
            if (Showing is not { } shown || Skin is not { } skin || Module is not { } module)
                return null;

            panel ??= new MenuPanel(shown, skin, TextScale, corner, module.Frame);
            return panel;
        }
    }

    /// <summary>Where the list is, in the module's units.</summary>
    public UIRect ListBounds => Panel?.Bounds ?? default;

    /// <summary>Where an item of the showing menu is, in the module's units.</summary>
    public UIRect ItemBounds(int index) => Skin is { } skin && Panel is { } shown ? shown.ItemBounds(skin, index) : default;

    /// <summary>Where an item of the showing menu, or of a menu open off it, is by what it says.</summary>
    public UIRect ItemBounds(string label) => Skin is { } skin && Panel is { } shown ? shown.ItemBounds(skin, label) : default;

    /// <summary>Opens the menu of one of its items, by what it says, as resting the pointer on it would.</summary>
    public bool OpenSubmenuOf(string label) =>
        Skin is { } skin && Module is { } module && Panel is { } shown && shown.OpenSubmenuOf(skin, module.Frame, label);

    protected internal override bool PopupContains(Vector2 point) => Panel?.Contains(point) ?? false;

    protected internal override void PaintPopup(UIDrawList list)
    {
        if (Panel is not { } shown || Module is not { } module)
            return;

        Vector2 pointer = module.ToLocal(module.Compositor.Pointer.Position);
        shown.Track(list.Skin, module.Frame, pointer);
        shown.Paint(list, pointer, Glass ?? module.Compositor.PopupGlass);
    }

    protected internal override void OnPointerUp(Vector2 point)
    {
        if (Panel is not { } shown || Skin is not { } skin)
            return;

        MenuItem? item = shown.Pick(skin, point, out bool inside);

        // A separator, a menu of its own or something that can't be done right now leaves it open
        if (item is null && inside)
            return;

        Close();
        item?.OnPicked?.Invoke();
    }
}
