using System.Numerics;

namespace Horizon.UI.Components;

// More tabs than the strip has room for. The strip scrolls them along with the wheel, which nobody finds out by
// looking, so a chevron comes up before the plus while they don't all fit and lists every tab, the open one ticked,
// the way an editor's list of open files does.
public partial class TerminalPanel
{
    private Button? more;

    /// <summary>Whether the tabs don't all fit and the chevron that lists them is up, for a test.</summary>
    public bool TabsOverflow => more?.Visible == true;

    /// <summary>Where the chevron that lists the tabs is, nothing while they all fit.</summary>
    public UIRect TabsListBounds => TabsOverflow ? more!.Bounds : default;

    private void BuildOverflow()
    {
        more = Add(new Button { Icon = "chevron-down", Tooltip = "Every tab", Style = "button_flat", Visible = false });
        more.OnPressed = ShowAllTabs;
    }

    // Every tick, the chevron up while the tabs are wider than the room the strip gets without it
    private void UpdateOverflow()
    {
        if (more is null)
            return;

        float room = Bounds.Width - strip.DesiredSize.Y * (profilePicker.Visible ? 2.0f : 1.0f);
        bool overflowing = tabs.Count > 1 && strip.TabsWidth > room + 0.5f;
        if (overflowing != more.Visible)
        {
            more.Visible = overflowing;
            RequestFrame();
        }
    }

    /// <summary>Every tab as a menu at the chevron, the open one ticked, a pick opens it.</summary>
    public void ShowAllTabs()
    {
        if (Module is not { } module || more is null)
            return;

        var menu = new Menu("tabs");
        for (int i = 0; i < tabs.Count; i++)
        {
            TerminalTab tab = tabs[i];
            MenuItem item = menu.Add(tab.Title, () => Select(tab), icon: tab.Profile?.Icon ?? "terminal");
            item.IsChecked = () => current == tab;
        }

        ContextMenu.Show(module, new Vector2(more.Bounds.Min.X, more.Bounds.Min.Y), menu);
    }
}
