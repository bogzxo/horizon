using Horizon.HIDL.Runtime;
using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Horizon.Tests.UI;

/// <summary>What scripts put on screen kept together, and what's said when a layout of theirs doesn't build.</summary>
public class UIScriptedScreensTests
{
    private sealed class Script : IUIScriptOwner
    {
        public string Name => "script";
        public string? Failed { get; set; }

        public bool Invoke(IRuntimeValue function, IRuntimeValue[] arguments, out IRuntimeValue returned)
        {
            returned = Values.Null;
            return true;
        }
    }

    private sealed class File;

    private static readonly string Missing = Path.Combine(Path.GetTempPath(), "no such layout here.hor");

    [Fact]
    public void A_view_whose_layout_doesnt_build_is_said_and_leaves_nothing_behind()
    {
        var said = new List<string>();
        var screens = new UIScriptedScreens<File>(null!, said.Add, _ => { });
        var views = new Panel();
        var of = new File();

        Assert.Null(screens.MakeView(new Script(), "notes", of, Missing, views, null));
        Assert.Single(said);
        Assert.Contains("script", said[0]);
        Assert.Empty(views.Children);
        Assert.Null(screens.View(of, "notes"));
        Assert.False(screens.Any);
    }

    [Fact]
    public void A_page_whose_layout_doesnt_build_has_no_tab_and_changes_nothing()
    {
        var said = new List<string>();
        var screens = new UIScriptedScreens<File>(null!, said.Add, _ => { });
        var tabs = new TabPanel();
        bool changed = false;

        Assert.Null(screens.MakePage(new Script(), "NOTES", Missing, tabs, () => changed = true));
        Assert.Single(said);
        Assert.Empty(tabs.Children);
        Assert.False(changed);
        Assert.Empty(screens.Pages);
    }

    [Fact]
    public void Forgetting_with_nothing_made_hands_back_nothing()
    {
        var screens = new UIScriptedScreens<File>(null!, _ => { }, _ => { });
        Assert.Empty(screens.Forget(new Script()));
        screens.Forget(new File());
        screens.Update(0.1f, null, null);
        Assert.False(screens.Press(null, "up"));
    }
}
